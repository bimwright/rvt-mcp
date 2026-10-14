using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ExportSharedParameterFileHandler : IRevitCommand
    {
        public string Name => "export_shared_parameter_file";
        public string Description => "Export the active or custom shared parameter file as structured DTO data.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""sharedParameterFilePath"": { ""type"": ""string"" },
    ""includeRawLines"": { ""type"": ""boolean"", ""default"": false },
    ""includeBindings"": { ""type"": ""boolean"", ""default"": true },
    ""output"": { ""type"": ""string"", ""enum"": [""inline"", ""file""], ""default"": ""inline"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var customPath = request.Value<string>("sharedParameterFilePath");
            var includeRawLines = request.Value<bool?>("includeRawLines") ?? false;
            var includeBindings = request.Value<bool?>("includeBindings") ?? true;

            if (!string.IsNullOrEmpty(customPath) && !Path.IsPathRooted(customPath))
                return CommandResult.Fail("sharedParameterFilePath must be an absolute path.");

            string resolvedPath = customPath;
            if (string.IsNullOrEmpty(resolvedPath))
            {
                resolvedPath = app.Application.SharedParametersFilename;
            }

            if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
            {
                return CommandResult.Ok(new
                {
                    sharedParameterFilePath = resolvedPath ?? string.Empty,
                    exists = false,
                    lineCount = 0,
                    groupCount = 0,
                    definitionCount = 0,
                    groups = Array.Empty<object>(),
                    rawLines = (object)null
                });
            }

            string[] rawLines = null;
            int lineCount = 0;
            if (includeRawLines)
            {
                try
                {
                    rawLines = File.ReadAllLines(resolvedPath);
                    lineCount = rawLines.Length;
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail($"Failed to read shared parameter file lines: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    lineCount = File.ReadLines(resolvedPath).Count();
                }
                catch { }
            }

            var bindingsMap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (includeBindings)
            {
                try
                {
                    var iterator = doc.ParameterBindings.ForwardIterator();
                    iterator.Reset();
                    while (iterator.MoveNext())
                    {
                        if (iterator.Key is ExternalDefinition extDef)
                        {
                            bindingsMap.Add(extDef.GUID.ToString("d"));
                        }
                    }
                }
                catch { }
            }

            string originalFilename = app.Application.SharedParametersFilename;
            DefinitionFile defFile = null;

            try
            {
                if (!string.IsNullOrEmpty(customPath))
                {
                    app.Application.SharedParametersFilename = customPath;
                }
                defFile = app.Application.OpenSharedParameterFile();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to open shared parameter file: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(customPath))
                {
                    app.Application.SharedParametersFilename = originalFilename;
                }
            }

            if (defFile == null)
            {
                return CommandResult.Ok(new
                {
                    sharedParameterFilePath = resolvedPath,
                    exists = false,
                    lineCount,
                    groupCount = 0,
                    definitionCount = 0,
                    groups = Array.Empty<object>(),
                    rawLines = rawLines
                });
            }

            var groupsList = new List<object>();
            int definitionCount = 0;

            foreach (DefinitionGroup group in defFile.Groups)
            {
                if (group == null) continue;

                var definitionsList = new List<object>();
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is ExternalDefinition extDef)
                    {
                        definitionCount++;
                        string dataTypeId = string.Empty;
                        try
                        {
                            dataTypeId = extDef.GetDataType().TypeId;
                        }
                        catch { }

                        definitionsList.Add(new
                        {
                            name = extDef.Name,
                            guid = extDef.GUID.ToString("d"),
                            dataTypeId = dataTypeId,
                            visible = extDef.Visible,
                            userModifiable = extDef.UserModifiable,
                            hideWhenNoValue = extDef.HideWhenNoValue,
                            isBound = bindingsMap.Contains(extDef.GUID.ToString("d"))
                        });
                    }
                }

                groupsList.Add(new
                {
                    name = group.Name,
                    definitions = definitionsList.OrderBy(d => ((dynamic)d).name).ToArray()
                });
            }

            return CommandResult.Ok(new
            {
                sharedParameterFilePath = resolvedPath,
                exists = true,
                lineCount,
                groupCount = groupsList.Count,
                definitionCount,
                groups = groupsList.OrderBy(g => ((dynamic)g).name).ToArray(),
                rawLines = rawLines
            });
        }
    }
}
