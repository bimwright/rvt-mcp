using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetScheduleDefinitionHandler : IRevitCommand
    {
        public string Name => "get_schedule_definition";
        public string Description => "Get the full structural definition of a schedule: fields (parameter/formula/combined), filters, sort/group, and settings. Identify schedule by `scheduleId` (long) or `scheduleName`.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string"",""properties"":{}}}}".Replace(@""",""properties"":{}", string.Empty);

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

            string categoryName = null;
            try
            {
                if (definition.CategoryId == ElementId.InvalidElementId)
                    categoryName = "<multi-category>";
                else
                    categoryName = Category.GetCategory(doc, definition.CategoryId)?.Name;
            }
            catch
            {
                categoryName = null;
            }

            // Fields
            var fields = new List<object>();
            IList<ScheduleFieldId> fieldOrder = null;
            try { fieldOrder = definition.GetFieldOrder(); } catch { }

            if (fieldOrder != null)
            {
                for (var i = 0; i < fieldOrder.Count; i++)
                {
                    fields.Add(BuildFieldDto(doc, definition, fieldOrder[i], i));
                }
            }

            // Filters
            var filters = new List<object>();
            int filterCount = 0;
            try { filterCount = definition.GetFilterCount(); } catch { }
            for (var i = 0; i < filterCount; i++)
            {
                filters.Add(BuildFilterDto(doc, definition, i, fieldOrder));
            }

            // Sort/Group
            var sortGroup = new List<object>();
            int sortCount = 0;
            try { sortCount = definition.GetSortGroupFieldCount(); } catch { }
            for (var i = 0; i < sortCount; i++)
            {
                sortGroup.Add(BuildSortGroupDto(definition, i, fieldOrder));
            }

            // Settings
            bool isItemized = false;
            bool showGrandTotal = false;
            bool showHeaders = false;
            bool showTitle = false;
            bool isKey = false;

            try { isItemized = definition.IsItemized; } catch { }
            try { showGrandTotal = definition.ShowGrandTotal; } catch { }
            try { showHeaders = definition.ShowHeaders; } catch { }
            try { showTitle = definition.ShowTitle; } catch { }
            try { isKey = definition.IsKeySchedule; } catch { }

            return CommandResult.Ok(new
            {
                id = RevitCompat.GetId(schedule.Id),
                name = schedule.Name,
                categoryName = categoryName,
                isKey = isKey,
                isItemized = isItemized,
                showGrandTotal = showGrandTotal,
                showHeaders = showHeaders,
                showTitle = showTitle,
                fields = fields.ToArray(),
                filters = filters.ToArray(),
                sortGroup = sortGroup.ToArray()
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

        private static object BuildFieldDto(Document doc, ScheduleDefinition definition, ScheduleFieldId fieldId, int fieldIndex)
        {
            try
            {
                var field = definition.GetField(fieldId);
                if (field == null)
                    return new { _error = "Field is null." };

                string fieldType;
                bool hasFormula = false;
                bool isCombined = false;
                try { hasFormula = GetBoolMember(field, "HasFormula"); } catch { }
                try { isCombined = field.IsCombinedParameterField; } catch { }

                if (hasFormula) fieldType = "formula";
                else if (isCombined) fieldType = "combined";
                else fieldType = "parameter";

                string fieldName = null;
                try { fieldName = field.GetName(); } catch { }

                string columnHeading = null;
                try { columnHeading = field.ColumnHeading; } catch { }

                string parameterName = null;
                long? parameterId = null;
                try
                {
                    var pId = field.ParameterId;
                    if (pId != null && pId != ElementId.InvalidElementId)
                    {
                        parameterId = RevitCompat.GetIdOrNull(pId);

                        // Try element lookup (shared/project parameter)
                        try
                        {
                            var pElem = doc.GetElement(pId);
                            if (pElem != null)
                                parameterName = pElem.Name;
                        }
                        catch { }

                        // Built-in parameter: fall back to LabelUtils
                        if (string.IsNullOrEmpty(parameterName))
                        {
                            try
                            {
                                var rawId = RevitCompat.GetId(pId);
                                if (rawId < int.MinValue || rawId > int.MaxValue) throw new InvalidOperationException();
                                var bip = (BuiltInParameter)(int)rawId;
                                if (bip != BuiltInParameter.INVALID)
                                    parameterName = GetBuiltInParameterLabel(bip);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                string formula = TryGetFormula(field);

                var combinedSegments = new List<object>();
                if (isCombined)
                {
                    try
                    {
                        var combined = field.GetCombinedParameters();
                        if (combined != null)
                        {
                            foreach (var seg in combined)
                            {
                                combinedSegments.Add(BuildCombinedSegmentDto(doc, seg));
                            }
                        }
                    }
                    catch { }
                }

                string specTypeId = null;
                try { specTypeId = field.GetSpecTypeId()?.TypeId; } catch { }

                bool hidden = false;
                try { hidden = field.IsHidden; } catch { }

                double columnWidthMm = 0.0;
                try { columnWidthMm = GetDoubleMember(field, "ColumnWidth") * 304.8; } catch { }

                bool isTotal = false;
                try { isTotal = GetBoolMember(field, "IsTotal"); } catch { }

                // Field.IsPercentage may not exist on all versions; reflect-safe
                bool isPercentage = false;
                try
                {
                    var prop = typeof(ScheduleField).GetProperty("IsPercentage", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var v = prop.GetValue(field);
                        if (v is bool b) isPercentage = b;
                    }
                }
                catch { }

                string displayType = null;
                try { displayType = field.DisplayType.ToString(); } catch { }

                string horizontalAlignment = null;
                try { horizontalAlignment = field.HorizontalAlignment.ToString(); } catch { }

                string headingOrientation = null;
                try { headingOrientation = field.HeadingOrientation.ToString(); } catch { }

                return new
                {
                    fieldId = fieldIndex,
                    fieldIndex = fieldIndex,
                    name = fieldName,
                    columnHeading = columnHeading,
                    type = fieldType,
                    parameterName = parameterName,
                    parameterId = parameterId,
                    formula = formula,
                    combinedSegments = combinedSegments.ToArray(),
                    specTypeId = specTypeId,
                    hidden = hidden,
                    columnWidth = columnWidthMm,
                    isTotal = isTotal,
                    isPercentage = isPercentage,
                    displayType = displayType,
                    horizontalAlignment = horizontalAlignment,
                    headingOrientation = headingOrientation
                };
            }
            catch (Exception ex)
            {
                return new { _error = ex.Message };
            }
        }

        private static string TryGetFormula(ScheduleField field)
        {
            // Direct property
            try
            {
                var prop = typeof(ScheduleField).GetProperty("Formula", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(field);
                    if (v is string s && !string.IsNullOrEmpty(s))
                        return s;
                }
            }
            catch { }

            // Reflection fallback: GetFormula()
            try
            {
                var method = typeof(ScheduleField).GetMethod("GetFormula", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    var v = method.Invoke(field, null);
                    if (v is string s && !string.IsNullOrEmpty(s))
                        return s;
                }
            }
            catch { }

            return null;
        }

        private static object BuildCombinedSegmentDto(Document doc, object segment)
        {
            try
            {
                if (segment == null) return new { _error = "Segment is null." };

                var type = segment.GetType();

                string segParamName = null;
                try
                {
                    var paramIdProp = type.GetProperty("ParamId", BindingFlags.Public | BindingFlags.Instance)
                                      ?? type.GetProperty("ParameterId", BindingFlags.Public | BindingFlags.Instance);
                    if (paramIdProp != null)
                    {
                        var paramId = paramIdProp.GetValue(segment) as ElementId;
                        if (paramId != null && paramId != ElementId.InvalidElementId)
                        {
                            try
                            {
                                var pElem = doc.GetElement(paramId);
                                if (pElem != null)
                                    segParamName = pElem.Name;
                            }
                            catch { }

                            if (string.IsNullOrEmpty(segParamName))
                            {
                                try
                                {
                                    var rawId = RevitCompat.GetId(paramId);
                                    if (rawId < int.MinValue || rawId > int.MaxValue) throw new InvalidOperationException();
                                    var bip = (BuiltInParameter)(int)rawId;
                                    if (bip != BuiltInParameter.INVALID)
                                        segParamName = GetBuiltInParameterLabel(bip);
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }

                var prefix = SafeGetStringProperty(segment, "Prefix");
                var separator = SafeGetStringProperty(segment, "Separator");
                var suffix = SafeGetStringProperty(segment, "Suffix");
                var sampleValue = SafeGetStringProperty(segment, "SampleValue");

                bool truncate = false;
                int numberOfCharacters = 0;
                try
                {
                    var truncProp = type.GetProperty("Truncated", BindingFlags.Public | BindingFlags.Instance);
                    if (truncProp != null)
                    {
                        var v = truncProp.GetValue(segment);
                        if (v is bool b) truncate = b;
                    }
                }
                catch { }
                try
                {
                    var nocProp = type.GetProperty("NumberOfCharacters", BindingFlags.Public | BindingFlags.Instance);
                    if (nocProp != null)
                    {
                        var v = nocProp.GetValue(segment);
                        if (v is int n) numberOfCharacters = n;
                    }
                }
                catch { }

                return new
                {
                    parameterName = segParamName,
                    prefix = prefix,
                    separator = separator,
                    suffix = suffix,
                    sampleValue = sampleValue,
                    truncated = truncate,
                    numberOfCharacters = numberOfCharacters
                };
            }
            catch (Exception ex)
            {
                return new { _error = ex.Message };
            }
        }

        private static string SafeGetStringProperty(object source, string name)
        {
            try
            {
                var prop = source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return null;
                var v = prop.GetValue(source);
                return v as string;
            }
            catch
            {
                return null;
            }
        }

        private static object BuildFilterDto(Document doc, ScheduleDefinition definition, int index, IList<ScheduleFieldId> fieldOrder)
        {
            try
            {
                var f = definition.GetFilter(index);

                var dto = new JObject();

                int fieldIndex = -1;
                string fieldName = null;
                ForgeTypeId fieldSpec = null;
                try
                {
                    var fId = f.FieldId;
                    fieldIndex = FindFieldIndex(fieldOrder, fId);
                    try
                    {
                        var fld = definition.GetField(fId);
                        if (fld != null)
                        {
                            fieldName = fld.GetName();
                            try { fieldSpec = fld.GetSpecTypeId(); } catch { }
                        }
                    }
                    catch { }
                }
                catch { }

                dto["fieldId"] = fieldIndex;
                dto["fieldIndex"] = fieldIndex;
                dto["fieldName"] = fieldName;

                try { dto["operator"] = f.FilterType.ToString(); } catch { dto["operator"] = null; }

                JToken value = JValue.CreateNull();

                try { var s = f.GetStringValue(); if (s != null) value = new JValue(s); } catch { }
                if (value.Type == JTokenType.Null)
                {
                    try { value = new JValue(ConvertFromInternalUnits(f.GetDoubleValue(), fieldSpec)); } catch { }
                }
                if (value.Type == JTokenType.Null)
                {
                    try { value = new JValue(f.GetIntegerValue()); } catch { }
                }
                if (value.Type == JTokenType.Null)
                {
                    try
                    {
                        var elemId = f.GetElementIdValue();
                        if (elemId != null && elemId != ElementId.InvalidElementId)
                            value = new JValue(RevitCompat.GetId(elemId));
                    }
                    catch { }
                }

                dto["value"] = value;
                dto["valueUnits"] = UnitLabel(fieldSpec);
                return dto;
            }
            catch (Exception ex)
            {
                return new { _error = ex.Message };
            }
        }

        private static object BuildSortGroupDto(ScheduleDefinition definition, int index, IList<ScheduleFieldId> fieldOrder)
        {
            try
            {
                var s = definition.GetSortGroupField(index);

                int fieldIndex = -1;
                string fieldName = null;
                try
                {
                    var fId = s.FieldId;
                    fieldIndex = FindFieldIndex(fieldOrder, fId);
                    try
                    {
                        var fld = definition.GetField(fId);
                        if (fld != null) fieldName = fld.GetName();
                    }
                    catch { }
                }
                catch { }

                string sortOrder = null;
                try { sortOrder = s.SortOrder.ToString(); } catch { }

                bool showHeader = false;
                bool showFooter = false;
                bool showBlankLine = false;
                try { showHeader = s.ShowHeader; } catch { }
                try { showFooter = s.ShowFooter; } catch { }
                try { showBlankLine = s.ShowBlankLine; } catch { }

                return new
                {
                    fieldId = fieldIndex,
                    fieldIndex = fieldIndex,
                    fieldName = fieldName,
                    sortOrder = sortOrder,
                    showHeader = showHeader,
                    showFooter = showFooter,
                    showBlankLine = showBlankLine
                };
            }
            catch (Exception ex)
            {
                return new { _error = ex.Message };
            }
        }

        private static double ConvertFromInternalUnits(double value, ForgeTypeId spec)
        {
            if (spec == null) return value;
            try
            {
                if (spec == SpecTypeId.Length) return value * 304.8;
                if (spec == SpecTypeId.Area) return value * 0.092903;
                if (spec == SpecTypeId.Volume) return value * 0.0283168;
                if (spec == SpecTypeId.Angle) return value * (180.0 / Math.PI);
            }
            catch { }
            return value;
        }

        private static string UnitLabel(ForgeTypeId spec)
        {
            if (spec == null) return null;
            try
            {
                if (spec == SpecTypeId.Length) return "mm";
                if (spec == SpecTypeId.Area) return "m²";
                if (spec == SpecTypeId.Volume) return "m³";
                if (spec == SpecTypeId.Angle) return "degrees";
            }
            catch { }
            return null;
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

        private static double GetDoubleMember(object target, string memberName)
        {
            try
            {
                var prop = target.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                var value = prop?.GetValue(target);
                return value is double d ? d : 0.0;
            }
            catch { return 0.0; }
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
