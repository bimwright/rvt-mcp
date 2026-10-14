using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class WorkflowModelAuditHandler : IRevitCommand
    {
        public string Name => "workflow_model_audit";
        public string Description => "Run a read-only composite model audit covering warnings, families, views, schedules, and MEP connectivity signals.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""include_warnings"": { ""type"": ""boolean"", ""default"": true },
    ""include_families"": { ""type"": ""boolean"", ""default"": true },
    ""include_views"": { ""type"": ""boolean"", ""default"": true },
    ""include_schedules"": { ""type"": ""boolean"", ""default"": true },
    ""include_mep"": { ""type"": ""boolean"", ""default"": true },
    ""limit_per_section"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 1000 }
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

            var includeWarnings = request.Value<bool?>("include_warnings") ?? true;
            var includeFamilies = request.Value<bool?>("include_families") ?? true;
            var includeViews = request.Value<bool?>("include_views") ?? true;
            var includeSchedules = request.Value<bool?>("include_schedules") ?? true;
            var includeMep = request.Value<bool?>("include_mep") ?? true;
            var limitPerSection = request.Value<int?>("limit_per_section") ?? 100;

            if (!includeWarnings && !includeFamilies && !includeViews && !includeSchedules && !includeMep)
                return CommandResult.Fail("At least one audit section flag must be true.");
            if (limitPerSection < 1 || limitPerSection > 1000)
                return CommandResult.Fail("limit_per_section must be between 1 and 1000.");

            var steps = new JArray();
            var sections = new JObject();
            var recommendations = new JArray();
            var warningMessages = new List<string>();
            var riskHigh = 0;
            var riskMedium = 0;
            var riskLow = 0;
            var truncated = false;

            var stats = CollectModelStatistics(doc, limitPerSection, out var statsTruncated);
            truncated |= statsTruncated;
            steps.Add(WorkflowSupport.Step(
                "Model Statistics",
                "analyze_model_statistics",
                "succeeded",
                "Count model elements by category.",
                stats));
            sections["statistics"] = stats;

            if (includeWarnings)
            {
                var section = AuditWarnings(doc, limitPerSection, out var sectionTruncated);
                truncated |= sectionTruncated;
                riskHigh += section.Value<int>("high");
                riskMedium += section.Value<int>("medium");
                riskLow += section.Value<int>("low");
                sections["warnings"] = section;
                steps.Add(WorkflowSupport.Step(
                    "Warnings",
                    "doc.GetWarnings",
                    "succeeded",
                    "Collect Revit failure messages grouped by description.",
                    new { count = section.Value<int>("total") }));
            }

            if (includeFamilies)
            {
                var section = AuditFamilies(doc, limitPerSection, out var sectionTruncated);
                truncated |= sectionTruncated;
                riskMedium += section.Value<int>("unused_count");
                riskLow += section.Value<int>("high_type_count");
                sections["families"] = section;
                steps.Add(WorkflowSupport.Step(
                    "Families",
                    "audit_families",
                    "succeeded",
                    "Find in-place, unused, duplicate-name, and high-type-count families.",
                    new { count = section.Value<int>("total_families") }));
            }

            if (includeViews)
            {
                var section = AuditViews(doc, limitPerSection, out var sectionTruncated);
                truncated |= sectionTruncated;
                riskLow += section.Value<int>("unplaced_printable_count");
                riskLow += section.Value<int>("duplicate_name_groups_count");
                sections["views"] = section;
                steps.Add(WorkflowSupport.Step(
                    "Views",
                    "view collectors",
                    "succeeded",
                    "Check templates, printable views, sheet placement, and duplicate names.",
                    new { count = section.Value<int>("total_views") }));
            }

            if (includeSchedules)
            {
                var section = AuditSchedules(doc, limitPerSection, out var sectionTruncated);
                truncated |= sectionTruncated;
                riskLow += section.Value<int>("empty_count");
                sections["schedules"] = section;
                steps.Add(WorkflowSupport.Step(
                    "Schedules",
                    "list_schedules",
                    "succeeded",
                    "Check schedules, placement, and likely empty body rows.",
                    new { count = section.Value<int>("total_schedules") }));
            }

            if (includeMep)
            {
                var section = AuditMep(doc, limitPerSection, out var sectionTruncated);
                truncated |= sectionTruncated;
                riskMedium += section.Value<int>("disconnected_count");
                sections["mep"] = section;
                steps.Add(WorkflowSupport.Step(
                    "MEP",
                    "find_mep_disconnects",
                    "succeeded",
                    "Sample MEP curves and family instances for connector disconnect signals.",
                    new { count = section.Value<int>("checked") }));
            }

            if (riskHigh > 0)
                recommendations.Add("Resolve high-severity warnings before issuing model deliverables.");
            if (riskMedium > 0)
                recommendations.Add("Review medium-risk families, warnings, and disconnected MEP elements before coordination.");
            if (riskLow > 0)
                recommendations.Add("Use cleanup and naming workflows to reduce low-risk model hygiene findings.");
            if (recommendations.Count == 0)
                recommendations.Add("No audit findings were detected with the selected sections.");

            if (truncated)
                warningMessages.Add("At least one audit section was truncated by limit_per_section.");

            var envelope = WorkflowSupport.Envelope(
                Name,
                false,
                "succeeded",
                steps,
                Array.Empty<long>(),
                Array.Empty<long>(),
                warningMessages,
                WorkflowSupport.Rollback("None", false, "Read-only audit; no transaction opened."));

            envelope["summary"] = new JObject
            {
                ["document_title"] = doc.Title ?? string.Empty,
                ["status"] = riskHigh > 0 ? "high_risk" : riskMedium > 0 ? "review_needed" : riskLow > 0 ? "minor_findings" : "clean",
                ["finding_count"] = riskHigh + riskMedium + riskLow,
                ["risk_counts"] = new JObject
                {
                    ["high"] = riskHigh,
                    ["medium"] = riskMedium,
                    ["low"] = riskLow
                }
            };
            envelope["sections"] = sections;
            envelope["risk_counts"] = envelope["summary"]["risk_counts"];
            envelope["recommendations"] = recommendations;
            envelope["truncated"] = truncated;

            return CommandResult.Ok(envelope);
        }

        private static JObject CollectModelStatistics(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var processed = 0;

            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (processed >= limit)
                {
                    truncated = true;
                    break;
                }

                processed++;
                var category = WorkflowSupport.SafeCategoryName(element) ?? "Uncategorized";
                categories[category] = categories.TryGetValue(category, out var count) ? count + 1 : 1;
            }

            return new JObject
            {
                ["elements_counted"] = processed,
                ["truncated"] = truncated,
                ["categories"] = JArray.FromObject(categories.OrderByDescending(kv => kv.Value).Select(kv => new { category = kv.Key, count = kv.Value }).ToArray())
            };
        }

        private static JObject AuditWarnings(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var warnings = doc.GetWarnings();
            var grouped = warnings
                .GroupBy(w => SafeDescription(w))
                .OrderByDescending(g => g.Count())
                .Take(limit)
                .Select(g => new
                {
                    description = g.Key,
                    count = g.Count(),
                    severity = ClassifyWarning(g.Key),
                    failing_element_ids = g.SelectMany(x => SafeFailingIds(x)).Distinct().Take(20).ToArray()
                })
                .ToArray();

            truncated = warnings.Count > grouped.Length;

            return new JObject
            {
                ["total"] = warnings.Count,
                ["returned"] = grouped.Length,
                ["high"] = grouped.Where(g => g.severity == "high").Sum(g => g.count),
                ["medium"] = grouped.Where(g => g.severity == "medium").Sum(g => g.count),
                ["low"] = grouped.Where(g => g.severity == "low").Sum(g => g.count),
                ["items"] = JArray.FromObject(grouped)
            };
        }

        private static JObject AuditFamilies(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var symbolToFamily = new Dictionary<long, long>();
            var familyInfo = new Dictionary<long, FamilyAuditInfo>();

            foreach (var family in families)
            {
                var id = RevitCompat.GetId(family.Id);
                var info = new FamilyAuditInfo
                {
                    Id = id,
                    Name = WorkflowSupport.SafeName(family) ?? string.Empty,
                    Category = SafeFamilyCategory(family),
                    IsInPlace = SafeIsInPlace(family)
                };

                try
                {
                    var symbolIds = family.GetFamilySymbolIds();
                    info.TypeCount = symbolIds == null ? 0 : symbolIds.Count;
                    if (symbolIds != null)
                    {
                        foreach (var sid in symbolIds)
                            symbolToFamily[RevitCompat.GetId(sid)] = id;
                    }
                }
                catch { }

                familyInfo[id] = info;
            }

            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                try
                {
                    var fi = element as FamilyInstance;
                    if (fi == null || fi.Symbol == null)
                        continue;
                    var symbolId = RevitCompat.GetId(fi.Symbol.Id);
                    if (symbolToFamily.TryGetValue(symbolId, out var familyId) && familyInfo.TryGetValue(familyId, out var info))
                        info.InstanceCount++;
                }
                catch { }
            }

            var infos = familyInfo.Values.ToList();
            var unused = infos.Where(f => f.InstanceCount == 0).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
            var inplace = infos.Where(f => f.IsInPlace).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
            var highType = infos.Where(f => f.TypeCount >= 20).OrderByDescending(f => f.TypeCount).Take(limit).ToArray();
            var duplicates = infos.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
                .Take(limit)
                .Select(g => new { name = g.Key, ids = g.Select(x => x.Id).ToArray(), count = g.Count() })
                .ToArray();

            truncated = unused.Length == limit || inplace.Length == limit || highType.Length == limit || duplicates.Length == limit;

            return new JObject
            {
                ["total_families"] = infos.Count,
                ["unused_count"] = infos.Count(f => f.InstanceCount == 0),
                ["inplace_count"] = infos.Count(f => f.IsInPlace),
                ["high_type_count"] = infos.Count(f => f.TypeCount >= 20),
                ["duplicate_name_groups_count"] = duplicates.Length,
                ["unused"] = JArray.FromObject(unused.Select(ToFamilyDto).ToArray()),
                ["inplace"] = JArray.FromObject(inplace.Select(ToFamilyDto).ToArray()),
                ["high_type_count_families"] = JArray.FromObject(highType.Select(ToFamilyDto).ToArray()),
                ["duplicate_name_groups"] = JArray.FromObject(duplicates)
            };
        }

        private static JObject AuditViews(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var placedViewIds = new HashSet<long>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try { placedViewIds.Add(RevitCompat.GetId(vp.ViewId)); }
                catch { }
            }

            foreach (var ssi in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                try { placedViewIds.Add(RevitCompat.GetId(ssi.ScheduleId)); }
                catch { }
            }

            var printable = views.Where(v => SafeCanBePrinted(v) && !v.IsTemplate).ToList();
            var unplaced = printable
                .Where(v => !placedViewIds.Contains(RevitCompat.GetId(v.Id)) && v.ViewType != ViewType.DrawingSheet)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(v => new { view_id = RevitCompat.GetId(v.Id), name = v.Name, view_type = v.ViewType.ToString() })
                .ToArray();
            var duplicateGroups = printable
                .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
                .Take(limit)
                .Select(g => new { name = g.Key, ids = g.Select(v => RevitCompat.GetId(v.Id)).ToArray(), count = g.Count() })
                .ToArray();

            truncated = unplaced.Length == limit || duplicateGroups.Length == limit;
            return new JObject
            {
                ["total_views"] = views.Count,
                ["templates"] = views.Count(v => v.IsTemplate),
                ["printable"] = printable.Count,
                ["unplaced_printable_count"] = printable.Count(v => !placedViewIds.Contains(RevitCompat.GetId(v.Id)) && v.ViewType != ViewType.DrawingSheet),
                ["duplicate_name_groups_count"] = duplicateGroups.Length,
                ["unplaced_printable"] = JArray.FromObject(unplaced),
                ["duplicate_name_groups"] = JArray.FromObject(duplicateGroups)
            };
        }

        private static JObject AuditSchedules(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate)
                .ToList();

            var placedScheduleIds = new HashSet<long>();
            foreach (var ssi in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                try { placedScheduleIds.Add(RevitCompat.GetId(ssi.ScheduleId)); }
                catch { }
            }

            var empty = new List<object>();
            foreach (var schedule in schedules)
            {
                if (empty.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                if (IsScheduleLikelyEmpty(schedule))
                {
                    empty.Add(new
                    {
                        schedule_id = RevitCompat.GetId(schedule.Id),
                        name = schedule.Name,
                        is_placed = placedScheduleIds.Contains(RevitCompat.GetId(schedule.Id))
                    });
                }
            }

            return new JObject
            {
                ["total_schedules"] = schedules.Count,
                ["placed_count"] = schedules.Count(s => placedScheduleIds.Contains(RevitCompat.GetId(s.Id))),
                ["empty_count"] = empty.Count,
                ["empty"] = JArray.FromObject(empty)
            };
        }

        private static JObject AuditMep(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var candidates = new List<Element>();
            foreach (BuiltInCategory bic in new[]
            {
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_PlumbingFixtures
            })
            {
                try
                {
                    foreach (var element in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                    {
                        if (candidates.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }
                        candidates.Add(element);
                    }
                }
                catch { }
                if (truncated)
                    break;
            }

            var disconnected = new List<object>();
            foreach (var element in candidates)
            {
                var count = CountDisconnectedConnectors(element);
                if (count > 0)
                {
                    disconnected.Add(new
                    {
                        element_id = RevitCompat.GetId(element.Id),
                        name = WorkflowSupport.SafeName(element),
                        category = WorkflowSupport.SafeCategoryName(element),
                        disconnected_connectors = count
                    });
                }
            }

            return new JObject
            {
                ["checked"] = candidates.Count,
                ["disconnected_count"] = disconnected.Count,
                ["disconnected"] = JArray.FromObject(disconnected)
            };
        }

        private static IEnumerable<long> SafeFailingIds(FailureMessage warning)
        {
            try { return warning.GetFailingElements().Select(RevitCompat.GetId); }
            catch { return Array.Empty<long>(); }
        }

        private static string SafeDescription(FailureMessage warning)
        {
            try { return warning.GetDescriptionText() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string ClassifyWarning(string description)
        {
            var text = (description ?? string.Empty).ToLowerInvariant();
            if (text.Contains("overlap") || text.Contains("duplicate") || text.Contains("identical") || text.Contains("room") && text.Contains("not enclosed"))
                return "medium";
            if (text.Contains("cannot") || text.Contains("failed") || text.Contains("lost") || text.Contains("inconsistent"))
                return "high";
            return "low";
        }

        private static bool SafeIsInPlace(Family family)
        {
            try { return family.IsInPlace; }
            catch { return false; }
        }

        private static string SafeFamilyCategory(Family family)
        {
            try { return family.FamilyCategory?.Name ?? "Uncategorized"; }
            catch { return "Uncategorized"; }
        }

        private static object ToFamilyDto(FamilyAuditInfo f)
        {
            return new
            {
                family_id = f.Id,
                name = f.Name,
                category = f.Category,
                is_in_place = f.IsInPlace,
                type_count = f.TypeCount,
                instance_count = f.InstanceCount
            };
        }

        private static bool SafeCanBePrinted(View view)
        {
            try { return view.CanBePrinted; }
            catch { return false; }
        }

        private static bool IsScheduleLikelyEmpty(ViewSchedule schedule)
        {
            try
            {
                var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                return body == null || body.NumberOfRows <= 1;
            }
            catch
            {
                return false;
            }
        }

        private static int CountDisconnectedConnectors(Element element)
        {
            try
            {
                ConnectorSet connectors = null;
                var fi = element as FamilyInstance;
                if (fi != null)
                    connectors = fi.MEPModel?.ConnectorManager?.Connectors;
                var mepCurve = element as MEPCurve;
                if (mepCurve != null)
                    connectors = mepCurve.ConnectorManager?.Connectors;

                if (connectors == null)
                    return 0;

                var disconnected = 0;
                foreach (Connector connector in connectors)
                {
                    try
                    {
                        if (!connector.IsConnected)
                            disconnected++;
                    }
                    catch { }
                }
                return disconnected;
            }
            catch
            {
                return 0;
            }
        }

        private class FamilyAuditInfo
        {
            public long Id;
            public string Name;
            public string Category;
            public bool IsInPlace;
            public int TypeCount;
            public int InstanceCount;
        }
    }
}
