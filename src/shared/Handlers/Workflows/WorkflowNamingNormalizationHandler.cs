using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class WorkflowNamingNormalizationHandler : IRevitCommand
    {
        public string Name => "workflow_naming_normalization";
        public string Description => "Analyze and optionally rename views, sheets, levels, and grids using deterministic normalization or a token pattern.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""target""],
  ""properties"": {
    ""target"": { ""type"": ""string"", ""enum"": [""views"", ""sheets"", ""levels"", ""grids"", ""all""] },
    ""profile"": { ""type"": ""string"" },
    ""pattern"": { ""type"": ""string"" },
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 1000 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No active document is available.");

            JObject request;
            try
            {
                request = WorkflowSupport.ParseParams(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var target = (request.Value<string>("target") ?? string.Empty).Trim().ToLowerInvariant();
            var profile = request.Value<string>("profile");
            var pattern = request.Value<string>("pattern");
            var dryRun = request.Value<bool?>("dry_run") ?? true;
            var limit = request.Value<int?>("limit") ?? 200;
            long[] ids;
            try
            {
                ids = WorkflowSupport.ReadLongArray(request, "ids");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            if (!new[] { "views", "sheets", "levels", "grids", "all" }.Contains(target))
                return CommandResult.Fail("target must be one of: views, sheets, levels, grids, all.");
            if (limit < 1 || limit > 1000)
                return CommandResult.Fail("limit must be between 1 and 1000.");

            var steps = new JArray();
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(profile) && string.IsNullOrWhiteSpace(pattern))
                warnings.Add("profile is recorded for traceability, but no external profile library is applied by this workflow. Provide pattern for profile-specific renaming.");

            var elements = CollectTargets(doc, target, ids, limit, out var truncated, warnings);
            if (truncated)
                warnings.Add("Target collection was truncated at " + limit.ToString(CultureInfo.InvariantCulture) + ".");

            var proposed = BuildRenamePlan(doc, elements, pattern, out var conflicts, out var skipped);
            steps.Add(WorkflowSupport.Step(
                "Analyze Names",
                "workflow_naming_normalization",
                "succeeded",
                "Collect target elements and build deterministic rename proposals.",
                new { target, collected = elements.Count, proposed = proposed.Count, conflicts = conflicts.Count, skipped = skipped.Count, truncated }));

            if (dryRun)
            {
                var result = BuildResult(
                    true,
                    conflicts.Count > 0 ? "partial" : "succeeded",
                    steps,
                    warnings,
                    proposed,
                    new JArray(),
                    conflicts,
                    skipped,
                    Array.Empty<long>(),
                    WorkflowSupport.Rollback("Transaction", false, "Dry-run; no rename transaction opened."));
                return CommandResult.Ok(result);
            }

            if (conflicts.Count > 0)
            {
                steps.Add(WorkflowSupport.Step(
                    "Apply Renames",
                    "Element.Name",
                    "failed",
                    "Reject rename commit because target names conflict.",
                    null,
                    "Resolve conflicts before setting dry_run=false."));
                return CommandResult.Ok(BuildResult(false, "failed", steps, warnings, proposed, new JArray(), conflicts, skipped, Array.Empty<long>(), WorkflowSupport.Rollback("Transaction", false, "Validation failed before transaction.")));
            }

            var applied = new JArray();
            var modifiedIds = new List<long>();
            using (var tx = new Transaction(doc, "RvtMcp: workflow naming normalization"))
            {
                tx.Start();
                try
                {
                    foreach (var proposal in proposed.OfType<JObject>())
                    {
                        var rawId = proposal.Value<long>("element_id");
                        var element = doc.GetElement(RevitCompat.ToElementId(rawId));
                        if (element == null)
                            throw new InvalidOperationException("Element disappeared before rename: " + rawId.ToString(CultureInfo.InvariantCulture));

                        element.Name = proposal.Value<string>("proposed_name");
                        modifiedIds.Add(rawId);
                        applied.Add(proposal.DeepClone());
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException("Transaction status: " + status);

                    steps.Add(WorkflowSupport.Step(
                        "Apply Renames",
                        "Element.Name",
                        "succeeded",
                        "Committed all validated renames.",
                        new { renamed = applied.Count }));
                    return CommandResult.Ok(BuildResult(false, "succeeded", steps, warnings, proposed, applied, conflicts, skipped, modifiedIds, WorkflowSupport.Rollback("Transaction", false, "Rename transaction committed.")));
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted())
                        tx.RollBack();
                    steps.Add(WorkflowSupport.Step("Apply Renames", "Element.Name", "failed", "Committed all validated renames.", null, ex.Message));
                    return CommandResult.Ok(BuildResult(false, "failed", steps, warnings, proposed, applied, conflicts, skipped, Array.Empty<long>(), WorkflowSupport.Rollback("Transaction", true, "Rename transaction failed and was rolled back.")));
                }
            }
        }

        private JObject BuildResult(
            bool dryRun,
            string status,
            JArray steps,
            IEnumerable<string> warnings,
            JArray proposedRenames,
            JArray appliedRenames,
            JArray conflicts,
            JArray skippedItems,
            IEnumerable<long> modifiedIds,
            JObject rollback)
        {
            var result = WorkflowSupport.Envelope(Name, dryRun, status, steps, Array.Empty<long>(), modifiedIds, warnings, rollback);
            result["proposed_renames"] = proposedRenames ?? new JArray();
            result["applied_renames"] = appliedRenames ?? new JArray();
            result["conflicts"] = conflicts ?? new JArray();
            result["skipped_items"] = skippedItems ?? new JArray();
            return result;
        }

        private static List<Element> CollectTargets(Document doc, string target, long[] ids, int limit, List<string> warnings)
        {
            return CollectTargets(doc, target, ids, limit, out _, warnings);
        }

        private static List<Element> CollectTargets(Document doc, string target, long[] ids, int limit, out bool truncated, List<string> warnings)
        {
            truncated = false;
            var idFilter = ids == null || ids.Length == 0 ? null : new HashSet<long>(ids);
            var elements = new List<Element>();

            foreach (var element in EnumerateTargets(doc, target))
            {
                if (elements.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                var rawId = RevitCompat.GetId(element.Id);
                if (idFilter != null && !idFilter.Contains(rawId))
                    continue;
                if (IsProtected(element, out var reason))
                {
                    warnings.Add("Skipped " + rawId.ToString(CultureInfo.InvariantCulture) + ": " + reason);
                    continue;
                }
                elements.Add(element);
            }

            if (idFilter != null)
            {
                var found = new HashSet<long>(elements.Select(e => RevitCompat.GetId(e.Id)));
                foreach (var requested in idFilter.Where(id => !found.Contains(id)))
                    warnings.Add("Requested id was not found or is not in target scope: " + requested.ToString(CultureInfo.InvariantCulture));
            }

            return elements;
        }

        private static IEnumerable<Element> EnumerateTargets(Document doc, string target)
        {
            if (target == "views" || target == "all")
            {
                foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    if (!(view is ViewSheet))
                        yield return view;
                }
            }
            if (target == "sheets" || target == "all")
            {
                foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                    yield return sheet;
            }
            if (target == "levels" || target == "all")
            {
                foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    yield return level;
            }
            if (target == "grids" || target == "all")
            {
                foreach (var grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                    yield return grid;
            }
        }

        private static bool IsProtected(Element element, out string reason)
        {
            reason = null;
            var view = element as View;
            if (view != null)
            {
                if (view.IsTemplate)
                {
                    reason = "View templates are managed by the organization toolset.";
                    return true;
                }
                if (view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser)
                {
                    reason = "System browser views cannot be renamed.";
                    return true;
                }
            }
            return false;
        }

        private static JArray BuildRenamePlan(Document doc, List<Element> elements, string pattern, out JArray conflicts, out JArray skipped)
        {
            conflicts = new JArray();
            skipped = new JArray();
            var proposed = new JArray();
            var existingByKind = BuildExistingNameMap(doc);
            var proposedNamesByKind = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var kind = KindOf(element);
                var current = WorkflowSupport.SafeName(element) ?? string.Empty;
                var proposedName = WorkflowSupport.ApplyPattern(pattern, element, i + 1);

                if (string.IsNullOrWhiteSpace(proposedName))
                {
                    skipped.Add(Skip(element, "Proposed name is blank."));
                    continue;
                }
                if (string.Equals(current, proposedName, StringComparison.Ordinal))
                {
                    skipped.Add(Skip(element, "Already normalized."));
                    continue;
                }

                if (!proposedNamesByKind.TryGetValue(kind, out var proposedSet))
                {
                    proposedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    proposedNamesByKind[kind] = proposedSet;
                }

                if (!proposedSet.Add(proposedName))
                {
                    conflicts.Add(Conflict(element, proposedName, "Duplicate proposed name in request."));
                    continue;
                }

                if (existingByKind.TryGetValue(kind, out var existingNames) &&
                    existingNames.TryGetValue(proposedName, out var existingId) &&
                    existingId != RevitCompat.GetId(element.Id))
                {
                    conflicts.Add(Conflict(element, proposedName, "Name already exists on element " + existingId.ToString(CultureInfo.InvariantCulture) + "."));
                    continue;
                }

                proposed.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(element.Id),
                    ["kind"] = kind,
                    ["current_name"] = current,
                    ["proposed_name"] = proposedName
                });
            }

            return proposed;
        }

        private static Dictionary<string, Dictionary<string, long>> BuildExistingNameMap(Document doc)
        {
            var map = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in EnumerateTargets(doc, "all"))
            {
                var kind = KindOf(element);
                var name = WorkflowSupport.SafeName(element);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!map.TryGetValue(kind, out var names))
                {
                    names = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    map[kind] = names;
                }
                if (!names.ContainsKey(name))
                    names[name] = RevitCompat.GetId(element.Id);
            }
            return map;
        }

        private static string KindOf(Element element)
        {
            if (element is ViewSheet) return "sheets";
            if (element is View) return "views";
            if (element is Level) return "levels";
            if (element is Grid) return "grids";
            return "elements";
        }

        private static JObject Skip(Element element, string reason)
        {
            return new JObject
            {
                ["element_id"] = RevitCompat.GetId(element.Id),
                ["kind"] = KindOf(element),
                ["current_name"] = WorkflowSupport.SafeName(element),
                ["reason"] = reason
            };
        }

        private static JObject Conflict(Element element, string proposedName, string reason)
        {
            return new JObject
            {
                ["element_id"] = RevitCompat.GetId(element.Id),
                ["kind"] = KindOf(element),
                ["current_name"] = WorkflowSupport.SafeName(element),
                ["proposed_name"] = proposedName,
                ["reason"] = reason
            };
        }
    }
}
