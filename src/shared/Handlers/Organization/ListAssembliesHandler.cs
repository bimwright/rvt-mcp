using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListAssembliesHandler : IRevitCommand
    {
        public string Name => "list_assemblies";
        public string Description => "List a bounded page of Revit assembly instances in the active document.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""includeMembers"":{""type"":""boolean"",""default"":false,""description"":""Include a bounded member ID preview for each assembly.""},""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""max_results"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":500},""max_members_per_assembly"":{""type"":""integer"",""default"":50,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            var includeMembers = request.Value<bool?>("includeMembers") ?? false;
            if (!ResponsePaging.TryParse(request, "start_index", "max_results", 100, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);
            var maxMembers = request.Value<int?>("max_members_per_assembly") ?? 50;
            if (maxMembers < 1 || maxMembers > 500)
                return CommandResult.Fail("max_members_per_assembly must be between 1 and the hard maximum of 500.");

            var assemblies = new List<ListAssemblyInfo>();
            foreach (AssemblyInstance assembly in new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)))
                assemblies.Add(BuildAssemblyInfo(doc, assembly, includeMembers, maxMembers));

            var orderedAssemblies = assemblies
                .OrderBy(a => a.Name)
                .ThenBy(a => a.AssemblyId)
                .ToArray();
            var page = ResponsePaging.Slice(orderedAssemblies, paging.StartIndex, paging.MaxResults);

            return CommandResult.Ok(new
            {
                count = page.TotalCount,
                start_index = page.StartIndex,
                returned_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_index = page.NextIndex,
                includeMembers,
                max_members_per_assembly = maxMembers,
                assemblies = page.Items
            });
        }

        private static ListAssemblyInfo BuildAssemblyInfo(Document doc, AssemblyInstance assembly, bool includeMembers, int maxMembers)
        {
            var typeId = ToValidId(assembly.GetTypeId());
            var typeElement = typeId.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(typeId.Value))
                : null;
            var namingCategoryId = ToValidId(assembly.NamingCategoryId);
            var namingCategory = namingCategoryId.HasValue
                ? Category.GetCategory(doc, RevitCompat.ToElementId(namingCategoryId.Value))
                : null;
            var ownerViewId = ToValidId(assembly.OwnerViewId);
            var ownerView = ownerViewId.HasValue
                ? doc.GetElement(assembly.OwnerViewId)
                : null;
            var memberIds = assembly.GetMemberIds();

            var info = new ListAssemblyInfo
            {
                AssemblyId = RevitCompat.GetId(assembly.Id),
                Name = assembly.Name,
                TypeId = typeId,
                TypeName = string.IsNullOrEmpty(assembly.AssemblyTypeName)
                    ? typeElement?.Name
                    : assembly.AssemblyTypeName,
                Category = assembly.Category?.Name,
                CategoryId = RevitCompat.GetIdOrNull(assembly.Category?.Id),
                NamingCategoryId = namingCategoryId,
                NamingCategory = namingCategory?.Name,
                MemberCount = memberIds.Count,
                OwnerViewId = ownerViewId,
                OwnerViewName = ownerView?.Name
            };

            if (includeMembers)
            {
                info.MemberIds = memberIds
                    .Select(RevitCompat.GetId)
                    .OrderBy(id => id)
                    .Take(maxMembers)
                    .ToArray();
                info.MembersTruncated = memberIds.Count > info.MemberIds.Length;
            }

            return info;
        }

        private static long? ToValidId(ElementId id)
        {
            var value = RevitCompat.GetIdOrNull(id);
            return value.HasValue && value.Value > 0
                ? value
                : null;
        }

        private class ListAssemblyInfo
        {
            [JsonProperty("assemblyId")]
            public long AssemblyId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("typeId")]
            public long? TypeId { get; set; }

            [JsonProperty("typeName")]
            public string TypeName { get; set; }

            [JsonProperty("category")]
            public string Category { get; set; }

            [JsonProperty("categoryId")]
            public long? CategoryId { get; set; }

            [JsonProperty("namingCategoryId")]
            public long? NamingCategoryId { get; set; }

            [JsonProperty("namingCategory")]
            public string NamingCategory { get; set; }

            [JsonProperty("memberCount")]
            public int MemberCount { get; set; }

            [JsonProperty("ownerViewId")]
            public long? OwnerViewId { get; set; }

            [JsonProperty("ownerViewName")]
            public string OwnerViewName { get; set; }

            [JsonProperty("memberIds", NullValueHandling = NullValueHandling.Ignore)]
            public long[] MemberIds { get; set; }

            [JsonProperty("membersTruncated", NullValueHandling = NullValueHandling.Ignore)]
            public bool? MembersTruncated { get; set; }
        }
    }
}
