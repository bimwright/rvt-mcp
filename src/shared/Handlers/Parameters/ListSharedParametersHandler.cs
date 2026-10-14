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
    public class ListSharedParametersHandler : IRevitCommand
    {
        public string Name => "list_shared_parameters";
        public string Description => "List all shared parameters defined in the shared parameter file.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""sharedParameterFilePath"": {
      ""type"": ""string"",
      ""description"": ""Optional absolute .txt path. If omitted, uses Revit Application.SharedParametersFilename.""
    },
    ""groupName"": {
      ""type"": ""string"",
      ""description"": ""Optional exact shared-parameter group name filter.""
    },
    ""includeBindings"": {
      ""type"": ""boolean"",
      ""default"": true
    },
    ""limit"": {
      ""type"": ""integer"",
      ""default"": 1000,
      ""minimum"": 1,
      ""maximum"": 5000
    }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var customPath = request.Value<string>("sharedParameterFilePath");
            var groupNameFilter = request.Value<string>("groupName");
            var includeBindings = request.Value<bool?>("includeBindings") ?? true;
            var limit = request.Value<int?>("limit") ?? 1000;

            if (limit < 1 || limit > 5000)
                return CommandResult.Fail("limit must be between 1 and 5000.");

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
                    groupCount = 0,
                    definitionCount = 0,
                    returned = 0,
                    includeBindings,
                    definitions = Array.Empty<object>(),
                    warnings = new[] { "Shared parameters file does not exist or is not set." }
                });
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
                    groupCount = 0,
                    definitionCount = 0,
                    returned = 0,
                    includeBindings,
                    definitions = Array.Empty<object>(),
                    warnings = new[] { "Could not load the shared parameters file." }
                });
            }

            // If includeBindings is true, build a map from GUID string to binding and category info
            var bindingsMap = new Dictionary<string, (string BindingKind, string ParameterGroupId, List<CategoryInfo> Categories)>(StringComparer.OrdinalIgnoreCase);
            if (includeBindings)
            {
                try
                {
                    var iterator = doc.ParameterBindings.ForwardIterator();
                    iterator.Reset();
                    while (iterator.MoveNext())
                    {
                        var definition = iterator.Key;
                        var binding = iterator.Current as ElementBinding;
                        if (definition == null || binding == null) continue;

                        var externalDefinition = definition as ExternalDefinition;
                        if (externalDefinition != null)
                        {
                            var guidStr = externalDefinition.GUID.ToString("d");
                            var bindingKind = binding is TypeBinding ? "type" : "instance";
                            var parameterGroupId = string.Empty;
                            try
                            {
                                parameterGroupId = definition.GetGroupTypeId().TypeId;
                            }
                            catch { }

                            var categories = new List<CategoryInfo>();
                            if (binding.Categories != null)
                            {
                                foreach (Category cat in binding.Categories)
                                {
                                    if (cat != null)
                                    {
                                        categories.Add(new CategoryInfo
                                        {
                                            Id = RevitCompat.GetId(cat.Id),
                                            Name = cat.Name
                                        });
                                    }
                                }
                            }

                            bindingsMap[guidStr] = (bindingKind, parameterGroupId, categories);
                        }
                    }
                }
                catch { }
            }

            var definitions = new List<SharedDefinitionDto>();
            int groupCount = 0;
            int totalDefinitions = 0;

            foreach (DefinitionGroup group in defFile.Groups)
            {
                if (group == null) continue;
                groupCount++;

                if (!string.IsNullOrEmpty(groupNameFilter) && !string.Equals(group.Name, groupNameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (Definition definition in group.Definitions)
                {
                    if (definition == null) continue;
                    totalDefinitions++;

                    if (definitions.Count >= limit) continue;

                    var externalDef = definition as ExternalDefinition;
                    if (externalDef == null) continue;

                    var guidStr = externalDef.GUID.ToString("d");
                    bool isBound = bindingsMap.ContainsKey(guidStr);
                    string bindingKind = null;
                    string parameterGroupId = null;
                    List<CategoryInfo> cats = null;

                    if (isBound)
                    {
                        var boundInfo = bindingsMap[guidStr];
                        bindingKind = boundInfo.BindingKind;
                        parameterGroupId = boundInfo.ParameterGroupId;
                        cats = boundInfo.Categories;
                    }

                    string dataTypeId = string.Empty;
                    string dataTypeLabel = string.Empty;
                    try
                    {
                        var typeId = externalDef.GetDataType();
                        dataTypeId = typeId.TypeId;
                        dataTypeLabel = LabelUtils.GetLabelForSpec(typeId);
                    }
                    catch { }

                    definitions.Add(new SharedDefinitionDto
                    {
                        Name = externalDef.Name,
                        Guid = guidStr,
                        GroupName = group.Name,
                        DataTypeId = dataTypeId,
                        DataTypeLabel = dataTypeLabel,
                        Visible = externalDef.Visible,
                        UserModifiable = externalDef.UserModifiable,
                        HideWhenNoValue = externalDef.HideWhenNoValue,
                        Description = externalDef.Description,
                        IsBound = isBound,
                        BindingKind = bindingKind,
                        ParameterGroupId = parameterGroupId,
                        Categories = cats
                    });
                }
            }

            return CommandResult.Ok(new
            {
                sharedParameterFilePath = resolvedPath,
                exists = true,
                groupCount,
                definitionCount = totalDefinitions,
                returned = definitions.Count,
                includeBindings,
                definitions = definitions.OrderBy(d => d.GroupName).ThenBy(d => d.Name).ToArray(),
                warnings = Array.Empty<string>()
            });
        }

        private class CategoryInfo
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }
        }

        private class SharedDefinitionDto
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("guid")]
            public string Guid { get; set; }

            [JsonProperty("groupName")]
            public string GroupName { get; set; }

            [JsonProperty("dataTypeId")]
            public string DataTypeId { get; set; }

            [JsonProperty("dataTypeLabel")]
            public string DataTypeLabel { get; set; }

            [JsonProperty("visible")]
            public bool Visible { get; set; }

            [JsonProperty("userModifiable")]
            public bool UserModifiable { get; set; }

            [JsonProperty("hideWhenNoValue")]
            public bool HideWhenNoValue { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("isBound")]
            public bool IsBound { get; set; }

            [JsonProperty("bindingKind", NullValueHandling = NullValueHandling.Ignore)]
            public string BindingKind { get; set; }

            [JsonProperty("parameterGroupId", NullValueHandling = NullValueHandling.Ignore)]
            public string ParameterGroupId { get; set; }

            [JsonProperty("categories", NullValueHandling = NullValueHandling.Ignore)]
            public List<CategoryInfo> Categories { get; set; }
        }
    }
}
