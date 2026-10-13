using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListProjectParameterBindingsHandler : IRevitCommand
    {
        public string Name => "list_project_parameter_bindings";
        public string Description => "List all project and shared parameter bindings in the current document.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""includeCategories"": { ""type"": ""boolean"", ""default"": true },
    ""includeShared"": { ""type"": ""boolean"", ""default"": true },
    ""includeProject"": { ""type"": ""boolean"", ""default"": true },
    ""nameFilter"": { ""type"": ""string"" },
    ""guid"": { ""type"": ""string"" },
    ""limit"": { ""type"": ""integer"", ""default"": 1000, ""minimum"": 1, ""maximum"": 5000 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var includeCategories = request.Value<bool?>("includeCategories") ?? true;
            var includeShared = request.Value<bool?>("includeShared") ?? true;
            var includeProject = request.Value<bool?>("includeProject") ?? true;
            var nameFilter = request.Value<string>("nameFilter");
            var guidInput = request.Value<string>("guid");
            var limit = request.Value<int?>("limit") ?? 1000;

            if (!includeShared && !includeProject)
                return CommandResult.Fail("Either includeShared or includeProject must be true.");

            Guid guidObj = Guid.Empty;
            if (!string.IsNullOrEmpty(guidInput))
            {
                if (!Guid.TryParse(guidInput, out guidObj))
                    return CommandResult.Fail("Invalid GUID format.");
            }

            if (limit < 1 || limit > 5000)
                return CommandResult.Fail("limit must be between 1 and 5000.");

            var bindingsList = new List<object>();
            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();

            var parameterElements = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterElement))
                .Cast<ParameterElement>()
                .ToList();

            while (iterator.MoveNext())
            {
                var definition = iterator.Key;
                var binding = iterator.Current as Binding;
                if (definition == null || binding == null) continue;

                var sharedGuid = SharedParameterGuid.Of(doc, definition);
                bool isShared = sharedGuid.HasValue;

                if (isShared && !includeShared) continue;
                if (!isShared && !includeProject) continue;

                if (!string.IsNullOrEmpty(nameFilter) && definition.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (guidObj != Guid.Empty && sharedGuid != guidObj) continue;

                ParameterElement paramElem = definition is InternalDefinition internalDefinition
                    ? doc.GetElement(internalDefinition.Id) as ParameterElement
                    : null;
                if (paramElem == null && isShared)
                {
                    paramElem = parameterElements.FirstOrDefault(pe => pe is SharedParameterElement spe && spe.GuidValue == sharedGuid.Value);
                }
                else if (paramElem == null)
                {
                    paramElem = parameterElements.FirstOrDefault(pe => !(pe is SharedParameterElement) && string.Equals(pe.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
                }

                long? parameterElementId = RevitCompat.GetIdOrNull(paramElem?.Id);

                string parameterGroupId = null;
                string groupLabel = null;
                try
                {
                    var groupTypeId = definition.GetGroupTypeId();
                    parameterGroupId = groupTypeId.TypeId;
                    groupLabel = LabelUtils.GetLabelForGroup(groupTypeId);
                }
                catch { }

                string dataTypeId = null;
                string dataTypeLabel = null;
                try
                {
                    var dataType = definition.GetDataType();
                    dataTypeId = dataType.TypeId;
                    dataTypeLabel = LabelUtils.GetLabelForSpec(dataType);
                }
                catch { }

                var categories = new List<CategoryInfo>();
                if (includeCategories && binding is ElementBinding eb && eb.Categories != null)
                {
                    foreach (Category cat in eb.Categories)
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

                var bindingKind = "instance";
                if (binding is TypeBinding) bindingKind = "type";

                bindingsList.Add(new
                {
                    name = definition.Name,
                    parameterElementId = parameterElementId,
                    isShared = isShared,
                    guid = sharedGuid?.ToString("d"),
                    bindingKind = bindingKind,
                    parameterGroupId = parameterGroupId,
                    groupLabel = groupLabel,
                    dataTypeId = dataTypeId,
                    dataTypeLabel = dataTypeLabel,
                    categoryCount = categories.Count,
                    categories = includeCategories ? categories.OrderBy(c => c.Name).ToArray() : null
                });
            }

            var returnedList = bindingsList.OrderBy(b => ((dynamic)b).name).Take(limit).ToList();

            return CommandResult.Ok(new
            {
                count = bindingsList.Count,
                returned = returnedList.Count,
                bindings = returnedList
            });
        }

        private class CategoryInfo
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }
        }
    }
}
