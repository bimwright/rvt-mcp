using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetScheduleFormulasHandler : IRevitCommand
    {
        public string Name => "get_schedule_formulas";
        public string Description => "Extract all calculated (formula) and combined-parameter fields from a schedule, with parsed formula dependencies. Useful for auditing, debugging, or copying formulas between schedules.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""}}}";

        // Operator tokens used to split a formula text into identifier candidates.
        private static readonly string[] FormulaSplitTokens =
        {
            "+", "-", "*", "/", "(", ")", ",", "<=", ">=", "!=", "<", ">", "=", "&&", "||", "!", "%"
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var scheduleIdToken = request["scheduleId"];
            var scheduleName = request.Value<string>("scheduleName");

            // Resolve ViewSchedule by id or name
            ViewSchedule schedule = null;
            if (scheduleIdToken != null && scheduleIdToken.Type != JTokenType.Null)
            {
                long idValue;
                try { idValue = scheduleIdToken.Value<long>(); }
                catch { return CommandResult.Fail("scheduleId must be an integer."); }

                if (!RevitCompat.CanRepresentElementId(idValue))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(idValue));

                var el = doc.GetElement(RevitCompat.ToElementId(idValue));
                schedule = el as ViewSchedule;
                if (schedule == null)
                    return CommandResult.Fail($"Element {idValue} is not a ViewSchedule or not found.");
            }
            else if (!string.IsNullOrEmpty(scheduleName))
            {
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => s.Name != null &&
                                s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail($"Schedule '{scheduleName}' not found.");
                if (matches.Count > 1)
                    return CommandResult.Fail($"Ambiguous schedule name '{scheduleName}': {matches.Count} matches found. Use scheduleId.");
                schedule = matches[0];
            }
            else
            {
                return CommandResult.Fail("Either scheduleId or scheduleName is required.");
            }

            var definition = schedule.Definition;
            if (definition == null)
                return CommandResult.Fail("Schedule has no definition.");

            // Build name → field index map (stable inside this schedule definition).
            var nameToFieldId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            IList<ScheduleFieldId> fieldOrder = null;
            try { fieldOrder = definition.GetFieldOrder(); }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to read field order: {ex.Message}");
            }
            if (fieldOrder == null)
                fieldOrder = new List<ScheduleFieldId>();

            // First pass: collect all field names so dependency parsing can resolve them.
            var collectedFields = new List<Tuple<ScheduleField, int>>(fieldOrder.Count);
            for (var i = 0; i < fieldOrder.Count; i++)
            {
                var fid = fieldOrder[i];
                ScheduleField sf = null;
                try { sf = definition.GetField(fid); }
                catch { sf = null; }
                if (sf == null) continue;

                collectedFields.Add(Tuple.Create(sf, i));

                string fname = null;
                try { fname = sf.GetName(); } catch { fname = null; }
                if (string.IsNullOrEmpty(fname)) continue;

                if (!nameToFieldId.ContainsKey(fname))
                    nameToFieldId[fname] = i;
            }

            // Second pass: extract formula + combined-parameter fields.
            var formulaItems = new List<object>();
            int totalFormulaFields = 0;
            int totalCombinedFields = 0;

            foreach (var item in collectedFields)
            {
                var field = item.Item1;
                var fieldIndex = item.Item2;
                string fieldName = null;
                try { fieldName = field.GetName(); } catch { fieldName = null; }

                bool hasFormula = false;
                try { hasFormula = GetBoolProperty(field, "HasFormula"); } catch { hasFormula = false; }

                bool isCombined = false;
                try { isCombined = field.IsCombinedParameterField; } catch { isCombined = false; }

                string specTypeId = null;
                try { specTypeId = field.GetSpecTypeId()?.TypeId; } catch { specTypeId = null; }

                if (hasFormula)
                {
                    totalFormulaFields++;

                    string formulaText = ReadFormulaText(field);
                    bool isPercentage = false;
                    try { isPercentage = GetBoolProperty(field, "IsPercentage"); } catch { isPercentage = false; }

                    string percentageOfFieldName = ResolvePercentageOfFieldName(field, definition);

                    var dependsOn = ParseFormulaDependencies(formulaText, nameToFieldId);

                    formulaItems.Add(new
                    {
                        fieldId = fieldIndex,
                        fieldIndex = fieldIndex,
                        name = fieldName,
                        kind = "formula",
                        formula = formulaText,
                        specTypeId = specTypeId,
                        isPercentage = isPercentage,
                        percentageOfFieldName = percentageOfFieldName,
                        dependsOn = dependsOn
                    });
                }
                else if (isCombined)
                {
                    totalCombinedFields++;

                    var combinedSegments = new List<object>();
                    System.Collections.IEnumerable combined = null;
                    try
                    {
                        var method = typeof(ScheduleField).GetMethod("GetCombinedParameters", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        combined = method?.Invoke(field, null) as System.Collections.IEnumerable;
                    }
                    catch { combined = null; }

                    if (combined != null)
                    {
                        foreach (var c in combined)
                        {
                            if (c == null) continue;

                            string segParamName = null;
                            long? segParamId = null;
                            try
                            {
                                var paramId = GetElementIdProperty(c, "ParamId") ?? GetElementIdProperty(c, "ParameterId");
                                segParamName = ResolveCombinedParamName(doc, paramId);
                                segParamId = RevitCompat.GetIdOrNull(paramId);
                            }
                            catch
                            {
                                segParamName = null;
                                segParamId = null;
                            }

                            string prefix = null;
                            string separator = null;
                            string suffix = null;
                            bool truncate = false;
                            int numberOfCharacters = 0;

                            try { prefix = GetStringProperty(c, "Prefix"); } catch { prefix = null; }
                            try { separator = GetStringProperty(c, "Separator"); } catch { separator = null; }
                            try { suffix = GetStringProperty(c, "Suffix"); } catch { suffix = null; }
                            try { truncate = GetBoolProperty(c, "Truncated"); } catch { truncate = false; }
                            try { numberOfCharacters = GetIntProperty(c, "NumberOfCharacters"); } catch { numberOfCharacters = 0; }

                            combinedSegments.Add(new
                            {
                                parameterName = segParamName,
                                parameterId = segParamId,
                                prefix = prefix,
                                separator = separator,
                                suffix = suffix,
                                truncated = truncate,
                                numberOfCharacters = numberOfCharacters
                            });
                        }
                    }

                    formulaItems.Add(new
                    {
                        fieldId = fieldIndex,
                        fieldIndex = fieldIndex,
                        name = fieldName,
                        kind = "combined",
                        specTypeId = specTypeId,
                        combinedSegments = combinedSegments.ToArray()
                    });
                }
            }

            return CommandResult.Ok(new
            {
                scheduleId = RevitCompat.GetId(schedule.Id),
                scheduleName = schedule.Name,
                totalFormulaFields = totalFormulaFields,
                totalCombinedFields = totalCombinedFields,
                formulas = formulaItems.ToArray()
            });
        }

        /// <summary>
        /// Tries to read the formula string from a ScheduleField.
        /// Prefers a direct "Formula" property; falls back to a "GetFormula()" method.
        /// </summary>
        private static string ReadFormulaText(ScheduleField field)
        {
            if (field == null) return null;

            try
            {
                var t = field.GetType();
                var prop = t.GetProperty("Formula", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanRead)
                {
                    var v = prop.GetValue(field, null);
                    if (v != null) return v.ToString();
                }
            }
            catch { }

            try
            {
                var t = field.GetType();
                var m = t.GetMethod("GetFormula", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null)
                {
                    var v = m.Invoke(field, null);
                    if (v != null) return v.ToString();
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Attempt to resolve the field-name referenced by a percentage formula's "PercentageOf"
        /// (or "PercentageOfFieldId") member. Returns null on miss.
        /// TODO: API surface may vary across Revit versions; reflection keeps us version-tolerant.
        /// </summary>
        private static string ResolvePercentageOfFieldName(ScheduleField field, ScheduleDefinition definition)
        {
            if (field == null || definition == null) return null;

            try
            {
                var t = field.GetType();

                // Try a direct "PercentageOf" property first.
                var propPctOf = t.GetProperty("PercentageOf", BindingFlags.Public | BindingFlags.Instance);
                if (propPctOf != null && propPctOf.CanRead)
                {
                    var v = propPctOf.GetValue(field, null);
                    var resolved = TryResolveScheduleFieldName(v, definition);
                    if (resolved != null) return resolved;
                }

                // Then "PercentageOfFieldId".
                var propPctOfId = t.GetProperty("PercentageOfFieldId", BindingFlags.Public | BindingFlags.Instance);
                if (propPctOfId != null && propPctOfId.CanRead)
                {
                    var v = propPctOfId.GetValue(field, null);
                    var resolved = TryResolveScheduleFieldName(v, definition);
                    if (resolved != null) return resolved;
                }
            }
            catch { }

            return null;
        }

        private static string TryResolveScheduleFieldName(object idObj, ScheduleDefinition definition)
        {
            if (idObj == null || definition == null) return null;

            try
            {
                var sfid = idObj as ScheduleFieldId;
                if (sfid != null)
                {
                    var sf = definition.GetField(sfid);
                    if (sf != null) return sf.GetName();
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Tokenizes a formula string on common operators/punctuation, then matches each
        /// token (case-insensitive, brackets/whitespace stripped) against the known field
        /// names. Returns the original field-name casing, de-duplicated.
        /// </summary>
        private static string[] ParseFormulaDependencies(string formula, Dictionary<string, int> nameToFieldId)
        {
            if (string.IsNullOrEmpty(formula) || nameToFieldId == null || nameToFieldId.Count == 0)
                return Array.Empty<string>();

            var working = StripBrackets(formula);
            var canonicalNames = nameToFieldId.Keys.ToArray();

            var matched = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var canon in canonicalNames)
            {
                if (string.IsNullOrWhiteSpace(canon)) continue;
                if (working.IndexOf(canon, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (seen.Add(canon)) matched.Add(canon);
                }
            }

            return matched.ToArray();
        }

        private static string StripBrackets(string token)
        {
            if (string.IsNullOrEmpty(token)) return token;
            // Remove all '[' and ']' characters anywhere in the token.
            var sb = new System.Text.StringBuilder(token.Length);
            foreach (var ch in token)
            {
                if (ch == '[' || ch == ']') continue;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static ElementId GetElementIdProperty(object source, string propertyName)
        {
            if (source == null) return null;
            try
            {
                var prop = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(source) as ElementId;
            }
            catch { return null; }
        }

        private static string GetStringProperty(object source, string propertyName)
        {
            if (source == null) return null;
            try
            {
                var prop = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(source) as string;
            }
            catch { return null; }
        }

        private static bool GetBoolProperty(object source, string propertyName)
        {
            if (source == null) return false;
            try
            {
                var prop = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                var value = prop?.GetValue(source);
                return value is bool b && b;
            }
            catch { return false; }
        }

        private static int GetIntProperty(object source, string propertyName)
        {
            if (source == null) return 0;
            try
            {
                var prop = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                var value = prop?.GetValue(source);
                return value is int i ? i : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Resolve a combined-parameter segment's parameter name:
        ///   InvalidElementId → null
        ///   Project/shared parameter element → doc.GetElement(...).Name
        ///   Built-in parameter id (negative) → LabelUtils.GetLabelForBuiltInParameter(...)
        ///   Fallback → "param:{id}"
        /// </summary>
        private static string ResolveCombinedParamName(Document doc, ElementId paramId)
        {
            try
            {
                if (paramId == null || paramId == ElementId.InvalidElementId)
                    return null;

                // Try resolving as a real element first (project/shared parameter element).
                try
                {
                    var pEl = doc.GetElement(paramId);
                    if (pEl != null && !string.IsNullOrEmpty(pEl.Name))
                        return pEl.Name;
                }
                catch { }

                // Try BuiltInParameter (negative ids) without using version-specific ElementId accessors.
                try
                {
                    long raw = RevitCompat.GetId(paramId);
                    if (raw < 0 && raw >= int.MinValue)
                    {
                        var bip = (BuiltInParameter)(int)raw;
                        var label = GetBuiltInParameterLabel(bip);
                        if (!string.IsNullOrEmpty(label))
                            return label;
                    }
                }
                catch { }

                return "param:" + RevitCompat.GetId(paramId);
            }
            catch
            {
                return null;
            }
        }

        private static string GetBuiltInParameterLabel(BuiltInParameter bip)
        {
            try
            {
                foreach (var method in typeof(LabelUtils).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "GetLabelForBuiltInParameter") continue;
                    var ps = method.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(BuiltInParameter))
                    {
                        var label = method.Invoke(null, new object[] { bip }) as string;
                        if (!string.IsNullOrEmpty(label)) return label;
                    }
                }
            }
            catch { }
            return bip.ToString();
        }
    }
}
