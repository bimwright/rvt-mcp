using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateSharedParameterHandler : IRevitCommand
    {
        public string Name => "create_shared_parameter";
        public string Description => "Create a shared parameter definition in the shared parameter file.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""name"", ""dataTypeId""],
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""dataTypeId"": {
      ""type"": ""string"",
      ""description"": ""Forge data type id or supported alias such as string, integer, number, length, area, volume, angle, yesno.""
    },
    ""groupName"": { ""type"": ""string"", ""default"": ""RvtMcp"" },
    ""guid"": {
      ""type"": ""string"",
      ""description"": ""Optional GUID. If omitted, handler generates a new GUID.""
    },
    ""sharedParameterFilePath"": { ""type"": ""string"" },
    ""createFileIfMissing"": { ""type"": ""boolean"", ""default"": true },
    ""description"": { ""type"": ""string"" },
    ""visible"": { ""type"": ""boolean"", ""default"": true },
    ""userModifiable"": { ""type"": ""boolean"", ""default"": true },
    ""hideWhenNoValue"": { ""type"": ""boolean"", ""default"": false }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var name = request.Value<string>("name");
            var dataTypeIdInput = request.Value<string>("dataTypeId");
            var groupName = request.Value<string>("groupName") ?? "RvtMcp";
            var guidInput = request.Value<string>("guid");
            var customPath = request.Value<string>("sharedParameterFilePath");
            var createFileIfMissing = request.Value<bool?>("createFileIfMissing") ?? true;
            var description = request.Value<string>("description") ?? string.Empty;
            var visible = request.Value<bool?>("visible") ?? true;
            var userModifiable = request.Value<bool?>("userModifiable") ?? true;
            var hideWhenNoValue = request.Value<bool?>("hideWhenNoValue") ?? false;

            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("name is required and cannot be empty.");
            if (string.IsNullOrWhiteSpace(dataTypeIdInput))
                return CommandResult.Fail("dataTypeId is required.");

            string resolvedDataTypeId = ResolveDataTypeId(dataTypeIdInput);
            if (string.IsNullOrEmpty(resolvedDataTypeId))
                return CommandResult.Fail($"Unsupported dataTypeId or alias: {dataTypeIdInput}");

            Guid targetGuid = Guid.Empty;
            if (!string.IsNullOrEmpty(guidInput))
            {
                if (!Guid.TryParse(guidInput, out targetGuid))
                    return CommandResult.Fail($"Invalid GUID format: {guidInput}");
            }
            else
            {
                targetGuid = Guid.NewGuid();
            }

            if (!string.IsNullOrEmpty(customPath) && !Path.IsPathRooted(customPath))
                return CommandResult.Fail("sharedParameterFilePath must be an absolute path.");

            string resolvedPath = customPath;
            if (string.IsNullOrEmpty(resolvedPath))
            {
                resolvedPath = app.Application.SharedParametersFilename;
            }

            bool fileCreated = false;
            if (string.IsNullOrEmpty(resolvedPath))
            {
                if (createFileIfMissing)
                {
                    string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string dir = Path.Combine(localApp, "Bimwright", "rvt-mcp");
                    resolvedPath = Path.Combine(dir, "shared-parameters.txt");
                }
                else
                {
                    return CommandResult.Fail("Revit has no active shared parameter file and sharedParameterFilePath is not supplied.");
                }
            }

            if (!File.Exists(resolvedPath))
            {
                if (createFileIfMissing)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        // Create file with standard tab-separated Revit header
                        string header = "# This is a Revit shared parameter file.\n" +
                                        "# Do not edit manually.\n" +
                                        "*META\tVERSION\tMINVERSION\n" +
                                        "META\t2\t1\n" +
                                        "*GROUP\tID\tNAME\n" +
                                        "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\n";
                        File.WriteAllText(resolvedPath, header, System.Text.Encoding.UTF8);
                        fileCreated = true;
                    }
                    catch (Exception ex)
                    {
                        return CommandResult.Fail($"Failed to create shared parameter file at {resolvedPath}: {ex.Message}");
                    }
                }
                else
                {
                    return CommandResult.Fail($"Shared parameter file does not exist at {resolvedPath}. Set createFileIfMissing to true to create it.");
                }
            }

            string originalFilename = app.Application.SharedParametersFilename;
            try
            {
                app.Application.SharedParametersFilename = resolvedPath;
                var defFile = app.Application.OpenSharedParameterFile();
                if (defFile == null)
                    return CommandResult.Fail($"Revit was unable to open the shared parameter file at {resolvedPath}.");

                // Check for existing GUID across all groups
                foreach (DefinitionGroup g in defFile.Groups)
                {
                    foreach (Definition d in g.Definitions)
                    {
                        var extDef = d as ExternalDefinition;
                        if (extDef != null)
                        {
                            if (extDef.GUID == targetGuid)
                            {
                                string existingDataType = string.Empty;
                                try { existingDataType = extDef.GetDataType().TypeId; } catch { }

                                if (string.Equals(extDef.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(existingDataType, resolvedDataTypeId, StringComparison.OrdinalIgnoreCase))
                                {
                                    return CommandResult.Ok(new
                                    {
                                        created = false,
                                        alreadyExisted = true,
                                        sharedParameterFilePath = resolvedPath,
                                        fileCreated = false,
                                        groupName = g.Name,
                                        name = extDef.Name,
                                        guid = extDef.GUID.ToString("d"),
                                        dataTypeId = existingDataType,
                                        dataTypeLabel = GetDataTypeLabel(extDef),
                                        visible = extDef.Visible,
                                        userModifiable = extDef.UserModifiable,
                                        hideWhenNoValue = extDef.HideWhenNoValue
                                    });
                                }
                                else
                                {
                                    return CommandResult.Fail($"GUID {targetGuid} already exists with a different name ('{extDef.Name}') or data type ('{existingDataType}').");
                                }
                            }
                        }
                    }
                }

                // Check for existing parameter with same name in target group
                var targetGroup = defFile.Groups.get_Item(groupName);
                if (targetGroup == null)
                {
                    targetGroup = defFile.Groups.Create(groupName);
                }
                else
                {
                    var existingByName = targetGroup.Definitions.get_Item(name);
                    if (existingByName != null)
                    {
                        var extDef = existingByName as ExternalDefinition;
                        return CommandResult.Fail($"Parameter with name '{name}' already exists in group '{groupName}' with a different GUID ('{extDef?.GUID}').");
                    }
                }

                // Create the external definition
                var forgeTypeId = new ForgeTypeId(resolvedDataTypeId);
                var options = new ExternalDefinitionCreationOptions(name, forgeTypeId)
                {
                    GUID = targetGuid,
                    Description = description,
                    Visible = visible,
                    UserModifiable = userModifiable,
                    HideWhenNoValue = hideWhenNoValue
                };

                var newDefinition = targetGroup.Definitions.Create(options) as ExternalDefinition;
                if (newDefinition == null)
                    return CommandResult.Fail("Failed to create shared parameter definition.");

                return CommandResult.Ok(new
                {
                    created = true,
                    alreadyExisted = false,
                    sharedParameterFilePath = resolvedPath,
                    fileCreated,
                    groupName = targetGroup.Name,
                    name = newDefinition.Name,
                    guid = newDefinition.GUID.ToString("d"),
                    dataTypeId = resolvedDataTypeId,
                    dataTypeLabel = GetDataTypeLabel(newDefinition),
                    visible = newDefinition.Visible,
                    userModifiable = newDefinition.UserModifiable,
                    hideWhenNoValue = newDefinition.HideWhenNoValue
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to create shared parameter: {ex.Message}");
            }
            finally
            {
                app.Application.SharedParametersFilename = originalFilename;
            }
        }

        private static string ResolveDataTypeId(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var lower = input.ToLowerInvariant();
            switch (lower)
            {
                case "string":
                case "text":
                    return "autodesk.spec.aec:string";
                case "integer":
                case "int":
                    return "autodesk.spec.aec:int";
                case "number":
                case "double":
                    return "autodesk.spec.aec:number";
                case "length":
                    return "autodesk.spec.aec:length";
                case "area":
                    return "autodesk.spec.aec:area";
                case "volume":
                    return "autodesk.spec.aec:volume";
                case "angle":
                    return "autodesk.spec.aec:angle";
                case "yesno":
                case "boolean":
                case "bool":
                    return "autodesk.spec.aec:yesno";
                case "url":
                    return "autodesk.spec.aec:url";
                default:
                    return input; // Raw Forge ID
            }
        }

        private static string GetDataTypeLabel(ExternalDefinition def)
        {
            try
            {
                return LabelUtils.GetLabelForSpec(def.GetDataType());
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
