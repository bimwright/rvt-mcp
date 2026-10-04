using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AddScheduleFieldHandler : IRevitCommand
    {
        public string Name => "add_schedule_field";
        public string Description => "Add one new field to an existing schedule. Supports parameter, formula, or combined-parameter kinds via a discriminated-union spec. Optional insertIndex, columnHeading, hidden, columnWidth (mm).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""field"":{""type"":""object""},""insertIndex"":{""type"":""integer""},""columnHeading"":{""type"":""string""},""hidden"":{""type"":""boolean"",""default"":false},""columnWidth"":{""type"":""number""}},""required"":[""field""]}";

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

            var scheduleIdToken = request["scheduleId"];
            var scheduleName = request.Value<string>("scheduleName");
            var fieldToken = request["field"] as JObject;
            var insertIndexToken = request["insertIndex"];
            var columnHeading = request.Value<string>("columnHeading");
            var hiddenToken = request["hidden"];
            var columnWidthToken = request["columnWidth"];

            if (fieldToken == null)
                return CommandResult.Fail("field is required (object).");

            var fieldKind = fieldToken.Value<string>("type");
            if (string.IsNullOrEmpty(fieldKind))
                return CommandResult.Fail("field.type is required: one of 'parameter', 'formula', 'combined'.");

            fieldKind = fieldKind.ToLowerInvariant();
            if (fieldKind != "parameter" && fieldKind != "formula" && fieldKind != "combined")
                return CommandResult.Fail($"field.type must be one of 'parameter', 'formula', 'combined'. Got '{fieldKind}'.");

            // Resolve ViewSchedule by id or name
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
                    return CommandResult.Fail("Ambiguous schedule name '" + scheduleName + "': " + matches.Count + " matches. Use scheduleId.");

                schedule = matches[0];
            }
            else
            {
                return CommandResult.Fail("Either scheduleId or scheduleName is required.");
            }

            var definition = schedule.Definition;
            if (definition == null)
                return CommandResult.Fail("Schedule has no definition.");

            using (var tx = new Transaction(doc, "MCP: Add Schedule Field"))
            {
                tx.Start();
                try
                {
                    ScheduleField addedField = null;
                    switch (fieldKind)
                    {
                        case "parameter":
                            addedField = AddParameterField(doc, definition, fieldToken);
                            break;
                        case "formula":
                            addedField = AddFormulaField(definition, fieldToken);
                            break;
                        case "combined":
                            addedField = AddCombinedField(doc, definition, fieldToken);
                            break;
                    }

                    if (addedField == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Field addition returned null.");
                    }

                    // Apply optional properties
                    if (!string.IsNullOrEmpty(columnHeading))
                    {
                        try { addedField.ColumnHeading = columnHeading; } catch { }
                    }

                    if (hiddenToken != null && hiddenToken.Type == JTokenType.Boolean)
                    {
                        try { addedField.IsHidden = hiddenToken.Value<bool>(); } catch { }
                    }

                    if (columnWidthToken != null &&
                        (columnWidthToken.Type == JTokenType.Float || columnWidthToken.Type == JTokenType.Integer))
                    {
                        try
                        {
                            var widthMm = columnWidthToken.Value<double>();
                            if (widthMm > 0.0)
                                TrySetDoubleMember(addedField, "ColumnWidth", widthMm / 304.8); // mm -> feet
                        }
                        catch { }
                    }

                    // Insert at clamped index, if requested
                    int finalIndex;
                    if (insertIndexToken != null && insertIndexToken.Type == JTokenType.Integer)
                    {
                        finalIndex = MoveFieldToIndex(definition, addedField, insertIndexToken.Value<int>());
                    }
                    else
                    {
                        finalIndex = GetFieldIndex(definition, addedField);
                    }

                    string finalName = null;
                    try { finalName = addedField.GetName(); } catch { }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        scheduleId = RevitCompat.GetId(schedule.Id),
                        fieldId = finalIndex,
                        fieldIndex = finalIndex,
                        fieldName = finalName,
                        type = fieldKind
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to add schedule field: {ex.Message}");
                }
            }
        }

        // ---- field kinds ----

        private static ScheduleField AddParameterField(Document doc, ScheduleDefinition definition, JObject spec)
        {
            var parameterName = spec.Value<string>("parameterName");
            if (string.IsNullOrEmpty(parameterName))
                throw new InvalidOperationException("field.parameterName is required for type 'parameter'.");

            var fieldTypeStr = spec.Value<string>("fieldType"); // optional

            IList<SchedulableField> all;
            try { all = definition.GetSchedulableFields(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to enumerate schedulable fields: " + ex.Message);
            }
            if (all == null) all = new List<SchedulableField>();

            SchedulableField match = null;
            var availableNames = new List<string>();
            foreach (var sf in all)
            {
                string name = null;
                try { name = sf.GetName(doc); } catch { name = null; }
                if (string.IsNullOrEmpty(name)) continue;
                availableNames.Add(name);

                if (!name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(fieldTypeStr))
                {
                    if (!sf.FieldType.ToString().Equals(fieldTypeStr, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                match = sf;
                break;
            }

            if (match == null)
            {
                var sorted = availableNames
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToArray();
                var listStr = sorted.Length == 0 ? "(none)" : string.Join(", ", sorted);
                throw new InvalidOperationException(
                    $"SchedulableField '{parameterName}'" +
                    (string.IsNullOrEmpty(fieldTypeStr) ? "" : $" (fieldType={fieldTypeStr})") +
                    $" not found. Available (first 20): {listStr}");
            }

            return definition.AddField(match);
        }

        private static ScheduleField AddFormulaField(ScheduleDefinition definition, JObject spec)
        {
            var name = spec.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("field.name is required for type 'formula'.");

            var specTypeShort = spec.Value<string>("specType");
            if (string.IsNullOrEmpty(specTypeShort))
                throw new InvalidOperationException("field.specType is required for type 'formula' (Length, Area, Volume, Number, Integer, String, Angle, Currency).");

            var formula = spec.Value<string>("formula");
            if (string.IsNullOrEmpty(formula))
                throw new InvalidOperationException("field.formula is required for type 'formula'.");

            var specTypeId = ResolveSpecTypeId(specTypeShort);

            ScheduleField field;
            try
            {
                field = AddFormulaField(definition, name, specTypeId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"AddField(Formula) failed: {ex.Message}");
            }

            try
            {
                SetFormula(field, formula);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SetFormula failed (invalid formula '{formula}'): {ex.Message}");
            }

            return field;
        }

        private static ScheduleField AddCombinedField(Document doc, ScheduleDefinition definition, JObject spec)
        {
            var name = spec.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("field.name is required for type 'combined'.");

            // specType is accepted but unused — combined fields take their type from the anchor.
            var segmentsToken = spec["segments"] as JArray;
            if (segmentsToken == null || segmentsToken.Count == 0)
                throw new InvalidOperationException("field.segments (non-empty array) is required for type 'combined'.");

            // Pick anchor SchedulableField: first String-spec schedulable field.
            // TODO: Combined fields need an anchor SchedulableField — Revit derives the field's
            // category/storage from it. We prefer a string-spec field so the combined output
            // (always a string) does not conflict with units of a numeric anchor.
            IList<SchedulableField> all;
            try { all = definition.GetSchedulableFields(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to enumerate schedulable fields: " + ex.Message);
            }
            if (all == null || all.Count == 0)
                throw new InvalidOperationException("No SchedulableFields available to anchor combined field.");

            SchedulableField anchor = null;
            foreach (var sf in all)
            {
                if (HasStringSpec(sf))
                {
                    anchor = sf;
                    break;
                }
            }
            if (anchor == null) anchor = all[0]; // last-resort fallback

            ScheduleField field;
            try
            {
                field = definition.AddField(anchor);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"AddField(anchor) failed: {ex.Message}");
            }

            // Rename to requested name. Combined fields support SetName via column heading or Name property.
            try { field.ColumnHeading = name; } catch { }

            // Build the strongly typed combined-parameter list through reflection because
            // the public type name differs across packaged Revit API references.
            var combinedList = CreateCombinedParameterList();
            foreach (var segToken in segmentsToken)
            {
                var segObj = segToken as JObject;
                if (segObj == null) continue;

                var segParamName = segObj.Value<string>("parameterName");
                if (string.IsNullOrEmpty(segParamName))
                    throw new InvalidOperationException("Each combined segment requires parameterName.");

                var prefix = segObj.Value<string>("prefix") ?? string.Empty;
                var separator = segObj.Value<string>("separator") ?? string.Empty;
                var suffix = segObj.Value<string>("suffix") ?? string.Empty;

                // Find SchedulableField for this segment so we can grab its ParameterId.
                ElementId paramId = ElementId.InvalidElementId;
                foreach (var sf in all)
                {
                    string n = null;
                    try { n = sf.GetName(doc); } catch { n = null; }
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.Equals(segParamName, StringComparison.OrdinalIgnoreCase))
                    {
                        paramId = sf.ParameterId;
                        break;
                    }
                }
                if (paramId == ElementId.InvalidElementId)
                    throw new InvalidOperationException(
                        $"Combined segment parameter '{segParamName}' not found among schedulable fields.");

                var cpd = CreateCombinedParameterData(paramId);
                if (cpd == null)
                    throw new InvalidOperationException(
                        $"CombinedParameterData.Create failed for '{segParamName}'.");

                TrySetSegmentProperty(cpd, "Prefix", prefix);
                TrySetSegmentProperty(cpd, "Separator", separator);
                TrySetSegmentProperty(cpd, "Suffix", suffix);
                try
                {
                    var truncToken = segObj["truncated"] ?? segObj["truncate"];
                    if (truncToken != null && truncToken.Type == JTokenType.Boolean)
                        TrySetSegmentProperty(cpd, "Truncated", truncToken.Value<bool>());
                }
                catch { }
                try
                {
                    var countToken = segObj["numberOfCharacters"];
                    if (countToken != null && countToken.Type == JTokenType.Integer)
                        TrySetSegmentProperty(cpd, "NumberOfCharacters", countToken.Value<int>());
                }
                catch { }

                combinedList.Add(cpd);
            }

            if (combinedList.Count == 0)
                throw new InvalidOperationException("No valid combined segments produced.");

            try
            {
                SetCombinedParameters(field, combinedList);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SetCombinedParameters failed: {ex.Message}");
            }

            return field;
        }

        // ---- helpers ----

        private static ForgeTypeId ResolveSpecTypeId(string shortName)
        {
            switch ((shortName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "length": return SpecTypeId.Length;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                case "number": return SpecTypeId.Number;
                case "integer": return GetIntegerSpecTypeId();
                case "string": return GetStringSpecTypeId();
                case "angle": return SpecTypeId.Angle;
                case "currency": return SpecTypeId.Currency;
                default:
                    throw new InvalidOperationException(
                        $"Unknown specType '{shortName}'. Use one of: Length, Area, Volume, Number, Integer, String, Angle, Currency.");
            }
        }

        private static ForgeTypeId GetIntegerSpecTypeId()
        {
            var nested = typeof(SpecTypeId).GetNestedType("Int", BindingFlags.Public | BindingFlags.Static);
            var prop = nested?.GetProperty("Integer", BindingFlags.Public | BindingFlags.Static);
            var value = prop?.GetValue(null) as ForgeTypeId;
            if (value == null)
                throw new InvalidOperationException("SpecTypeId.Int.Integer is unavailable in this Revit API version.");
            return value;
        }

        private static ForgeTypeId GetStringSpecTypeId()
        {
            var nested = typeof(SpecTypeId).GetNestedType("String", BindingFlags.Public | BindingFlags.Static);
            var prop = nested?.GetProperty("Text", BindingFlags.Public | BindingFlags.Static);
            var value = prop?.GetValue(null) as ForgeTypeId;
            if (value == null)
                throw new InvalidOperationException("SpecTypeId.String.Text is unavailable in this Revit API version.");
            return value;
        }

        private static bool HasStringSpec(SchedulableField sf)
        {
            try
            {
                var mi = sf.GetType().GetMethod("GetSpecTypeId", BindingFlags.Public | BindingFlags.Instance);
                if (mi == null) return false;
                var ftid = mi.Invoke(sf, null) as ForgeTypeId;
                if (ftid == null) return false;
                var tid = ftid.TypeId;
                if (string.IsNullOrEmpty(tid)) return false;
                return tid.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0
                    || tid.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
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

        private static void TrySetDoubleMember(object target, string memberName, double value)
        {
            try
            {
                var prop = target.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) prop.SetValue(target, value);
            }
            catch { }
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
            try
            {
                var type = GetCombinedParameterDataType();
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Create")
                    .ToArray();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(ElementId))
                        return m.Invoke(null, new object[] { paramId });
                    if (ps.Length == 2 &&
                        ps[0].ParameterType == typeof(ElementId) &&
                        ps[1].ParameterType == typeof(ElementId))
                        return m.Invoke(null,
                            new object[] { paramId, ElementId.InvalidElementId });
                }
            }
            catch { }

            return null;
        }

        private static void SetCombinedParameters(ScheduleField field, System.Collections.IList combinedList)
        {
            var method = typeof(ScheduleField).GetMethod("SetCombinedParameters", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                throw new InvalidOperationException("ScheduleField.SetCombinedParameters is unavailable in this Revit API version.");

            method.Invoke(field, new object[] { combinedList });
        }

        private static void TrySetSegmentProperty(object cpd, string propName, string value)
        {
            if (cpd == null) return;
            try
            {
                var prop = cpd.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite) return;
                prop.SetValue(cpd, value ?? string.Empty);
            }
            catch { }
        }

        private static void TrySetSegmentProperty(object cpd, string propName, bool value)
        {
            if (cpd == null) return;
            try
            {
                var prop = cpd.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite) return;
                prop.SetValue(cpd, value);
            }
            catch { }
        }

        private static void TrySetSegmentProperty(object cpd, string propName, int value)
        {
            if (cpd == null) return;
            try
            {
                var prop = cpd.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite) return;
                prop.SetValue(cpd, value);
            }
            catch { }
        }

        private static int GetFieldIndex(ScheduleDefinition definition, ScheduleField field)
        {
            try
            {
                var order = definition.GetFieldOrder();
                if (order == null) return -1;
                for (int i = 0; i < order.Count; i++)
                {
                    var candidate = definition.GetField(order[i]);
                    if (candidate == null) continue;
                    if (ReferenceEquals(candidate, field)) return i;
                    if (candidate.FieldId != null && field.FieldId != null &&
                        object.Equals(candidate.FieldId, field.FieldId))
                        return i;
                }
            }
            catch { }
            return -1;
        }

        private static int MoveFieldToIndex(ScheduleDefinition definition, ScheduleField field, int requestedIndex)
        {
            IList<ScheduleFieldId> order;
            try { order = definition.GetFieldOrder(); }
            catch { return GetFieldIndex(definition, field); }
            if (order == null || order.Count == 0) return GetFieldIndex(definition, field);

            // Identify the just-added field — newest field is normally at the end.
            ScheduleFieldId addedId = null;
            for (int i = order.Count - 1; i >= 0; i--)
            {
                ScheduleField candidate = null;
                try { candidate = definition.GetField(order[i]); } catch { candidate = null; }
                if (candidate == null) continue;
                if (ReferenceEquals(candidate, field) ||
                    (candidate.FieldId != null && field.FieldId != null &&
                     object.Equals(candidate.FieldId, field.FieldId)))
                {
                    addedId = order[i];
                    break;
                }
            }
            if (addedId == null) return GetFieldIndex(definition, field);

            // Clamp index to [0, count-1] (count stays the same since we move, not insert).
            int count = order.Count;
            int clamped = requestedIndex;
            if (clamped < 0) clamped = 0;
            if (clamped > count - 1) clamped = count - 1;

            var newOrder = new List<ScheduleFieldId>(order);
            newOrder.Remove(addedId);
            newOrder.Insert(clamped, addedId);

            try
            {
                definition.SetFieldOrder(newOrder);
            }
            catch
            {
                // Silently keep original order — return current index.
                return GetFieldIndex(definition, field);
            }

            return clamped;
        }
    }
}
