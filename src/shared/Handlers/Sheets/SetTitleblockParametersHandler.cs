using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetTitleblockParametersHandler : IRevitCommand
    {
        public string Name => "set_titleblock_parameters";
        public string Description => "Set titleblock instance and type parameters for a sheet";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""parameters""],
  ""properties"": {
    ""sheet_id"": { ""type"": ""integer"" },
    ""sheet_number"": { ""type"": ""string"" },
    ""target"": { ""type"": ""string"", ""enum"": [""instance"", ""type"", ""both""], ""default"": ""instance"" },
    ""parameters"": { ""type"": ""object"", ""additionalProperties"": true }
  }
}";

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
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            var sheetId = request.Value<long?>("sheet_id") ?? request.Value<long?>("sheetId");
            var sheetNumber = request.Value<string>("sheet_number") ?? request.Value<string>("sheetNumber") ?? "";
            var target = request.Value<string>("target") ?? "instance";
            var parametersToken = request["parameters"] as JObject;

            if (parametersToken == null || !parametersToken.Properties().Any())
                return CommandResult.Fail("parameters is required and must be a non-empty object.");

            if (!target.Equals("instance", StringComparison.OrdinalIgnoreCase) &&
                !target.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                !target.Equals("both", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("target must be one of: instance, type, both.");
            }

            // Resolve sheet
            ViewSheet sheet = null;
            if (sheetId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(sheetId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(sheetId.Value));

                sheet = doc.GetElement(RevitCompat.ToElementId(sheetId.Value)) as ViewSheet;
            }
            else if (!string.IsNullOrEmpty(sheetNumber))
            {
                sheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                sheet = doc.ActiveView as ViewSheet;
            }

            if (sheet == null)
                return CommandResult.Fail("Sheet could not be resolved. Provide valid sheet_id, sheet_number, or ensure active view is a sheet.");

            if (sheet.IsPlaceholder)
                return CommandResult.Fail("Placeholder sheets do not have titleblocks.");

            // Resolve title block
            var titleBlockInstance = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .FirstOrDefault();

            if (titleBlockInstance == null)
                return CommandResult.Fail("No titleblock instance found on the resolved sheet.");

            var titleBlockType = doc.GetElement(titleBlockInstance.GetTypeId()) as FamilySymbol;

            var results = new Dictionary<string, object>();

            using (var tx = new Transaction(doc, "RvtMcp: set titleblock parameters"))
            {
                tx.Start();
                try
                {
                    foreach (var property in parametersToken.Properties())
                    {
                        string name = property.Name;
                        JToken valueToken = property.Value;

                        bool found = false;
                        string errorMsg = null;
                        bool anySet = false;
                        bool anyReadOnly = false;
                        var targetResults = new Dictionary<string, object>();

                        // 1. Try instance parameter if target is "instance" or "both"
                        if (target.Equals("instance", StringComparison.OrdinalIgnoreCase) || target.Equals("both", StringComparison.OrdinalIgnoreCase))
                        {
                            var param = FindParameter(titleBlockInstance, name);
                            if (param != null)
                            {
                                found = true;
                                if (param.IsReadOnly)
                                {
                                    anyReadOnly = true;
                                    targetResults["instance"] = new { status = "skipped", reason = "read-only", storage_type = param.StorageType.ToString() };
                                }
                                else
                                {
                                    if (!TrySetSingleParameter(doc, param, valueToken, out errorMsg))
                                    {
                                        tx.RollBack();
                                        return CommandResult.Fail($"Failed to set instance parameter '{name}': {errorMsg}");
                                    }
                                    anySet = true;
                                    targetResults["instance"] = new { status = "set", storage_type = param.StorageType.ToString() };
                                }
                            }
                        }

                        // 2. Try type parameter independently so target="both" really applies both scopes.
                        if (titleBlockType != null && (target.Equals("type", StringComparison.OrdinalIgnoreCase) || target.Equals("both", StringComparison.OrdinalIgnoreCase)))
                        {
                            var param = FindParameter(titleBlockType, name);
                            if (param != null)
                            {
                                found = true;
                                if (param.IsReadOnly)
                                {
                                    anyReadOnly = true;
                                    targetResults["type"] = new { status = "skipped", reason = "read-only", storage_type = param.StorageType.ToString() };
                                }
                                else
                                {
                                    if (!TrySetSingleParameter(doc, param, valueToken, out errorMsg))
                                    {
                                        tx.RollBack();
                                        return CommandResult.Fail($"Failed to set type parameter '{name}': {errorMsg}");
                                    }
                                    anySet = true;
                                    targetResults["type"] = new { status = "set", storage_type = param.StorageType.ToString() };
                                }
                            }
                        }

                        if (!found)
                        {
                            tx.RollBack();
                            return CommandResult.Fail($"Parameter '{name}' not found on the titleblock.");
                        }

                        if (!anySet && anyReadOnly)
                        {
                            results[name] = new { status = "skipped", reason = "read-only", targets = targetResults };
                        }
                        else
                        {
                            results[name] = new
                            {
                                status = anyReadOnly ? "partial" : "set",
                                targets = targetResults
                            };
                        }
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Unexpected error setting titleblock parameters: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                updated = true,
                sheet_id = RevitCompat.GetId(sheet.Id),
                sheet_number = sheet.SheetNumber,
                title_block_instance_id = RevitCompat.GetId(titleBlockInstance.Id),
                title_block_type_id = titleBlockType != null ? RevitCompat.GetId(titleBlockType.Id) : (long?)null,
                parameters = results,
                error = (string)null
            });
        }

        private static Parameter FindParameter(Element elem, string name)
        {
            var p = elem.LookupParameter(name);
            if (p != null) return p;

            foreach (Parameter param in elem.Parameters)
            {
                if (param.Definition != null && param.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return param;
            }
            return null;
        }

        private static bool TrySetSingleParameter(Document doc, Parameter parameter, JToken valueToken, out string error)
        {
            error = null;
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        {
                            string val = valueToken.Type == JTokenType.Null ? "" : valueToken.ToString();
                            return parameter.Set(val);
                        }
                    case StorageType.Integer:
                        {
                            if (valueToken.Type == JTokenType.Boolean)
                            {
                                return parameter.Set(valueToken.Value<bool>() ? 1 : 0);
                            }
                            if (!int.TryParse(valueToken.ToString(), out int val))
                            {
                                error = "Value must be an integer.";
                                return false;
                            }
                            return parameter.Set(val);
                        }
                    case StorageType.Double:
                        {
                            if (!double.TryParse(valueToken.ToString(), out double val))
                            {
                                error = "Value must be a double.";
                                return false;
                            }
                            double converted = ConvertDoubleToInternal(parameter, val);
                            return parameter.Set(converted);
                        }
                    case StorageType.ElementId:
                        {
                            if (!long.TryParse(valueToken.ToString(), out long val))
                            {
                                error = "Value must be a valid ElementId (integer).";
                                return false;
                            }
                            if (!RevitCompat.CanRepresentElementId(val))
                            {
                                error = RevitCompat.ElementIdRangeError(val);
                                return false;
                            }
                            return parameter.Set(RevitCompat.ToElementId(val));
                        }
                    default:
                        error = $"Unsupported parameter storage type: {parameter.StorageType}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static double ConvertDoubleToInternal(Parameter parameter, double value)
        {
            string spec = GetDoubleSpec(parameter);
            switch (spec)
            {
                case "length":
                    return value / 304.8;
                case "area":
                    return value / 0.09290304;
                case "volume":
                    return value / 0.028316846592;
                case "angle":
                    return value * Math.PI / 180.0;
                default:
                    return value;
            }
        }

        private static string GetDoubleSpec(Parameter parameter)
        {
            string typeId;
            try
            {
                typeId = parameter.Definition?.GetDataType()?.TypeId;
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(typeId))
                return null;

            if (TypeIdMatchesSpec(typeId, "length"))
                return "length";
            if (TypeIdMatchesSpec(typeId, "area"))
                return "area";
            if (TypeIdMatchesSpec(typeId, "volume"))
                return "volume";
            if (TypeIdMatchesSpec(typeId, "angle"))
                return "angle";

            return null;
        }

        private static bool TypeIdMatchesSpec(string typeId, string specName)
        {
            var lower = typeId.ToLowerInvariant();
            var colonPattern = ":" + specName;
            var dotPattern = "." + specName;

            return lower.EndsWith(colonPattern, StringComparison.Ordinal)
                || lower.EndsWith(dotPattern, StringComparison.Ordinal)
                || lower.Contains(colonPattern + "-")
                || lower.Contains(dotPattern + "-");
        }
    }
}
