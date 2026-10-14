using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListProjectParametersHandler : IRevitCommand
    {
        public string Name => "list_project_parameters";
        public string Description => "List project parameter bindings in the current Revit document.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""includeCategories"":{""type"":""boolean"",""default"":true,""description"":""Include bound category name/id lists for each parameter.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson)
                ? new JObject()
                : JObject.Parse(paramsJson);
            var includeCategories = request.Value<bool?>("includeCategories") ?? true;

            var bindings = new List<ProjectParameterBindingInfo>();
            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();

            while (iterator.MoveNext())
            {
                var definition = iterator.Key;
                var binding = iterator.Current as Binding;
                if (definition == null || binding == null)
                    continue;

                var externalDefinition = definition as ExternalDefinition;
                var groupInfo = GetGroupInfo(definition);
                var dataTypeInfo = GetDataTypeInfo(definition);
                var categories = includeCategories
                    ? GetCategories(binding).OrderBy(c => c.Name).ToArray()
                    : null;

                bindings.Add(new ProjectParameterBindingInfo
                {
                    Name = definition.Name,
                    ParameterGroup = groupInfo.Id,
                    GroupLabel = groupInfo.Label,
                    DataType = dataTypeInfo.Label,
                    SpecId = dataTypeInfo.Id,
                    BindingKind = GetBindingKind(binding),
                    Guid = externalDefinition?.GUID.ToString(),
                    Categories = categories
                });
            }

            return CommandResult.Ok(new
            {
                count = bindings.Count,
                includeCategories,
                bindings = bindings.OrderBy(b => b.Name).ToArray()
            });
        }

        private static string GetBindingKind(Binding binding)
        {
            if (binding is InstanceBinding)
                return "instance";
            if (binding is TypeBinding)
                return "type";
            return binding.GetType().Name;
        }

        private static IEnumerable<ProjectParameterCategoryInfo> GetCategories(Binding binding)
        {
            var elementBinding = binding as ElementBinding;
            var categories = elementBinding?.Categories;
            if (categories == null)
                yield break;

            foreach (Category category in categories)
            {
                if (category == null)
                    continue;

                yield return new ProjectParameterCategoryInfo
                {
                    Name = category.Name,
                    Id = RevitCompat.GetId(category.Id)
                };
            }
        }

        private static ForgeInfo GetGroupInfo(Definition definition)
        {
            try
            {
                var groupTypeId = definition.GetGroupTypeId();
                return new ForgeInfo(GetTypeId(groupTypeId), GetGroupLabel(groupTypeId));
            }
            catch
            {
                return ForgeInfo.Empty;
            }
        }

        private static ForgeInfo GetDataTypeInfo(Definition definition)
        {
            try
            {
                var dataType = definition.GetDataType();
                return new ForgeInfo(GetTypeId(dataType), GetSpecLabel(dataType));
            }
            catch
            {
                return ForgeInfo.Empty;
            }
        }

        private static string GetTypeId(ForgeTypeId id)
        {
            if (id == null)
                return null;

            try
            {
                return string.IsNullOrEmpty(id.TypeId) ? null : id.TypeId;
            }
            catch
            {
                return null;
            }
        }

        private static string GetGroupLabel(ForgeTypeId id)
        {
            try
            {
                return LabelUtils.GetLabelForGroup(id);
            }
            catch
            {
                return null;
            }
        }

        private static string GetSpecLabel(ForgeTypeId id)
        {
            try
            {
                return LabelUtils.GetLabelForSpec(id);
            }
            catch
            {
                return null;
            }
        }

        private sealed class ForgeInfo
        {
            public static readonly ForgeInfo Empty = new ForgeInfo(null, null);

            public ForgeInfo(string id, string label)
            {
                Id = id;
                Label = label;
            }

            public string Id { get; }
            public string Label { get; }
        }

        private sealed class ProjectParameterBindingInfo
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("parameterGroup", NullValueHandling = NullValueHandling.Ignore)]
            public string ParameterGroup { get; set; }

            [JsonProperty("groupLabel", NullValueHandling = NullValueHandling.Ignore)]
            public string GroupLabel { get; set; }

            [JsonProperty("dataType", NullValueHandling = NullValueHandling.Ignore)]
            public string DataType { get; set; }

            [JsonProperty("specId", NullValueHandling = NullValueHandling.Ignore)]
            public string SpecId { get; set; }

            [JsonProperty("bindingKind")]
            public string BindingKind { get; set; }

            [JsonProperty("guid", NullValueHandling = NullValueHandling.Ignore)]
            public string Guid { get; set; }

            [JsonProperty("categories", NullValueHandling = NullValueHandling.Ignore)]
            public ProjectParameterCategoryInfo[] Categories { get; set; }
        }

        private sealed class ProjectParameterCategoryInfo
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("id")]
            public long Id { get; set; }
        }
    }
}
