using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class RenumberSheetsHandler : IRevitCommand
    {
        public string Name => "renumber_sheets";
        public string Description => "Bulk renumber/rename sheets with collision preflights and cyclic swap support";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""items"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""sheet_id"": { ""type"": ""integer"" },
          ""sheet_number"": { ""type"": ""string"" },
          ""new_sheet_number"": { ""type"": ""string"" },
          ""new_sheet_name"": { ""type"": ""string"" }
        }
      }
    },
    ""find"": { ""type"": ""string"" },
    ""replace"": { ""type"": ""string"" },
    ""prefix"": { ""type"": ""string"" },
    ""suffix"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }
  }
}";

        private class SheetChange
        {
            public ViewSheet Sheet { get; set; }
            public string OldNumber { get; set; }
            public string NewNumber { get; set; }
            public string OldName { get; set; }
            public string NewName { get; set; }
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            var itemsToken = request["items"];
            var find = request.Value<string>("find") ?? "";
            var replace = request.Value<string>("replace") ?? "";
            var prefix = request.Value<string>("prefix") ?? "";
            var suffix = request.Value<string>("suffix") ?? "";
            var dryRun = request.Value<bool?>("dry_run") ?? request.Value<bool?>("dryRun") ?? true;

            var allSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToList();

            var allSheetsMap = allSheets.ToDictionary(s => RevitCompat.GetId(s.Id));
            var targetChanges = new List<SheetChange>();

            // 1. Explicit Mode: "items" is supplied
            if (itemsToken != null && itemsToken.Type == JTokenType.Array)
            {
                var itemsArray = (JArray)itemsToken;
                foreach (JObject item in itemsArray)
                {
                    var sheetId = item.Value<long?>("sheet_id") ?? item.Value<long?>("sheetId");
                    var sheetNumber = item.Value<string>("sheet_number") ?? item.Value<string>("sheetNumber") ?? "";
                    var newNum = item.Value<string>("new_sheet_number") ?? item.Value<string>("newSheetNumber");
                    var newNameStr = item.Value<string>("new_sheet_name") ?? item.Value<string>("newSheetName");

                    ViewSheet targetSheet = null;
                    if (sheetId.HasValue)
                    {
                        if (!RevitCompat.CanRepresentElementId(sheetId.Value))
                            return CommandResult.Fail(RevitCompat.ElementIdRangeError(sheetId.Value));

                        allSheetsMap.TryGetValue(sheetId.Value, out targetSheet);
                    }
                    else if (!string.IsNullOrEmpty(sheetNumber))
                    {
                        targetSheet = allSheets.FirstOrDefault(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));
                    }

                    if (targetSheet == null)
                        return CommandResult.Fail($"Could not resolve sheet from item (sheet_id: {sheetId}, sheet_number: {sheetNumber}).");

                    var oldNum = targetSheet.SheetNumber;
                    var oldName = targetSheet.Name;

                    var computedNewNum = !string.IsNullOrEmpty(newNum) ? newNum.Trim() : oldNum;
                    var computedNewName = newNameStr != null ? newNameStr.Trim() : oldName;

                    if (string.IsNullOrEmpty(computedNewNum))
                        return CommandResult.Fail($"New sheet number cannot be empty for sheet ID {RevitCompat.GetId(targetSheet.Id)}.");

                    targetChanges.Add(new SheetChange
                    {
                        Sheet = targetSheet,
                        OldNumber = oldNum,
                        NewNumber = computedNewNum,
                        OldName = oldName,
                        NewName = computedNewName
                    });
                }
            }
            // 2. Transform Mode: "items" is omitted
            else if (itemsToken != null && itemsToken.Type != JTokenType.Null)
            {
                return CommandResult.Fail("items must be a JSON array when supplied.");
            }
            // 3. Transform Mode: "items" is omitted
            else
            {
                if (string.IsNullOrEmpty(find) && string.IsNullOrEmpty(replace) && string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix))
                    return CommandResult.Fail("No renumbering rules (find/replace, prefix, suffix) or explicit items were supplied.");

                foreach (var sheet in allSheets)
                {
                    var oldNum = sheet.SheetNumber;
                    var oldName = sheet.Name;

                    var computedNewNum = oldNum;
                    if (!string.IsNullOrEmpty(find))
                    {
                        computedNewNum = System.Text.RegularExpressions.Regex.Replace(computedNewNum, System.Text.RegularExpressions.Regex.Escape(find), replace, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }
                    computedNewNum = prefix + computedNewNum + suffix;

                    if (string.IsNullOrEmpty(computedNewNum))
                        return CommandResult.Fail($"Transform rules resulted in empty sheet number for sheet {oldNum}.");

                    targetChanges.Add(new SheetChange
                    {
                        Sheet = sheet,
                        OldNumber = oldNum,
                        NewNumber = computedNewNum,
                        OldName = oldName,
                        NewName = oldName
                    });
                }
            }

            var effectiveChanges = targetChanges.Where(c => c.OldNumber != c.NewNumber || c.OldName != c.NewName).ToList();
            if (effectiveChanges.Count == 0)
                return CommandResult.Fail("No effective sheet number or name changes were requested.");

            // 3. Collision Preflights
            var conflicts = new List<object>();

            // Duplicate source sheets in explicit items can otherwise be applied sequentially.
            var duplicateSources = effectiveChanges
                .GroupBy(c => RevitCompat.GetId(c.Sheet.Id))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var dupGroup in duplicateSources)
            {
                foreach (var change in dupGroup)
                {
                    conflicts.Add(new
                    {
                        sheet_id = RevitCompat.GetId(change.Sheet.Id),
                        sheet_number = change.OldNumber,
                        reason = "Duplicate source sheet in the requested change set."
                    });
                }
            }

            // Duplicate target numbers within target set
            var duplicateTargets = effectiveChanges
                .GroupBy(c => c.NewNumber, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var dupGroup in duplicateTargets)
            {
                foreach (var change in dupGroup)
                {
                    conflicts.Add(new
                    {
                        sheet_id = RevitCompat.GetId(change.Sheet.Id),
                        sheet_number = change.OldNumber,
                        conflict_number = change.NewNumber,
                        reason = "Duplicate sheet number within the requested change set."
                    });
                }
            }

            // Collision with unchanged existing sheets
            var renumberedSheetIds = new HashSet<long>(effectiveChanges.Select(c => RevitCompat.GetId(c.Sheet.Id)));
            var unchangedSheetNumbers = allSheets
                .Where(s => !renumberedSheetIds.Contains(RevitCompat.GetId(s.Id)))
                .Select(s => s.SheetNumber)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var change in effectiveChanges)
            {
                if (unchangedSheetNumbers.Contains(change.NewNumber))
                {
                    conflicts.Add(new
                    {
                        sheet_id = RevitCompat.GetId(change.Sheet.Id),
                        sheet_number = change.OldNumber,
                        conflict_number = change.NewNumber,
                        reason = "Collision with an existing sheet number that is not being renumbered."
                    });
                }
            }

            var changesList = effectiveChanges.Select(c => new
            {
                sheet_id = RevitCompat.GetId(c.Sheet.Id),
                old_sheet_number = c.OldNumber,
                new_sheet_number = c.NewNumber,
                old_sheet_name = c.OldName,
                new_sheet_name = c.NewName
            }).ToList();

            if (conflicts.Count > 0)
            {
                return CommandResult.Ok(new
                {
                    dry_run = dryRun,
                    updated = false,
                    count = 0,
                    changes = new List<object>(),
                    conflicts = conflicts,
                    error = "Renumbering conflicts were detected."
                });
            }

            if (dryRun)
            {
                return CommandResult.Ok(new
                {
                    dry_run = true,
                    updated = false,
                    count = changesList.Count,
                    changes = changesList,
                    conflicts = new List<object>(),
                    error = (string)null
                });
            }

            // 4. Executing changes using a safe two-pass rename strategy (handles swaps cleanly)
            using (var tx = new Transaction(doc, "RvtMcp: renumber sheets"))
            {
                tx.Start();
                try
                {
                    // Pass 1: Apply temporary numbers to isolate duplicates/swaps
                    for (int i = 0; i < effectiveChanges.Count; i++)
                    {
                        var change = effectiveChanges[i];
                        change.Sheet.SheetNumber = $"TEMP_RENUM_{Guid.NewGuid():N}_{i}";
                    }

                    // Pass 2: Apply final numbers and names
                    foreach (var change in effectiveChanges)
                    {
                        change.Sheet.SheetNumber = change.NewNumber;
                        change.Sheet.Name = change.NewName;
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Ok(new
                    {
                        dry_run = false,
                        updated = false,
                        count = 0,
                        changes = new List<object>(),
                        conflicts = new List<object>(),
                        error = $"Failed to renumber sheets: {ex.Message}"
                    });
                }
            }

            return CommandResult.Ok(new
            {
                dry_run = false,
                updated = true,
                count = changesList.Count,
                changes = changesList,
                conflicts = new List<object>(),
                error = (string)null
            });
        }
    }
}
