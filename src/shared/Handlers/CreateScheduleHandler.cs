using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateScheduleHandler : IRevitCommand
    {
        public string Name => "create_schedule";

        public string Description =>
            "Create a new schedule from a declarative spec. Supports three field kinds in one transaction: " +
            "parameter (existing Revit param), formula (calculated value field), and combined " +
            "(concatenated parameters with separators). Optional filters, sort/group, and isItemized.";

        public string ParametersSchema =>
            @"{""type"":""object"",""properties"":{""category"":{""type"":""string""},""name"":{""type"":""string""},""fields"":{""type"":""array""},""filters"":{""type"":""array""},""sortGroup"":{""type"":""array""},""isItemized"":{""type"":""boolean"",""default"":true}},""required"":[""category"",""name"",""fields""]}";

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

            var categoryName = request.Value<string>("category");
            var scheduleName = request.Value<string>("name");
            var fieldsToken = request["fields"] as JArray;
            var filtersToken = request["filters"] as JArray;
            var sortGroupToken = request["sortGroup"] as JArray;
            bool? isItemized = null;
            var isItemizedToken = request["isItemized"];
            if (isItemizedToken != null && isItemizedToken.Type != JTokenType.Null)
            {
                try { isItemized = isItemizedToken.Value<bool>(); } catch { }
            }

            if (string.IsNullOrWhiteSpace(categoryName))
                return CommandResult.Fail("category is required.");
            if (string.IsNullOrWhiteSpace(scheduleName))
                return CommandResult.Fail("name is required.");
            if (fieldsToken == null || fieldsToken.Count == 0)
                return CommandResult.Fail("fields is required and must be a non-empty array.");

            // Resolve category via BuiltInCategory enum (case-insensitive name match).
            ElementId categoryId = null;
            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var c = Category.GetCategory(doc, bic);
                    if (c != null && c.Name != null &&
                        c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        categoryId = c.Id;
                        break;
                    }
                }
                catch { }
            }

            if (categoryId == null)
                return CommandResult.Fail($"Category '{categoryName}' not found.");

            using (var tx = new Transaction(doc, "MCP: Create Schedule"))
            {
                tx.Start();
                try
                {
                    ViewSchedule schedule;
                    try
                    {
                        schedule = ViewSchedule.CreateSchedule(doc, categoryId);
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Failed to create schedule: " + ex.Message);
                    }

                    try
                    {
                        schedule.Name = scheduleName;
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail(
                            $"Failed to set schedule name '{scheduleName}': {ex.Message}. " +
                            "Suggestion: pick a unique name (Revit disallows duplicate view names).");
                    }

                    var definition = schedule.Definition;
                    if (definition == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Newly-created schedule has no definition.");
                    }

                    // Cache schedulable fields once for parameter/combined lookup.
                    IList<SchedulableField> schedulableFields;
                    try
                    {
                        schedulableFields = definition.GetSchedulableFields();
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Failed to enumerate schedulable fields: " + ex.Message);
                    }
                    if (schedulableFields == null) schedulableFields = new List<SchedulableField>();

                    // Map each requested field-name → resulting ScheduleFieldId (for filters/sort lookup).
                    var fieldsAdded = new Dictionary<string, ScheduleFieldId>(StringComparer.OrdinalIgnoreCase);
                    var fieldSpecs = new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase);
                    var fieldIdsDto = new Dictionary<string, int>(StringComparer.Ordinal);

                    for (int i = 0; i < fieldsToken.Count; i++)
                    {
                        var entry = fieldsToken[i] as JObject;
                        if (entry == null)
                        {
                            if (tx.HasStarted()) tx.RollBack();
                            return CommandResult.Fail($"fields[{i}] must be an object.");
                        }

                        var kind = entry.Value<string>("type") ?? "parameter";
                        ScheduleField addedField = null;
                        string keyName = null;

                        if (kind.Equals("parameter", StringComparison.OrdinalIgnoreCase))
                        {
                            var paramName = entry.Value<string>("parameterName");
                            if (string.IsNullOrWhiteSpace(paramName))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"fields[{i}].parameterName is required for type=parameter.");
                            }
                            var fieldTypeStr = entry.Value<string>("fieldType"); // optional: "Instance" | "ProjectInfo" | …

                            SchedulableField match = null;
                            foreach (var sf in schedulableFields)
                            {
                                string sfName = null;
                                try { sfName = sf.GetName(doc); } catch { }
                                if (string.IsNullOrEmpty(sfName)) continue;
                                if (!sfName.Equals(paramName, StringComparison.OrdinalIgnoreCase)) continue;

                                if (!string.IsNullOrEmpty(fieldTypeStr))
                                {
                                    string sfTypeStr = null;
                                    try { sfTypeStr = sf.FieldType.ToString(); } catch { }
                                    if (sfTypeStr == null ||
                                        !sfTypeStr.Equals(fieldTypeStr, StringComparison.OrdinalIgnoreCase))
                                        continue;
                                }

                                match = sf;
                                break;
                            }

                            if (match == null)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"No SchedulableField found for parameterName='{paramName}'" +
                                    (string.IsNullOrEmpty(fieldTypeStr) ? string.Empty : $" with fieldType='{fieldTypeStr}'") +
                                    ". Suggestion: call get_schedulable_fields to list valid parameter names. " +
                                    BuildAvailableNamesHint(schedulableFields, doc));
                            }

                            try
                            {
                                addedField = definition.AddField(match);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"Failed to add parameter field '{paramName}': {ex.Message}");
                            }

                            keyName = paramName;
                        }
                        else if (kind.Equals("formula", StringComparison.OrdinalIgnoreCase))
                        {
                            var fName = entry.Value<string>("name");
                            var specShort = entry.Value<string>("specType");
                            var formula = entry.Value<string>("formula");

                            if (string.IsNullOrWhiteSpace(fName))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"fields[{i}].name is required for type=formula.");
                            }
                            if (string.IsNullOrWhiteSpace(formula))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"fields[{i}].formula is required for type=formula.");
                            }

                            ForgeTypeId specTypeId;
                            try
                            {
                                specTypeId = ResolveSpecTypeId(specShort);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"fields[{i}]: {ex.Message}");
                            }

                            try
                            {
                                addedField = AddFormulaField(definition, fName, specTypeId);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"Failed to add formula field '{fName}': {ex.Message}");
                            }

                            try
                            {
                                SetFormula(addedField, formula);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"Failed to set formula for field '{fName}': {ex.Message}. " +
                                    "Check that referenced fields exist in this schedule and operator/types are valid.");
                            }

                            keyName = fName;
                        }
                        else if (kind.Equals("combined", StringComparison.OrdinalIgnoreCase))
                        {
                            var fName = entry.Value<string>("name");
                            var segmentsToken = entry["segments"] as JArray;

                            if (string.IsNullOrWhiteSpace(fName))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"fields[{i}].name is required for type=combined.");
                            }
                            if (segmentsToken == null || segmentsToken.Count == 0)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"fields[{i}].segments is required and must be a non-empty array.");
                            }

                            // Anchor: first String-spec SchedulableField in this category.
                            SchedulableField anchor = PickStringAnchor(schedulableFields, doc);
                            if (anchor == null)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"Could not find a String-spec SchedulableField to anchor combined field '{fName}'.");
                            }

                            try
                            {
                                addedField = definition.AddField(anchor);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"Failed to add anchor for combined field '{fName}': {ex.Message}");
                            }

                            // Set the user-facing name on the combined field.
                            try { addedField.ColumnHeading = fName; } catch { }
                            try
                            {
                                var setNameMi = typeof(ScheduleField).GetMethod(
                                    "SetName", BindingFlags.Public | BindingFlags.Instance,
                                    null, new[] { typeof(string) }, null);
                                if (setNameMi != null) setNameMi.Invoke(addedField, new object[] { fName });
                            }
                            catch { }

                            // Build the segment list through reflection because the concrete
                            // combined-parameter data type name varies in packaged Revit APIs.
                            var segs = CreateCombinedParameterList();
                            for (int sIdx = 0; sIdx < segmentsToken.Count; sIdx++)
                            {
                                var seg = segmentsToken[sIdx] as JObject;
                                if (seg == null)
                                {
                                    if (tx.HasStarted()) tx.RollBack();
                                    return CommandResult.Fail($"fields[{i}].segments[{sIdx}] must be an object.");
                                }

                                var segParamName = seg.Value<string>("parameterName");
                                if (string.IsNullOrWhiteSpace(segParamName))
                                {
                                    if (tx.HasStarted()) tx.RollBack();
                                    return CommandResult.Fail(
                                        $"fields[{i}].segments[{sIdx}].parameterName is required.");
                                }

                                ElementId segParamId = ResolveParameterIdForCombined(schedulableFields, doc, segParamName);
                                if (segParamId == null)
                                {
                                    if (tx.HasStarted()) tx.RollBack();
                                    return CommandResult.Fail(
                                        $"Combined segment parameter '{segParamName}' not found among schedulable fields. " +
                                        "Suggestion: call get_schedulable_fields. " +
                                        BuildAvailableNamesHint(schedulableFields, doc));
                                }

                                object cpd;
                                try
                                {
                                    cpd = CreateCombinedParameterData(segParamId);
                                }
                                catch (Exception ex)
                                {
                                    if (tx.HasStarted()) tx.RollBack();
                                    return CommandResult.Fail(
                                        $"Failed to build CombinedParameterData for '{segParamName}': {ex.Message}");
                                }

                                TrySetStringProperty(cpd, "Prefix", seg.Value<string>("prefix") ?? string.Empty);
                                TrySetStringProperty(cpd, "Separator", seg.Value<string>("separator") ?? string.Empty);
                                TrySetStringProperty(cpd, "Suffix", seg.Value<string>("suffix") ?? string.Empty);

                                var truncToken = seg["truncate"];
                                if (truncToken != null && truncToken.Type != JTokenType.Null)
                                {
                                    try { TrySetBoolProperty(cpd, "Truncated", truncToken.Value<bool>()); } catch { }
                                }
                                var nocToken = seg["numberOfCharacters"];
                                if (nocToken != null && nocToken.Type != JTokenType.Null)
                                {
                                    try { TrySetIntProperty(cpd, "NumberOfCharacters", nocToken.Value<int>()); } catch { }
                                }

                                segs.Add(cpd);
                            }

                            try
                            {
                                SetCombinedParameters(addedField, segs);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"Failed to set combined parameters on field '{fName}': {ex.Message}");
                            }

                            keyName = fName;
                        }
                        else
                        {
                            if (tx.HasStarted()) tx.RollBack();
                            return CommandResult.Fail(
                                $"fields[{i}].type must be one of: parameter, formula, combined (got '{kind}').");
                        }

                        // Optional per-field overrides.
                        var columnHeading = entry.Value<string>("columnHeading");
                        if (!string.IsNullOrEmpty(columnHeading))
                        {
                            try { addedField.ColumnHeading = columnHeading; } catch { }
                        }
                        var hiddenToken = entry["hidden"];
                        if (hiddenToken != null && hiddenToken.Type != JTokenType.Null)
                        {
                            try { addedField.IsHidden = hiddenToken.Value<bool>(); } catch { }
                        }

                        if (!string.IsNullOrEmpty(keyName) && !fieldsAdded.ContainsKey(keyName))
                        {
                            fieldsAdded[keyName] = addedField.FieldId;
                            try { fieldSpecs[keyName] = addedField.GetSpecTypeId(); } catch { }
                            fieldIdsDto[keyName] = FindFieldIndex(definition, addedField.FieldId);
                        }
                    }

                    // Filters.
                    int filtersApplied = 0;
                    if (filtersToken != null && filtersToken.Count > 0)
                    {
                        for (int i = 0; i < filtersToken.Count; i++)
                        {
                            var fSpec = filtersToken[i] as JObject;
                            if (fSpec == null)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"filters[{i}] must be an object.");
                            }
                            var fName = fSpec.Value<string>("fieldName");
                            var opStr = fSpec.Value<string>("operator");
                            var valueToken = fSpec["value"];

                            if (string.IsNullOrWhiteSpace(fName))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"filters[{i}].fieldName is required.");
                            }
                            if (string.IsNullOrWhiteSpace(opStr))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"filters[{i}].operator is required.");
                            }
                            if (!fieldsAdded.TryGetValue(fName, out var fieldIdForFilter))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"filters[{i}].fieldName '{fName}' did not match any added field.");
                            }

                            ScheduleFilterType filterType;
                            try { filterType = ResolveFilterType(opStr); }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"filters[{i}]: {ex.Message}");
                            }

                            ScheduleFilter scheduleFilter;
                            try
                            {
                                fieldSpecs.TryGetValue(fName, out var fieldSpecForFilter);
                                scheduleFilter = BuildScheduleFilter(fieldIdForFilter, filterType, valueToken, fieldSpecForFilter);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"filters[{i}]: failed to build filter: {ex.Message}");
                            }

                            try
                            {
                                definition.AddFilter(scheduleFilter);
                                filtersApplied++;
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"filters[{i}]: AddFilter failed: {ex.Message}");
                            }
                        }
                    }

                    // Sort/group.
                    int sortGroupApplied = 0;
                    if (sortGroupToken != null && sortGroupToken.Count > 0)
                    {
                        for (int i = 0; i < sortGroupToken.Count; i++)
                        {
                            var sSpec = sortGroupToken[i] as JObject;
                            if (sSpec == null)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"sortGroup[{i}] must be an object.");
                            }
                            var fName = sSpec.Value<string>("fieldName");
                            if (string.IsNullOrWhiteSpace(fName))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail($"sortGroup[{i}].fieldName is required.");
                            }
                            if (!fieldsAdded.TryGetValue(fName, out var fieldIdForSort))
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"sortGroup[{i}].fieldName '{fName}' did not match any added field.");
                            }

                            bool ascending = true;
                            var ascToken = sSpec["ascending"];
                            if (ascToken != null && ascToken.Type != JTokenType.Null)
                            {
                                try { ascending = ascToken.Value<bool>(); } catch { }
                            }

                            ScheduleSortGroupField sortField;
                            try
                            {
                                sortField = new ScheduleSortGroupField(
                                    fieldIdForSort,
                                    ascending ? ScheduleSortOrder.Ascending : ScheduleSortOrder.Descending);
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"sortGroup[{i}]: failed to build sort field: {ex.Message}");
                            }

                            var showHeaderToken = sSpec["showHeader"];
                            if (showHeaderToken != null && showHeaderToken.Type != JTokenType.Null)
                            {
                                try { sortField.ShowHeader = showHeaderToken.Value<bool>(); } catch { }
                            }
                            var showFooterToken = sSpec["showFooter"];
                            if (showFooterToken != null && showFooterToken.Type != JTokenType.Null)
                            {
                                try { sortField.ShowFooter = showFooterToken.Value<bool>(); } catch { }
                            }
                            var showBlankToken = sSpec["showBlankLine"];
                            if (showBlankToken != null && showBlankToken.Type != JTokenType.Null)
                            {
                                try { sortField.ShowBlankLine = showBlankToken.Value<bool>(); } catch { }
                            }

                            try
                            {
                                definition.AddSortGroupField(sortField);
                                sortGroupApplied++;
                            }
                            catch (Exception ex)
                            {
                                if (tx.HasStarted()) tx.RollBack();
                                return CommandResult.Fail(
                                    $"sortGroup[{i}]: AddSortGroupField failed: {ex.Message}");
                            }
                        }
                    }

                    if (isItemized.HasValue)
                    {
                        try { definition.IsItemized = isItemized.Value; } catch { }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        scheduleId = RevitCompat.GetId(schedule.Id),
                        scheduleName = schedule.Name,
                        fieldIds = fieldIdsDto,
                        filtersApplied = filtersApplied,
                        sortGroupApplied = sortGroupApplied
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create schedule: " + ex.Message);
                }
            }
        }

        // -- helpers ---------------------------------------------------------

        private static int FindFieldIndex(ScheduleDefinition definition, ScheduleFieldId fieldId)
        {
            if (definition == null || fieldId == null) return -1;
            try
            {
                var order = definition.GetFieldOrder();
                if (order == null) return -1;
                for (var i = 0; i < order.Count; i++)
                {
                    if (object.Equals(order[i], fieldId)) return i;
                }
            }
            catch { }
            return -1;
        }

        private static ForgeTypeId ResolveSpecTypeId(string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName))
                throw new ArgumentException("specType is required for formula fields (e.g. Length, Area, Number, String).");

            switch (shortName.Trim().ToLowerInvariant())
            {
                case "length":   return SpecTypeId.Length;
                case "area":     return SpecTypeId.Area;
                case "volume":   return SpecTypeId.Volume;
                case "number":   return SpecTypeId.Number;
                case "integer":  return GetIntegerSpecTypeId();
                case "string":   return GetStringSpecTypeId();
                case "angle":    return SpecTypeId.Angle;
                case "currency": return SpecTypeId.Currency;
                default:
                    throw new ArgumentException(
                        $"Unsupported specType '{shortName}'. Allowed: Length, Area, Volume, Number, Integer, String, Angle, Currency.");
            }
        }

        private static ForgeTypeId GetIntegerSpecTypeId()
        {
            var nested = typeof(SpecTypeId).GetNestedType("Int", BindingFlags.Public | BindingFlags.Static);
            var prop = nested?.GetProperty("Integer", BindingFlags.Public | BindingFlags.Static);
            var value = prop?.GetValue(null) as ForgeTypeId;
            if (value == null)
                throw new ArgumentException("SpecTypeId.Int.Integer is unavailable in this Revit API version.");
            return value;
        }

        private static ForgeTypeId GetStringSpecTypeId()
        {
            var nested = typeof(SpecTypeId).GetNestedType("String", BindingFlags.Public | BindingFlags.Static);
            var prop = nested?.GetProperty("Text", BindingFlags.Public | BindingFlags.Static);
            var value = prop?.GetValue(null) as ForgeTypeId;
            if (value == null)
                throw new ArgumentException("SpecTypeId.String.Text is unavailable in this Revit API version.");
            return value;
        }

        private static ScheduleFilterType ResolveFilterType(string opStr)
        {
            switch (opStr.Trim().ToLowerInvariant())
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
                    throw new ArgumentException(
                        $"Unsupported filter operator '{opStr}'. Allowed: equals, notEqual, greaterThan, greaterThanOrEqual, " +
                        "lessThan, lessThanOrEqual, contains, notContains, beginsWith, notBeginsWith, endsWith, notEndsWith, " +
                        "isEmpty, isNotEmpty, hasParameter, hasNoParameter.");
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

        private static bool IsNoValueFilter(ScheduleFilterType t)
        {
            var name = t.ToString();
            return name.Equals("IsEmpty", StringComparison.OrdinalIgnoreCase)
                || name.Equals("IsNotEmpty", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HasParameter", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HasNoParameter", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HasValue", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HasNoValue", StringComparison.OrdinalIgnoreCase);
        }

        private static ScheduleFilter BuildScheduleFilter(ScheduleFieldId fieldId, ScheduleFilterType filterType, JToken valueToken, ForgeTypeId fieldSpec)
        {
            if (IsNoValueFilter(filterType))
            {
                // No-value ctor: ScheduleFilter(ScheduleFieldId, ScheduleFilterType)
                return new ScheduleFilter(fieldId, filterType);
            }

            if (valueToken == null || valueToken.Type == JTokenType.Null)
                throw new ArgumentException($"value is required for filter operator '{filterType}'.");

            switch (valueToken.Type)
            {
                case JTokenType.String:
                    if (IsDoubleSpec(fieldSpec) && double.TryParse(valueToken.Value<string>(), out var parsedDouble))
                        return new ScheduleFilter(fieldId, filterType, ConvertToInternalUnits(parsedDouble, fieldSpec));
                    return new ScheduleFilter(fieldId, filterType, valueToken.Value<string>());
                case JTokenType.Integer:
                    if (IsDoubleSpec(fieldSpec))
                        return new ScheduleFilter(fieldId, filterType, ConvertToInternalUnits(valueToken.Value<double>(), fieldSpec));
                    return new ScheduleFilter(fieldId, filterType, valueToken.Value<int>());
                case JTokenType.Float:
                    return new ScheduleFilter(fieldId, filterType, ConvertToInternalUnits(valueToken.Value<double>(), fieldSpec));
                case JTokenType.Boolean:
                    return new ScheduleFilter(fieldId, filterType, valueToken.Value<bool>() ? 1 : 0);
                default:
                    // Last-resort: stringify
                    return new ScheduleFilter(fieldId, filterType, valueToken.ToString());
            }
        }

        private static bool IsDoubleSpec(ForgeTypeId spec)
        {
            if (spec == null) return false;
            try
            {
                return spec == SpecTypeId.Length
                    || spec == SpecTypeId.Area
                    || spec == SpecTypeId.Volume
                    || spec == SpecTypeId.Angle
                    || spec == SpecTypeId.Number
                    || spec == SpecTypeId.Currency;
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

        private static SchedulableField PickStringAnchor(IList<SchedulableField> all, Document doc)
        {
            foreach (var sf in all)
            {
                try
                {
                    var mi = sf.GetType().GetMethod("GetSpecTypeId", BindingFlags.Public | BindingFlags.Instance);
                    if (mi == null) continue;
                    var ftid = mi.Invoke(sf, null);
                    if (ftid == null) continue;
                    var typeIdProp = ftid.GetType().GetProperty("TypeId", BindingFlags.Public | BindingFlags.Instance);
                    if (typeIdProp == null) continue;
                    var typeIdStr = typeIdProp.GetValue(ftid) as string;
                    if (string.IsNullOrEmpty(typeIdStr)) continue;
                    if (typeIdStr.IndexOf("spec.string", StringComparison.OrdinalIgnoreCase) >= 0)
                        return sf;
                }
                catch { }
            }

            // Fallback: any field tied to a common string built-in (Type Mark / Mark / Family Name / Type Name).
            string[] preferred = { "Type Mark", "Mark", "Family", "Family Name", "Type", "Type Name", "Comments" };
            foreach (var prefName in preferred)
            {
                foreach (var sf in all)
                {
                    string n = null;
                    try { n = sf.GetName(doc); } catch { }
                    if (!string.IsNullOrEmpty(n) && n.Equals(prefName, StringComparison.OrdinalIgnoreCase))
                        return sf;
                }
            }

            return all.Count > 0 ? all[0] : null;
        }

        private static ScheduleField AddFormulaField(ScheduleDefinition definition, string name, ForgeTypeId specTypeId)
        {
            var method = typeof(ScheduleDefinition).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "AddField") return false;
                    var ps = m.GetParameters();
                    return ps.Length == 3
                        && ps[0].ParameterType == typeof(ScheduleFieldType)
                        && ps[1].ParameterType == typeof(string)
                        && ps[2].ParameterType == typeof(ForgeTypeId);
                });

            if (method == null)
                throw new InvalidOperationException("This Revit API version does not expose AddField(ScheduleFieldType, string, ForgeTypeId) for formula fields.");

            return (ScheduleField)method.Invoke(definition, new object[] { ScheduleFieldType.Formula, name, specTypeId });
        }

        private static void SetFormula(ScheduleField field, string formula)
        {
            var method = typeof(ScheduleField).GetMethod("SetFormula", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (method != null)
            {
                method.Invoke(field, new object[] { formula });
                return;
            }

            var prop = typeof(ScheduleField).GetProperty("Formula", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(field, formula);
                return;
            }

            throw new InvalidOperationException("This Revit API version does not expose a formula setter.");
        }

        private static ElementId ResolveParameterIdForCombined(IList<SchedulableField> all, Document doc, string paramName)
        {
            foreach (var sf in all)
            {
                string n = null;
                try { n = sf.GetName(doc); } catch { }
                if (!string.IsNullOrEmpty(n) && n.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                {
                    try { return sf.ParameterId; } catch { return null; }
                }
            }
            return null;
        }

        private static System.Collections.IList CreateCombinedParameterList()
        {
            var dataType = GetCombinedParameterDataType();
            var listType = typeof(List<>).MakeGenericType(dataType);
            return (System.Collections.IList)Activator.CreateInstance(listType);
        }

        private static Type GetCombinedParameterDataType()
        {
            var method = typeof(ScheduleField).GetMethod("GetCombinedParameters", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
                throw new InvalidOperationException("ScheduleField.GetCombinedParameters is unavailable in this Revit API version.");

            var returnType = method.ReturnType;
            if (returnType.IsGenericType)
                return returnType.GetGenericArguments()[0];

            foreach (var iface in returnType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericArguments().Length == 1)
                    return iface.GetGenericArguments()[0];
            }

            throw new InvalidOperationException("Could not determine the combined-parameter data element type.");
        }

        private static object CreateCombinedParameterData(ElementId paramId)
        {
            var t = GetCombinedParameterDataType();
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (mi.Name != "Create") continue;
                var ps = mi.GetParameters();
                if (ps.Length == 0) continue;
                if (ps[0].ParameterType != typeof(ElementId)) continue;

                var args = new object[ps.Length];
                args[0] = paramId;
                for (int j = 1; j < ps.Length; j++)
                {
                    if (ps[j].ParameterType == typeof(ElementId))
                        args[j] = ElementId.InvalidElementId;
                    else if (ps[j].HasDefaultValue)
                        args[j] = ps[j].DefaultValue;
                    else
                        args[j] = ps[j].ParameterType.IsValueType
                            ? Activator.CreateInstance(ps[j].ParameterType)
                            : null;
                }

                try
                {
                    return mi.Invoke(null, args);
                }
                catch { }
            }

            throw new InvalidOperationException("Could not invoke CombinedParameterData.Create with any known signature.");
        }

        private static void SetCombinedParameters(ScheduleField field, System.Collections.IList combinedList)
        {
            var method = typeof(ScheduleField).GetMethod("SetCombinedParameters", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                throw new InvalidOperationException("ScheduleField.SetCombinedParameters is unavailable in this Revit API version.");

            method.Invoke(field, new object[] { combinedList });
        }

        private static void TrySetStringProperty(object target, string prop, string value)
        {
            try
            {
                var p = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(target, value ?? string.Empty);
            }
            catch { }
        }

        private static void TrySetBoolProperty(object target, string prop, bool value)
        {
            try
            {
                var p = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(target, value);
            }
            catch { }
        }

        private static void TrySetIntProperty(object target, string prop, int value)
        {
            try
            {
                var p = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(target, value);
            }
            catch { }
        }

        private static string BuildAvailableNamesHint(IList<SchedulableField> all, Document doc)
        {
            var names = new List<string>();
            foreach (var sf in all)
            {
                string n = null;
                try { n = sf.GetName(doc); } catch { }
                if (!string.IsNullOrEmpty(n)) names.Add(n);
                if (names.Count >= 20) break;
            }
            if (names.Count == 0) return string.Empty;
            return "Available (first " + names.Count + "): " + string.Join(", ", names) + ".";
        }
    }
}
