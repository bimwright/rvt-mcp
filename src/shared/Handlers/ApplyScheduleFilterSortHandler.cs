using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ApplyScheduleFilterSortHandler : IRevitCommand
    {
        public string Name => "apply_schedule_filter_sort";

        public string Description =>
            "Partially update a schedule's filters, sort/group, and settings. filters/sortGroup replace only when supplied; omitted sections are preserved.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""filters"":{""type"":""array""},""sortGroup"":{""type"":""array""},""settings"":{""type"":""object""}}}";

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
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // Resolve schedule (id or name)
            var scheduleIdToken = request["scheduleId"];
            var scheduleName = request.Value<string>("scheduleName");

            ViewSchedule schedule = null;

            if (scheduleIdToken != null && scheduleIdToken.Type == JTokenType.Integer)
            {
                var id = scheduleIdToken.Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(id));

                try
                {
                    schedule = doc.GetElement(RevitCompat.ToElementId(id)) as ViewSchedule;
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Failed to resolve schedule by id: " + ex.Message);
                }

                if (schedule == null)
                    return CommandResult.Fail("No ViewSchedule found with id " + id + ".");
            }
            else if (!string.IsNullOrWhiteSpace(scheduleName))
            {
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => s.Name != null && s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail("No ViewSchedule found with name '" + scheduleName + "'.");
                if (matches.Count > 1)
                    return CommandResult.Fail("Ambiguous schedule name '" + scheduleName + "': " + matches.Count + " matches found. Use scheduleId.");

                schedule = matches[0];
            }
            else
            {
                return CommandResult.Fail("Either scheduleId or scheduleName must be provided.");
            }

            var definition = schedule.Definition;
            if (definition == null)
                return CommandResult.Fail("Schedule has no definition.");

            // Build fieldName → ScheduleFieldId map + fieldName → ForgeTypeId spec map.
            // Use ordinal-ignore-case for the lookup key (Revit field names are case-sensitive
            // in storage but humans commonly type in mixed-case).
            var nameToFieldId = new Dictionary<string, ScheduleFieldId>(StringComparer.OrdinalIgnoreCase);
            var nameToSpec = new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var fieldOrder = definition.GetFieldOrder();
                if (fieldOrder != null)
                {
                    foreach (var fId in fieldOrder)
                    {
                        try
                        {
                            var field = definition.GetField(fId);
                            if (field == null) continue;
                            var fName = field.GetName();
                            if (string.IsNullOrEmpty(fName)) continue;
                            if (!nameToFieldId.ContainsKey(fName))
                                nameToFieldId[fName] = fId;
                            try
                            {
                                var spec = field.GetSpecTypeId();
                                if (spec != null && !nameToSpec.ContainsKey(fName))
                                    nameToSpec[fName] = spec;
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to inspect schedule field order: " + ex.Message);
            }

            var filtersArray = request["filters"] as JArray;
            var sortGroupArray = request["sortGroup"] as JArray;
            var settingsObj = request["settings"] as JObject;

            int filtersCleared = 0;
            int sortGroupCleared = 0;
            int filtersApplied = 0;
            int sortGroupApplied = 0;
            int settingsApplied = 0;

            using (var tx = new Transaction(doc, "MCP: Apply Schedule Filter/Sort"))
            {
                tx.Start();
                try
                {
                    // Replace filters only when the request supplies filters (pass [] to clear).
                    if (filtersArray != null)
                    {
                        while (definition.GetFilterCount() > 0)
                        {
                            definition.RemoveFilter(0);
                            filtersCleared++;
                        }

                        for (int i = 0; i < filtersArray.Count; i++)
                        {
                            var spec = filtersArray[i] as JObject;
                            if (spec == null)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("filters[" + i + "] must be an object.");
                            }

                            var fieldName = spec.Value<string>("fieldName");
                            if (string.IsNullOrWhiteSpace(fieldName))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("filters[" + i + "].fieldName is required.");
                            }

                            if (!nameToFieldId.TryGetValue(fieldName, out var fieldId))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("filters[" + i + "].fieldName '" + fieldName + "' not in schedule.");
                            }

                            var operatorStr = spec.Value<string>("operator");
                            if (string.IsNullOrWhiteSpace(operatorStr))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("filters[" + i + "].operator is required.");
                            }

                            ScheduleFilterType filterType;
                            try
                            {
                                filterType = ResolveFilterType(operatorStr);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("filters[" + i + "].operator error: " + ex.Message);
                            }

                            ScheduleFilter filter;
                            try
                            {
                                filter = BuildFilter(fieldId, filterType, spec["value"], nameToSpec.TryGetValue(fieldName, out var sp) ? sp : null);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("filters[" + i + "] value error: " + ex.Message);
                            }

                            try
                            {
                                definition.AddFilter(filter);
                                filtersApplied++;
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("filters[" + i + "] could not be added: " + ex.Message);
                            }
                        }
                    }

                    // Replace sort/group only when the request supplies sortGroup (pass [] to clear).
                    if (sortGroupArray != null)
                    {
                        while (definition.GetSortGroupFieldCount() > 0)
                        {
                            definition.RemoveSortGroupField(0);
                            sortGroupCleared++;
                        }

                        for (int i = 0; i < sortGroupArray.Count; i++)
                        {
                            var spec = sortGroupArray[i] as JObject;
                            if (spec == null)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("sortGroup[" + i + "] must be an object.");
                            }

                            var fieldName = spec.Value<string>("fieldName");
                            if (string.IsNullOrWhiteSpace(fieldName))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("sortGroup[" + i + "].fieldName is required.");
                            }

                            if (!nameToFieldId.TryGetValue(fieldName, out var fieldId))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("sortGroup[" + i + "].fieldName '" + fieldName + "' not in schedule.");
                            }

                            bool ascending = spec.Value<bool?>("ascending") ?? true;
                            var sortOrder = ascending ? ScheduleSortOrder.Ascending : ScheduleSortOrder.Descending;

                            ScheduleSortGroupField sgf;
                            try
                            {
                                sgf = new ScheduleSortGroupField(fieldId, sortOrder);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("sortGroup[" + i + "] could not be constructed: " + ex.Message);
                            }

                            var showHeaderTok = spec["showHeader"];
                            if (showHeaderTok != null && showHeaderTok.Type == JTokenType.Boolean)
                            {
                                try { sgf.ShowHeader = showHeaderTok.Value<bool>(); } catch { }
                            }

                            var showFooterTok = spec["showFooter"];
                            if (showFooterTok != null && showFooterTok.Type == JTokenType.Boolean)
                            {
                                try { sgf.ShowFooter = showFooterTok.Value<bool>(); } catch { }
                            }

                            var blankLineTok = spec["showBlankLine"];
                            if (blankLineTok != null && blankLineTok.Type == JTokenType.Boolean)
                            {
                                try { sgf.ShowBlankLine = blankLineTok.Value<bool>(); } catch { }
                            }

                            try
                            {
                                definition.AddSortGroupField(sgf);
                                sortGroupApplied++;
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("sortGroup[" + i + "] could not be added: " + ex.Message);
                            }
                        }
                    }

                    // Apply settings
                    if (settingsObj != null)
                    {
                        var isItemizedTok = settingsObj["isItemized"];
                        if (isItemizedTok != null && isItemizedTok.Type == JTokenType.Boolean)
                        {
                            try { definition.IsItemized = isItemizedTok.Value<bool>(); settingsApplied++; }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("settings.isItemized could not be applied: " + ex.Message);
                            }
                        }

                        var showGrandTotalTok = settingsObj["showGrandTotal"];
                        if (showGrandTotalTok != null && showGrandTotalTok.Type == JTokenType.Boolean)
                        {
                            try { definition.ShowGrandTotal = showGrandTotalTok.Value<bool>(); settingsApplied++; }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("settings.showGrandTotal could not be applied: " + ex.Message);
                            }
                        }

                        var showHeadersTok = settingsObj["showHeaders"];
                        if (showHeadersTok != null && showHeadersTok.Type == JTokenType.Boolean)
                        {
                            try { definition.ShowHeaders = showHeadersTok.Value<bool>(); settingsApplied++; }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("settings.showHeaders could not be applied: " + ex.Message);
                            }
                        }

                        var showTitleTok = settingsObj["showTitle"];
                        if (showTitleTok != null && showTitleTok.Type == JTokenType.Boolean)
                        {
                            try { definition.ShowTitle = showTitleTok.Value<bool>(); settingsApplied++; }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail("settings.showTitle could not be applied: " + ex.Message);
                            }
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to apply schedule filter/sort: " + ex.Message);
                }
            }

            return CommandResult.Ok(new
            {
                scheduleId = RevitCompat.GetId(schedule.Id),
                scheduleName = schedule.Name,
                filtersApplied = filtersApplied,
                sortGroupApplied = sortGroupApplied,
                settingsApplied = settingsApplied,
                filtersCleared = filtersCleared,
                sortGroupCleared = sortGroupCleared,
                filtersReplaced = filtersArray != null,
                sortGroupReplaced = sortGroupArray != null
            });
        }

        private static ScheduleFilterType ResolveFilterType(string opStr)
        {
            switch ((opStr ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "equals":
                case "equal":
                case "=":
                    return ScheduleFilterType.Equal;
                case "notequal":
                case "notequals":
                case "!=":
                case "<>":
                    return ScheduleFilterType.NotEqual;
                case "greaterthan":
                case "greater":
                case ">":
                    return ScheduleFilterType.GreaterThan;
                case "greaterthanorequal":
                case "greaterorequal":
                case ">=":
                    return ScheduleFilterType.GreaterThanOrEqual;
                case "lessthan":
                case "less":
                case "<":
                    return ScheduleFilterType.LessThan;
                case "lessthanorequal":
                case "lessorequal":
                case "<=":
                    return ScheduleFilterType.LessThanOrEqual;
                case "contains":
                    return ScheduleFilterType.Contains;
                case "notcontains":
                case "doesnotcontain":
                    return ScheduleFilterType.NotContains;
                case "beginswith":
                case "startswith":
                    return ScheduleFilterType.BeginsWith;
                case "notbeginswith":
                case "doesnotbeginwith":
                    return ScheduleFilterType.NotBeginsWith;
                case "endswith":
                    return ScheduleFilterType.EndsWith;
                case "notendswith":
                case "doesnotendwith":
                    return ScheduleFilterType.NotEndsWith;
                case "isempty":
                case "hasnovalue":
                    return ParseFilterType("IsEmpty", "HasNoValue");
                case "isnotempty":
                case "hasvalue":
                    return ParseFilterType("IsNotEmpty", "HasValue");
                case "hasparameter":
                    return ParseFilterType("HasParameter", "HasValue");
                case "hasnoparameter":
                    return ParseFilterType("HasNoParameter", "HasNoValue");
                default:
                    throw new ArgumentException("Unsupported filter operator '" + opStr + "'.");
            }
        }

        private static ScheduleFilterType ParseFilterType(params string[] names)
        {
            foreach (var name in names)
            {
                if (Enum.TryParse<ScheduleFilterType>(name, true, out var parsed))
                    return parsed;
            }

            throw new ArgumentException("This Revit API version does not expose any of: " + string.Join(", ", names) + ".");
        }

        // Build a ScheduleFilter for the given field, operator, and JSON value token.
        // Picks the appropriate ScheduleFilter constructor based on operator semantics
        // (no-value for IsEmpty/HasParameter family) and the JSON token type with the
        // field's spec as a tiebreaker for numeric values.
        private static ScheduleFilter BuildFilter(ScheduleFieldId fieldId, ScheduleFilterType filterType, JToken valueToken, ForgeTypeId fieldSpec)
        {
            // No-value operators
            var filterTypeName = filterType.ToString();
            if (filterTypeName.Equals("IsEmpty", StringComparison.OrdinalIgnoreCase)
                || filterTypeName.Equals("IsNotEmpty", StringComparison.OrdinalIgnoreCase)
                || filterTypeName.Equals("HasParameter", StringComparison.OrdinalIgnoreCase)
                || filterTypeName.Equals("HasNoParameter", StringComparison.OrdinalIgnoreCase)
                || filterTypeName.Equals("HasValue", StringComparison.OrdinalIgnoreCase)
                || filterTypeName.Equals("HasNoValue", StringComparison.OrdinalIgnoreCase))
            {
                return new ScheduleFilter(fieldId, filterType);
            }

            if (valueToken == null || valueToken.Type == JTokenType.Null)
                throw new ArgumentException("value is required for operator '" + filterType + "'.");

            switch (valueToken.Type)
            {
                case JTokenType.String:
                    {
                        var s = valueToken.Value<string>() ?? string.Empty;
                        return new ScheduleFilter(fieldId, filterType, s);
                    }
                case JTokenType.Integer:
                    {
                        // Prefer double constructor for length/area/volume/angle/number/currency specs
                        if (IsDoubleSpec(fieldSpec))
                            return new ScheduleFilter(fieldId, filterType, ConvertToInternalUnits(valueToken.Value<double>(), fieldSpec));
                        return new ScheduleFilter(fieldId, filterType, valueToken.Value<int>());
                    }
                case JTokenType.Float:
                    {
                        return new ScheduleFilter(fieldId, filterType, ConvertToInternalUnits(valueToken.Value<double>(), fieldSpec));
                    }
                case JTokenType.Boolean:
                    {
                        // Yes/No params are stored as integers (0/1) in Revit
                        return new ScheduleFilter(fieldId, filterType, valueToken.Value<bool>() ? 1 : 0);
                    }
                default:
                    {
                        // Fallback: stringify
                        var s = valueToken.ToString();
                        return new ScheduleFilter(fieldId, filterType, s);
                    }
            }
        }

        private static bool IsDoubleSpec(ForgeTypeId spec)
        {
            if (spec == null) return false;
            try
            {
                if (spec == SpecTypeId.Length
                    || spec == SpecTypeId.Area
                    || spec == SpecTypeId.Volume
                    || spec == SpecTypeId.Angle
                    || spec == SpecTypeId.Number
                    || spec == SpecTypeId.Currency)
                    return true;
            }
            catch { }
            return false;
        }

        private static double ConvertToInternalUnits(double value, ForgeTypeId spec)
        {
            if (spec == null) return value;
            try
            {
                if (spec == SpecTypeId.Length) return value / 304.8;
                if (spec == SpecTypeId.Area) return value / 0.092903;
                if (spec == SpecTypeId.Volume) return value / 0.0283168;
                if (spec == SpecTypeId.Angle) return value * (Math.PI / 180.0);
            }
            catch { }
            return value;
        }
    }
}
