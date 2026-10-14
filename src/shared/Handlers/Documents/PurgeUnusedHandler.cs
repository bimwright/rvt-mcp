using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class PurgeUnusedHandler : IRevitCommand
    {
        public string Name => "purge_unused";
        public string Description => "Conservative purge of unused loadable family symbols. MVP scope: targets=['families'] only. Skips symbols with any placed instance OR any reference from tags/schedules/view filters. dry_run defaults to true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""targets"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""families""]}},""dry_run"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":500,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var targets = (req["targets"] as JArray)?.Select(t => t.Value<string>()?.ToLowerInvariant()).ToHashSet()
                ?? new HashSet<string> { "families" };
            var dryRun = req.Value<bool?>("dry_run") ?? true;
            var limit = req.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500)
                return CommandResult.Fail("limit must be between 1 and the hard maximum of 500.");

            if (targets.Any(t => t != "families"))
                return CommandResult.Fail("MVP: only targets=['families'] is supported in this wave.");

            var allSymbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            var usedTypeIds = new HashSet<long>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Select(fi => RevitCompat.GetId(fi.GetTypeId())));

            var referencedIds = CollectReferencedSymbolIds(doc);
            var candidates = allSymbols
                .Where(s => !usedTypeIds.Contains(RevitCompat.GetId(s.Id)))
                .ToList();
            var safeToPurge = candidates
                .Where(s => !referencedIds.Contains(RevitCompat.GetId(s.Id)))
                .Take(limit)
                .ToList();

            var report = safeToPurge.Select(s => new
            {
                id = RevitCompat.GetId(s.Id),
                name = s.Name,
                family = s.Family?.Name,
                category = s.Category?.Name
            }).ToList();

            if (dryRun)
            {
                return CommandResult.Ok(new
                {
                    dry_run = true,
                    total_candidates = candidates.Count,
                    safe_to_purge = report.Count,
                    skipped_due_to_references = candidates.Count - safeToPurge.Count,
                    items = report
                });
            }

            var deleted = 0;
            var failed = 0;
            using (var tx = new Transaction(doc, "RvtMcp: Purge unused family symbols"))
            {
                tx.Start();
                try
                {
                    foreach (var symbol in safeToPurge)
                    {
                        try
                        {
                            doc.Delete(symbol.Id);
                            deleted++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Purge transaction failed: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                dry_run = false,
                total_candidates = candidates.Count,
                attempted = safeToPurge.Count,
                deleted,
                failed,
                items = report
            });
        }

        private static HashSet<long> CollectReferencedSymbolIds(Document doc)
        {
            var ids = new HashSet<long>();
            var inPlaceSymbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family?.IsInPlace == true)
                .Select(s => RevitCompat.GetId(s.Id));

            foreach (var id in inPlaceSymbols) ids.Add(id);
            return ids;
        }
    }
}
