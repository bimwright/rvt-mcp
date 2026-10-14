using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetTitleblockParametersHandler : IRevitCommand
    {
        public string Name => "get_titleblock_parameters";
        public string Description => "Get titleblock instance and type parameters for a sheet";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""sheet_id"": { ""type"": ""integer"" },
    ""sheet_number"": { ""type"": ""string"" },
    ""target"": { ""type"": ""string"", ""enum"": [""instance"", ""type"", ""both""], ""default"": ""both"" },
    ""include_read_only"": { ""type"": ""boolean"", ""default"": true }
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
            var target = request.Value<string>("target") ?? "both";
            var includeReadOnly = request.Value<bool?>("include_read_only") ?? request.Value<bool?>("includeReadOnly") ?? true;

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

            var instanceParams = new List<object>();
            var typeParams = new List<object>();

            // Collect instance parameters
            if (target.Equals("instance", StringComparison.OrdinalIgnoreCase) || target.Equals("both", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Parameter param in titleBlockInstance.Parameters)
                {
                    if (param == null || param.Definition == null) continue;
                    if (!includeReadOnly && param.IsReadOnly) continue;

                    instanceParams.Add(BuildParameterDto(param));
                }
            }

            // Collect type parameters
            if (titleBlockType != null && (target.Equals("type", StringComparison.OrdinalIgnoreCase) || target.Equals("both", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (Parameter param in titleBlockType.Parameters)
                {
                    if (param == null || param.Definition == null) continue;
                    if (!includeReadOnly && param.IsReadOnly) continue;

                    typeParams.Add(BuildParameterDto(param));
                }
            }

            return CommandResult.Ok(new
            {
                sheet_id = RevitCompat.GetId(sheet.Id),
                sheet_number = sheet.SheetNumber,
                title_block_instance_id = RevitCompat.GetId(titleBlockInstance.Id),
                title_block_type_id = titleBlockType != null ? RevitCompat.GetId(titleBlockType.Id) : (long?)null,
                instance_parameters = instanceParams,
                type_parameters = typeParams
            });
        }

        private static object BuildParameterDto(Parameter parameter)
        {
            var definition = parameter.Definition;
            string specTypeId = null;
            try
            {
                specTypeId = definition?.GetDataType()?.TypeId;
            }
            catch
            {
                // Catch and leave as null if it throws in some Revit versions
            }

            object value = null;
            if (parameter.HasValue)
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        value = parameter.AsString();
                        break;
                    case StorageType.Integer:
                        value = parameter.AsInteger();
                        break;
                    case StorageType.Double:
                        value = ConvertDoubleToExternal(parameter, parameter.AsDouble());
                        break;
                    case StorageType.ElementId:
                        value = RevitCompat.GetId(parameter.AsElementId());
                        break;
                }
            }

            return new
            {
                name = definition?.Name ?? "",
                storage_type = parameter.StorageType.ToString(),
                value = value,
                is_read_only = parameter.IsReadOnly,
                spec_type_id = specTypeId
            };
        }

        private static double ConvertDoubleToExternal(Parameter parameter, double value)
        {
            string spec = GetDoubleSpec(parameter);
            switch (spec)
            {
                case "length":
                    return value * 304.8;
                case "area":
                    return value * 0.09290304;
                case "volume":
                    return value * 0.028316846592;
                case "angle":
                    return value * 180.0 / Math.PI;
                default:
                    return value;
                // Otherwise return numeric value directly
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
