using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class AiElementFilterHandler : IRevitCommand
    {
        public string Name => "ai_element_filter";
        public string Description => "Filter elements by category, parameter name, value, and comparison operator. Numeric values are in millimeters (auto-converted from Revit internal units).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""category"":{""type"":""string""},""parameterName"":{""type"":""string""},""parameterValue"":{""type"":""string""},""operator"":{""type"":""string"",""enum"":[""equals"",""contains"",""startswith"",""greaterthan"",""lessthan""]},""limit"":{""type"":""integer"",""default"":100},""select"":{""type"":""boolean"",""default"":false}},""required"":[""category""]}";

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
            var selectResult = request.Value<bool?>("select") ?? false;

            if (string.IsNullOrEmpty(categoryName))
                return CommandResult.Fail("category is required.");

            // Find matching BuiltInCategory
            BuiltInCategory? bic = null;
            foreach (BuiltInCategory cat in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var c = Category.GetCategory(doc, cat);
                    if (c != null && c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        bic = cat;
                        break;
                    }
                }
                catch { }
            }

            if (bic == null)
                return CommandResult.Fail($"Category '{categoryName}' not found.");

            var collector = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType();

            var elements = collector.ToList();

            // Filter by parameter if specified
            if (!string.IsNullOrEmpty(paramName) && !string.IsNullOrEmpty(paramValue))
            {
                elements = elements.Where(el =>
                {
                    var param = el.LookupParameter(paramName);
                    if (param == null) return false;

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
                }).ToList();
            }

            var filtered = elements.Take(limit).ToList();

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
                    ["typeName"] = doc.GetElement(el.GetTypeId())?.Name
                };

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
                totalFound = elements.Count,
                returned = result.Length,
                selected = selectResult,
                limit,
                elements = result
            });
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
