using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class UpdateScheduleFieldHandler : IRevitCommand
    {
        public string Name => "update_schedule_field";

        public string Description =>
            "Modify an existing schedule field's properties: columnHeading, hidden, columnWidth, " +
            "horizontalAlignment, headingOrientation, formula (only if calculated), combinedParameters " +
            "(only if combined), isTotal, isPercentage, displayType. Cannot change the underlying " +
            "parameter of a parameter field — use remove + add instead.";

        public string ParametersSchema =>
            @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""fieldRef"":{""type"":""object"",""properties"":{""fieldId"":{""type"":""integer""},""fieldName"":{""type"":""string""}}},""changes"":{""type"":""object""}},""required"":[""fieldRef"",""changes""]}";

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

            // Resolve ViewSchedule
            var scheduleIdToken = request["scheduleId"];
            var scheduleName = request.Value<string>("scheduleName");

            ViewSchedule schedule = null;
            if (scheduleIdToken != null && scheduleIdToken.Type != JTokenType.Null)
            {
                long idValue;
                try { idValue = scheduleIdToken.Value<long>(); }
                catch { return CommandResult.Fail("scheduleId must be an integer."); }

                if (!RevitCompat.CanRepresentElementId(idValue))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(idValue));

                Element el = null;
                try { el = doc.GetElement(RevitCompat.ToElementId(idValue)); }
                catch (Exception ex) { return CommandResult.Fail("Failed to resolve schedule by id: " + ex.Message); }

                schedule = el as ViewSchedule;
                if (schedule == null)
                    return CommandResult.Fail("Element " + idValue + " is not a ViewSchedule or not found.");
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
                return CommandResult.Fail("Either scheduleId or scheduleName is required.");
            }

            var definition = schedule.Definition;
            if (definition == null)
                return CommandResult.Fail("Schedule has no definition.");

            // Resolve fieldRef
            var fieldRefToken = request["fieldRef"] as JObject;
            if (fieldRefToken == null)
                return CommandResult.Fail("fieldRef is required.");

            var fieldIdToken = fieldRefToken["fieldIndex"] ?? fieldRefToken["fieldId"];
            var fieldNameRef = fieldRefToken.Value<string>("fieldName");

            IList<ScheduleFieldId> fieldOrder;
            try { fieldOrder = definition.GetFieldOrder(); }
            catch (Exception ex) { return CommandResult.Fail("Failed to read field order: " + ex.Message); }
            if (fieldOrder == null) fieldOrder = new List<ScheduleFieldId>();

            ScheduleField targetField = null;
            ScheduleFieldId targetFieldId = null;

            if (fieldIdToken != null && fieldIdToken.Type != JTokenType.Null)
            {
                int fieldIndex;
                try { fieldIndex = fieldIdToken.Value<int>(); }
                catch { return CommandResult.Fail("fieldRef.fieldIndex/fieldId must be an integer field index."); }

                if (fieldIndex < 0 || fieldIndex >= fieldOrder.Count)
                    return CommandResult.Fail("Field index " + fieldIndex + " is outside the schedule field range 0.." + (fieldOrder.Count - 1) + ".");

                targetFieldId = fieldOrder[fieldIndex];
                try { targetField = definition.GetField(targetFieldId); }
                catch (Exception ex) { return CommandResult.Fail("Failed to resolve field index " + fieldIndex + ": " + ex.Message); }

                if (targetField == null)
                    return CommandResult.Fail("Field at index " + fieldIndex + " not found in schedule.");
            }
            else if (!string.IsNullOrWhiteSpace(fieldNameRef))
            {
                foreach (var fid in fieldOrder)
                {
                    ScheduleField sf;
                    try { sf = definition.GetField(fid); }
                    catch { continue; }
                    if (sf == null) continue;

                    string nm = null;
                    try { nm = sf.GetName(); } catch { nm = null; }
                    if (nm != null && nm.Equals(fieldNameRef, StringComparison.OrdinalIgnoreCase))
                    {
                        targetField = sf;
                        targetFieldId = fid;
                        break;
                    }
                }

                if (targetField == null)
                    return CommandResult.Fail("Field with name '" + fieldNameRef + "' not found in schedule.");
            }
            else
            {
                return CommandResult.Fail("fieldRef requires either fieldId or fieldName.");
            }

            // Validate changes
            var changes = request["changes"] as JObject;
            if (changes == null)
                return CommandResult.Fail("changes must be a JSON object.");

            if (changes["parameterName"] != null || changes["parameterId"] != null)
                return CommandResult.Fail(
                    "Cannot change a field's underlying parameter. Remove the field and add a new one with the desired parameter.");

            var applied = new List<string>();
            var skipped = new List<object>();

            using (var tx = new Transaction(doc, "MCP: Update Schedule Field"))
            {
                tx.Start();
                try
                {
                    foreach (var prop in changes.Properties())
                    {
                        var key = prop.Name;
                        var val = prop.Value;

                        try
                        {
                            switch (key)
                            {
                                case "columnHeading":
                                {
                                    var s = val.Type == JTokenType.Null ? null : val.Value<string>();
                                    targetField.ColumnHeading = s ?? string.Empty;
                                    applied.Add(key);
                                    break;
                                }
                                case "hidden":
                                {
                                    if (val.Type != JTokenType.Boolean)
                                    {
                                        skipped.Add(new { key, reason = "Expected boolean." });
                                        break;
                                    }
                                    targetField.IsHidden = val.Value<bool>();
                                    applied.Add(key);
                                    break;
                                }
                                case "columnWidth":
                                {
                                    if (val.Type != JTokenType.Float && val.Type != JTokenType.Integer)
                                    {
                                        skipped.Add(new { key, reason = "Expected number (mm)." });
                                        break;
                                    }
                                    var mm = val.Value<double>();
                                    if (mm <= 0)
                                    {
                                        skipped.Add(new { key, reason = "columnWidth must be > 0." });
                                        break;
                                    }
                                    TrySetDoubleMember(targetField, "ColumnWidth", mm / 304.8);
                                    applied.Add(key);
                                    break;
                                }
                                case "horizontalAlignment":
                                {
                                    var s = val.Value<string>();
                                    if (string.IsNullOrWhiteSpace(s))
                                    {
                                        skipped.Add(new { key, reason = "Expected non-empty string." });
                                        break;
                                    }
                                    ScheduleHorizontalAlignment ha;
                                    if (!Enum.TryParse<ScheduleHorizontalAlignment>(s, true, out ha))
                                    {
                                        skipped.Add(new { key, reason = "Invalid value '" + s + "'. Expected Left|Center|Right." });
                                        break;
                                    }
                                    targetField.HorizontalAlignment = ha;
                                    applied.Add(key);
                                    break;
                                }
                                case "headingOrientation":
                                {
                                    var s = val.Value<string>();
                                    if (string.IsNullOrWhiteSpace(s))
                                    {
                                        skipped.Add(new { key, reason = "Expected non-empty string." });
                                        break;
                                    }
                                    ScheduleHeadingOrientation ho;
                                    if (!Enum.TryParse<ScheduleHeadingOrientation>(s, true, out ho))
                                    {
                                        skipped.Add(new { key, reason = "Invalid value '" + s + "'. Expected Horizontal|Vertical." });
                                        break;
                                    }
                                    targetField.HeadingOrientation = ho;
                                    applied.Add(key);
                                    break;
                                }
                                case "formula":
                                {
                                    bool hasFormula = false;
                                    try { hasFormula = GetBoolMember(targetField, "HasFormula"); } catch { hasFormula = false; }
                                    if (!hasFormula)
                                    {
                                        skipped.Add(new { key, reason = "Field is not a calculated/formula field." });
                                        break;
                                    }

                                    var s = val.Type == JTokenType.Null ? null : val.Value<string>();
                                    if (!TrySetFormula(targetField, s, out var setFormulaErr))
                                    {
                                        skipped.Add(new { key, reason = setFormulaErr });
                                        break;
                                    }
                                    applied.Add(key);
                                    break;
                                }
                                case "combinedParameters":
                                {
                                    bool isCombined = false;
                                    try { isCombined = targetField.IsCombinedParameterField; } catch { isCombined = false; }
                                    if (!isCombined)
                                    {
                                        skipped.Add(new { key, reason = "Field is not a combined-parameter field." });
                                        break;
                                    }
                                    if (val.Type != JTokenType.Array)
                                    {
                                        skipped.Add(new { key, reason = "combinedParameters must be an array." });
                                        break;
                                    }

                                    if (!TryBuildCombinedParameters(doc, definition, (JArray)val, out var combinedList, out var buildErr))
                                    {
                                        skipped.Add(new { key, reason = buildErr });
                                        break;
                                    }

                                    try
                                    {
                                        SetCombinedParameters(targetField, combinedList);
                                        applied.Add(key);
                                    }
                                    catch (Exception ex)
                                    {
                                        skipped.Add(new { key, reason = "SetCombinedParameters failed: " + ex.Message });
                                    }
                                    break;
                                }
                                case "isTotal":
                                {
                                    if (val.Type != JTokenType.Boolean)
                                    {
                                        skipped.Add(new { key, reason = "Expected boolean." });
                                        break;
                                    }
                                    if (!IsNumericSpec(targetField))
                                    {
                                        skipped.Add(new { key, reason = "isTotal only applies to numeric fields (Length/Area/Volume/Number/Integer/Currency)." });
                                        break;
                                    }
                                    if (!TrySetBoolMember(targetField, "IsTotal", val.Value<bool>(), out var totalErr))
                                    {
                                        skipped.Add(new { key, reason = totalErr });
                                        break;
                                    }
                                    applied.Add(key);
                                    break;
                                }
                                case "isPercentage":
                                {
                                    if (val.Type != JTokenType.Boolean)
                                    {
                                        skipped.Add(new { key, reason = "Expected boolean." });
                                        break;
                                    }
                                    if (!TrySetBoolMember(targetField, "IsPercentage", val.Value<bool>(), out var pctErr))
                                    {
                                        skipped.Add(new { key, reason = pctErr });
                                        break;
                                    }
                                    applied.Add(key);
                                    break;
                                }
                                case "displayType":
                                {
                                    var s = val.Value<string>();
                                    if (string.IsNullOrWhiteSpace(s))
                                    {
                                        skipped.Add(new { key, reason = "Expected non-empty string." });
                                        break;
                                    }
                                    ScheduleFieldDisplayType dt;
                                    if (!Enum.TryParse<ScheduleFieldDisplayType>(s, true, out dt))
                                    {
                                        skipped.Add(new { key, reason = "Invalid value '" + s + "'." });
                                        break;
                                    }
                                    try
                                    {
                                        targetField.DisplayType = dt;
                                        applied.Add(key);
                                    }
                                    catch (Exception ex)
                                    {
                                        skipped.Add(new { key, reason = "Set DisplayType failed: " + ex.Message });
                                    }
                                    break;
                                }
                                default:
                                    skipped.Add(new { key, reason = "Unknown change key." });
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            if (tx.HasStarted()) tx.RollBack();
                            return CommandResult.Fail("Failed to apply change '" + key + "': " + ex.Message);
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to update schedule field: " + ex.Message);
                }
            }

            string finalFieldName = null;
            try { finalFieldName = targetField.GetName(); } catch { finalFieldName = null; }
            var finalFieldIndex = FindFieldIndex(fieldOrder, targetFieldId);

            return CommandResult.Ok(new
            {
                scheduleId = RevitCompat.GetId(schedule.Id),
                fieldId = finalFieldIndex,
                fieldIndex = finalFieldIndex,
                fieldName = finalFieldName,
                applied = applied.ToArray(),
                skipped = skipped.ToArray()
            });
        }

        private static int FindFieldIndex(IList<ScheduleFieldId> fieldOrder, ScheduleFieldId fieldId)
        {
            if (fieldOrder == null || fieldId == null) return -1;
            for (var i = 0; i < fieldOrder.Count; i++)
            {
                if (object.Equals(fieldOrder[i], fieldId)) return i;
            }
            return -1;
        }

        private static bool GetBoolMember(object target, string memberName)
        {
            try
            {
                var prop = target.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                var value = prop?.GetValue(target);
                return value is bool b && b;
            }
            catch { return false; }
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

        /// <summary>
        /// Detect whether the field's spec type is "numeric" enough to legally support IsTotal.
        /// </summary>
        private static bool IsNumericSpec(ScheduleField field)
        {
            try
            {
                var spec = field.GetSpecTypeId();
                if (spec == null) return false;
                if (spec == SpecTypeId.Length) return true;
                if (spec == SpecTypeId.Area) return true;
                if (spec == SpecTypeId.Volume) return true;
                if (spec == SpecTypeId.Number) return true;
                if (spec == SpecTypeId.Currency) return true;

                // SpecTypeId.Int.Integer — reflection-tolerant lookup; the nested "Int" class
                // exists in newer Revit API surfaces and we want to remain version-safe.
                try
                {
                    var intNested = typeof(SpecTypeId).GetNestedType("Int", BindingFlags.Public | BindingFlags.Static);
                    if (intNested != null)
                    {
                        var intProp = intNested.GetProperty("Integer", BindingFlags.Public | BindingFlags.Static);
                        if (intProp != null)
                        {
                            var v = intProp.GetValue(null) as ForgeTypeId;
                            if (v != null && spec == v) return true;
                        }
                    }
                }
                catch { }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets a ScheduleField.Formula via the property if writable, otherwise via SetFormula().
        /// Returns false with a reason if neither route is available.
        /// </summary>
        private static bool TrySetFormula(ScheduleField field, string formula, out string error)
        {
            error = null;

            // Property route
            try
            {
                var prop = typeof(ScheduleField).GetProperty("Formula", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(field, formula);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "Set Formula property failed: " + ex.Message;
                return false;
            }

            // Method route
            try
            {
                var method = typeof(ScheduleField).GetMethod(
                    "SetFormula",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null);
                if (method != null)
                {
                    method.Invoke(field, new object[] { formula });
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "SetFormula() failed: " + ex.Message;
                return false;
            }

            error = "Neither ScheduleField.Formula setter nor SetFormula(string) is available in this Revit version.";
            return false;
        }

        /// <summary>
        /// Sets a bool-typed property on ScheduleField via reflection. Returns false with
        /// a reason if the property does not exist / isn't writable / throws.
        /// </summary>
        private static bool TrySetBoolMember(ScheduleField field, string memberName, bool value, out string error)
        {
            error = null;
            try
            {
                var prop = typeof(ScheduleField).GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite)
                {
                    error = memberName + " is not writable in this Revit version.";
                    return false;
                }
                prop.SetValue(field, value);
                return true;
            }
            catch (Exception ex)
            {
                error = "Set " + memberName + " failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Build a typed combined-parameter data list from JSON specs. Resolves parameter names
        /// against the ScheduleDefinition's schedulable fields to obtain ParameterIds.
        /// </summary>
        private static bool TryBuildCombinedParameters(
            Document doc,
            ScheduleDefinition definition,
            JArray segments,
            out System.Collections.IList result,
            out string error)
        {
            result = null;
            error = null;

            if (segments.Count == 0)
            {
                error = "combinedParameters array is empty.";
                return false;
            }

            // Build paramName → ParameterId map from schedulable fields.
            IList<SchedulableField> schedulable = null;
            try { schedulable = definition.GetSchedulableFields(); }
            catch (Exception ex) { error = "Failed to read schedulable fields: " + ex.Message; return false; }
            if (schedulable == null) schedulable = new List<SchedulableField>();

            var nameToParamId = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            foreach (var sf in schedulable)
            {
                try
                {
                    var nm = sf.GetName(doc);
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (!nameToParamId.ContainsKey(nm))
                        nameToParamId[nm] = sf.ParameterId;
                }
                catch { }
            }

            var list = CreateCombinedParameterList();

            for (int i = 0; i < segments.Count; i++)
            {
                var segTok = segments[i] as JObject;
                if (segTok == null)
                {
                    error = "combinedParameters[" + i + "] must be an object.";
                    return false;
                }

                ElementId paramId = ElementId.InvalidElementId;

                var paramName = segTok.Value<string>("parameterName");
                var paramIdToken = segTok["parameterId"];

                if (paramIdToken != null && paramIdToken.Type != JTokenType.Null)
                {
                    long pidVal;
                    try { pidVal = paramIdToken.Value<long>(); }
                    catch { error = "combinedParameters[" + i + "].parameterId must be an integer."; return false; }
                    if (!RevitCompat.CanRepresentElementId(pidVal))
                    {
                        error = "combinedParameters[" + i + "]: " + RevitCompat.ElementIdRangeError(pidVal);
                        return false;
                    }
                    paramId = RevitCompat.ToElementId(pidVal);
                }
                else if (!string.IsNullOrEmpty(paramName))
                {
                    if (!nameToParamId.TryGetValue(paramName, out paramId))
                    {
                        error = "combinedParameters[" + i + "]: parameter '" + paramName + "' is not a schedulable field of this schedule.";
                        return false;
                    }
                }
                else
                {
                    error = "combinedParameters[" + i + "] requires parameterName or parameterId.";
                    return false;
                }

                object data;
                try
                {
                    data = CreateCombinedParameterData(paramId);
                }
                catch (Exception ex)
                {
                    error = "combinedParameters[" + i + "]: CombinedParameterData.Create failed: " + ex.Message;
                    return false;
                }

                try
                {
                    var prefix = segTok.Value<string>("prefix");
                    if (prefix != null) TrySetObjectProperty(data, "Prefix", prefix);
                }
                catch { }

                try
                {
                    var separator = segTok.Value<string>("separator");
                    if (separator != null) TrySetObjectProperty(data, "Separator", separator);
                }
                catch { }

                try
                {
                    var suffix = segTok.Value<string>("suffix");
                    if (suffix != null) TrySetObjectProperty(data, "Suffix", suffix);
                }
                catch { }

                try
                {
                    var truncToken = segTok["truncated"] ?? segTok["truncate"];
                    if (truncToken != null && truncToken.Type == JTokenType.Boolean)
                        TrySetObjectProperty(data, "Truncated", truncToken.Value<bool>());
                }
                catch { }

                try
                {
                    var nocToken = segTok["numberOfCharacters"];
                    if (nocToken != null && nocToken.Type == JTokenType.Integer)
                        TrySetObjectProperty(data, "NumberOfCharacters", nocToken.Value<int>());
                }
                catch { }

                list.Add(data);
            }

            result = list;
            return true;
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
            var type = GetCombinedParameterDataType();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Create") continue;
                var ps = method.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(ElementId)) continue;

                var args = new object[ps.Length];
                args[0] = paramId;
                for (var i = 1; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType == typeof(ElementId)) args[i] = ElementId.InvalidElementId;
                    else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                }

                try { return method.Invoke(null, args); }
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

        private static void TrySetObjectProperty(object target, string propertyName, object value)
        {
            if (target == null) return;
            try
            {
                var prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) prop.SetValue(target, value);
            }
            catch { }
        }
    }
}
