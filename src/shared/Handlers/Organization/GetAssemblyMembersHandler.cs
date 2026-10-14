using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetAssemblyMembersHandler : IRevitCommand
    {
        public string Name => "get_assembly_members";
        public string Description => "Get metadata and a bounded page of member elements for a Revit assembly instance.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""assemblyId"":{""type"":""integer"",""description"":""Element ID of the assembly instance.""},""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""max_results"":{""type"":""integer"",""default"":200,""minimum"":1,""maximum"":1000}},""required"":[""assemblyId""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long assemblyId;
            ResponsePaging.Options paging;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                var token = request["assemblyId"];
                if (token == null || token.Type == JTokenType.Null)
                    return CommandResult.Fail("assemblyId integer is required.");
                if (token.Type != JTokenType.Integer)
                    return CommandResult.Fail("assemblyId must be an integer.");

                assemblyId = token.Value<long>();
                if (!ResponsePaging.TryParse(request, "start_index", "max_results", 200, 1000, out paging, out var pagingError))
                    return CommandResult.Fail(pagingError);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }
            catch (FormatException)
            {
                return CommandResult.Fail("assemblyId must be an integer.");
            }
            catch (InvalidCastException)
            {
                return CommandResult.Fail("assemblyId must be an integer.");
            }

            var element = doc.GetElement(RevitCompat.ToElementId(assemblyId));
            if (element == null)
                return CommandResult.Fail($"Assembly element {assemblyId} was not found.");

            var assembly = element as AssemblyInstance;
            if (assembly == null)
            {
                return CommandResult.Fail(
                    $"Element {assemblyId} is not an assembly instance. Actual type: {element.GetType().Name}.");
            }

            var memberIds = (assembly.GetMemberIds() ?? new List<ElementId>())
                .OrderBy(RevitCompat.GetId)
                .ToArray();
            var page = ResponsePaging.Slice(memberIds, paging.StartIndex, paging.MaxResults);
            var members = page.Items
                .Select(id => doc.GetElement(id))
                .Where(member => member != null)
                .Select(member => BuildMemberInfo(doc, member))
                .OrderBy(member => member.Category)
                .ThenBy(member => member.Name)
                .ThenBy(member => member.ElementId)
                .ToArray();

            var typeId = ToValidId(assembly.GetTypeId());
            var typeElement = typeId.HasValue ? doc.GetElement(RevitCompat.ToElementId(typeId.Value)) : null;

            return CommandResult.Ok(new
            {
                assemblyId = RevitCompat.GetId(assembly.Id),
                uniqueId = assembly.UniqueId,
                name = assembly.Name,
                category = assembly.Category?.Name,
                typeId,
                typeName = typeElement?.Name,
                memberCount = page.TotalCount,
                start_index = page.StartIndex,
                returned_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_index = page.NextIndex,
                members
            });
        }

        private static AssemblyMemberInfo BuildMemberInfo(Document doc, Element member)
        {
            var typeId = ToValidId(member.GetTypeId());
            var typeElement = typeId.HasValue ? doc.GetElement(RevitCompat.ToElementId(typeId.Value)) : null;

            return new AssemblyMemberInfo
            {
                ElementId = RevitCompat.GetId(member.Id),
                Name = member.Name,
                Category = member.Category?.Name,
                TypeId = typeId,
                TypeName = typeElement?.Name,
                GroupId = ToValidId(member.GroupId),
                WorksetId = ToValidWorksetId(member)
            };
        }

        private static long? ToValidId(ElementId id)
        {
            var value = RevitCompat.GetIdOrNull(id);
            if (!value.HasValue || value.Value <= 0)
                return null;

            return value;
        }

        private static long? ToValidWorksetId(Element element)
        {
            try
            {
                var worksetId = element.WorksetId;
                if (worksetId == null || worksetId.IntegerValue <= 0)
                    return null;

                return worksetId.IntegerValue;
            }
            catch
            {
                return null;
            }
        }

        private class AssemblyMemberInfo
        {
            [JsonProperty("elementId")]
            public long ElementId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("category")]
            public string Category { get; set; }

            [JsonProperty("typeId")]
            public long? TypeId { get; set; }

            [JsonProperty("typeName")]
            public string TypeName { get; set; }

            [JsonProperty("groupId")]
            public long? GroupId { get; set; }

            [JsonProperty("worksetId")]
            public long? WorksetId { get; set; }
        }
    }
}
