using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AiElementFilterHandler : IRevitCommand
    {
        public string Name => "ai_element_filter";
        public string Description => "Filter elements by category, parameter name, value, and comparison operator. Numeric values are in millimeters (auto-converted from Revit internal units). An empty parameterValue with operator equals matches elements whose parameter is empty or unset. viewSpecific=true keeps view-owned elements (detail lines), false keeps model elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""category"":{""type"":""string""},""parameterName"":{""type"":""string""},""parameterValue"":{""type"":""string""},""operator"":{""type"":""string"",""enum"":[""equals"",""contains"",""startswith"",""greaterthan"",""lessthan""]},""limit"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":1000},""select"":{""type"":""boolean"",""default"":false},""viewSpecific"":{""type"":""boolean""}},""required"":[""category""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var categoryName = request.Value<string>("category");
            var paramName = request.Value<string>("parameterName");
            var paramValue = request.Value<string>("parameterValue");
            var op = request.Value<string>("operator") ?? "equals";
            var limit = request.Value<int?>("limit") ?? 100;
            if (limit < 1 || limit > 1000)
                return CommandResult.Fail("limit must be between 1 and the hard maximum of 1000.");
            var selectResult = request.Value<bool?>("select") ?? false;
            var viewSpecific = request.Value<bool?>("viewSpecific");

            if (string.IsNullOrEmpty(categoryName))
                return CommandResult.Fail("category is required.");

            var hasParamFilter = !string.IsNullOrEmpty(paramName);
            if (!hasParamFilter && !string.IsNullOrEmpty(paramValue))
                return CommandResult.Fail("parameterName is required when parameterValue is set.");
            // An empty value with a parameter name asks for elements where that parameter is empty or unset (#77).
            var matchEmpty = hasParamFilter && string.IsNullOrEmpty(paramValue);
            if (matchEmpty && !op.Equals("equals", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail($"parameterValue is required for operator '{op}'. To find elements where the parameter is empty or unset, use operator 'equals' with an empty parameterValue.");

            var matchedCategory = CategoryLookup.FindByName(doc, categoryName);
            BuiltInCategory? bic = matchedCategory == null ? (BuiltInCategory?)null : CategoryLookup.ToBuiltIn(matchedCategory);

            if (bic == null)
                return CommandResult.Fail($"Category '{categoryName}' not found.");

            var collector = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType();

            // Without a filter Revit counts the category natively and only `limit` elements are
            // read. With a parameter or viewSpecific filter every element is still tested so
            // totalFound stays exact, but only the first `limit` matches are kept.
            int totalFound;
            List<Element> filtered;
            if (!hasParamFilter && viewSpecific == null)
            {
                totalFound = collector.GetElementCount();
                filtered = new FilteredElementCollector(doc)
                    .OfCategory(bic.Value)
                    .WhereElementIsNotElementType()
                    .Take(limit)
                    .ToList();
            }
            else
            {
                var scanned = 0;
                var parameterSeen = false;
                Func<Element, bool> matches = el =>
                {
                    scanned++;
                    if (!hasParamFilter)
                        return el.ViewSpecific == viewSpecific.Value;

                    var param = el.LookupParameter(paramName);
                    if (param == null) return false;
                    parameterSeen = true;
                    if (viewSpecific != null && el.ViewSpecific != viewSpecific.Value) return false;

                    if (matchEmpty) return IsEmpty(param);

                    // For numeric comparisons, get value in display units (mm for length)
                    if (param.StorageType == StorageType.Double &&
                        (op.Equals("greaterthan", StringComparison.OrdinalIgnoreCase) ||
                         op.Equals("lessthan", StringComparison.OrdinalIgnoreCase) ||
                         op.Equals("equals", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!double.TryParse(paramValue, out var targetValue))
                            return false;

                        var rawValue = param.AsDouble();
                        var displayValue = ConvertToDisplayUnits(doc, param, rawValue);

                        switch (op.ToLower())
                        {
                            case "greaterthan": return displayValue > targetValue;
                            case "lessthan": return displayValue < targetValue;
                            case "equals": return Math.Abs(displayValue - targetValue) < 0.1;
                            default: return false;
                        }
                    }

                    // For string-based comparisons
                    var val = GetParamValueAsString(doc, param);
                    if (val == null) return false;

                    switch (op.ToLower())
                    {
                        case "equals": return val.Equals(paramValue, StringComparison.OrdinalIgnoreCase);
                        case "contains": return val.IndexOf(paramValue, StringComparison.OrdinalIgnoreCase) >= 0;
                        case "startswith": return val.StartsWith(paramValue, StringComparison.OrdinalIgnoreCase);
                        default: return val.Equals(paramValue, StringComparison.OrdinalIgnoreCase);
                    }
                };
                totalFound = 0;
                filtered = new List<Element>();
                foreach (var el in collector)
                {
                    if (!matches(el)) continue;
                    totalFound++;
                    if (filtered.Count < limit) filtered.Add(el);
                }

                // A name no element carries is a typo or a type parameter, not "no matches".
                if (hasParamFilter && scanned > 0 && !parameterSeen)
                    return CommandResult.Fail($"No element in category '{matchedCategory.Name}' has an instance parameter named '{paramName}'. Check the name with revit_get_element_parameters; type parameters are not searched.");
            }

            // Auto-select if requested
            if (selectResult && filtered.Count > 0)
            {
                var uidoc = app.ActiveUIDocument;
                uidoc?.Selection.SetElementIds(filtered.Select(e => e.Id).ToList());
            }

            var result = filtered.Select(el =>
            {
                var resultObj = new JObject
                {
                    ["elementId"] = RevitCompat.GetId(el.Id),
                    ["name"] = el.Name,
                    ["category"] = el.Category?.Name,
                    ["typeName"] = doc.GetElement(el.GetTypeId())?.Name,
                    ["viewSpecific"] = el.ViewSpecific
                };
                if (el.ViewSpecific)
                    resultObj["ownerViewId"] = RevitCompat.GetId(el.OwnerViewId);

                // Include requested parameter value in output
                if (!string.IsNullOrEmpty(paramName))
                {
                    var p = el.LookupParameter(paramName);
                    if (p != null)
                        resultObj["parameterValue"] = GetParamValueAsString(doc, p);
                }

                return resultObj;
            }).ToArray();

            return CommandResult.Ok(new
            {
                totalFound,
                returned = result.Length,
                selected = selectResult,
                limit,
                elements = result
            });
        }

        private static bool IsEmpty(Parameter param)
        {
            if (!param.HasValue) return true;
            switch (param.StorageType)
            {
                case StorageType.String: return string.IsNullOrEmpty(param.AsString());
                case StorageType.ElementId: return RevitCompat.GetId(param.AsElementId()) == -1;
                default: return false;
            }
        }

        private static double ConvertToDisplayUnits(Document doc, Parameter param, double internalValue)
        {
            try
            {
                var specId = param.Definition.GetDataType();

                if (specId == SpecTypeId.Length || specId == SpecTypeId.PipeSize ||
                    specId == SpecTypeId.DuctSize || specId == SpecTypeId.BarDiameter ||
                    specId == SpecTypeId.WireDiameter || specId == SpecTypeId.SectionDimension)
                    return internalValue * 304.8; // feet → mm
                if (specId == SpecTypeId.Area)
                    return internalValue * 92903.04; // sq feet → sq mm
                if (specId == SpecTypeId.Volume)
                    return internalValue * 28316846.6; // cu feet → cu mm
                if (specId == SpecTypeId.Angle)
                    return internalValue * (180.0 / Math.PI); // radians → degrees
            }
            catch { }

            // Unknown spec type — return raw value
            return internalValue;
        }

        private static string GetParamValueAsString(Document doc, Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    var raw = param.AsDouble();
                    var display = ConvertToDisplayUnits(doc, param, raw);
                    return Math.Round(display, 2).ToString();
                case StorageType.ElementId:
                    var refEl = doc.GetElement(param.AsElementId());
                    return refEl?.Name ?? RevitCompat.GetId(param.AsElementId()).ToString();
                default:
                    return null;
            }
        }
    }
}
