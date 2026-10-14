using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListGroupsHandler : IRevitCommand
    {
        private const string GroupKindAll = "all";
        private const string GroupKindModel = "model";
        private const string GroupKindDetail = "detail";
        private const string GroupKindAttached = "attached";

        public string Name => "list_groups";
        public string Description => "List Revit model, detail, and attached detail groups in the active document.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""groupKind"":{""type"":""string"",""enum"":[""all"",""model"",""detail"",""attached""],""default"":""all"",""description"":""Group kind to list: all, model, detail, or attached.""},""includeMembers"":{""type"":""boolean"",""default"":false,""description"":""Include member element IDs for each group.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson)
                ? new JObject()
                : JObject.Parse(paramsJson);

            var groupKind = (request.Value<string>("groupKind") ?? GroupKindAll).Trim().ToLowerInvariant();
            if (!IsValidGroupKind(groupKind))
                return CommandResult.Fail("groupKind must be one of: all, model, detail, attached.");

            var includeMembers = request.Value<bool?>("includeMembers") ?? false;
            var groups = new List<ListGroupInfo>();

            foreach (Group group in new FilteredElementCollector(doc).OfClass(typeof(Group)))
            {
                if (!MatchesGroupKind(group, groupKind))
                    continue;

                groups.Add(BuildGroupInfo(doc, group, includeMembers));
            }

            var orderedGroups = groups
                .OrderBy(g => g.Category)
                .ThenBy(g => g.Name)
                .ThenBy(g => g.GroupId)
                .ToArray();

            return CommandResult.Ok(new
            {
                count = orderedGroups.Length,
                groupKind,
                includeMembers,
                groups = orderedGroups
            });
        }

        private static bool IsValidGroupKind(string groupKind)
        {
            return groupKind == GroupKindAll
                   || groupKind == GroupKindModel
                   || groupKind == GroupKindDetail
                   || groupKind == GroupKindAttached;
        }

        private static bool MatchesGroupKind(Group group, string groupKind)
        {
            if (groupKind == GroupKindAll)
                return true;

            var categoryId = RevitCompat.GetIdOrNull(group.Category?.Id);
            var isAttached = IsAttachedDetailGroup(group, categoryId);

            if (groupKind == GroupKindAttached)
                return isAttached;

            if (groupKind == GroupKindModel)
                return categoryId == (long)BuiltInCategory.OST_IOSModelGroups;

            return !isAttached && categoryId == (long)BuiltInCategory.OST_IOSDetailGroups;
        }

        private static bool IsAttachedDetailGroup(Group group, long? categoryId)
        {
            return group.IsAttached
                   || categoryId == (long)BuiltInCategory.OST_IOSAttachedDetailGroups;
        }

        private static ListGroupInfo BuildGroupInfo(Document doc, Group group, bool includeMembers)
        {
            var groupType = group.GroupType ?? doc.GetElement(group.GetTypeId()) as GroupType;
            var memberIds = group.GetMemberIds();
            var ownerViewId = ToValidId(group.OwnerViewId);
            var ownerViewName = ownerViewId.HasValue
                ? doc.GetElement(group.OwnerViewId)?.Name
                : null;
            var categoryId = RevitCompat.GetIdOrNull(group.Category?.Id);

            var info = new ListGroupInfo
            {
                GroupId = RevitCompat.GetId(group.Id),
                Name = group.Name,
                GroupTypeId = ToValidId(groupType?.Id),
                GroupTypeName = groupType?.Name,
                IsAttached = IsAttachedDetailGroup(group, categoryId),
                AttachedParentId = ToValidId(group.AttachedParentId),
                MemberCount = memberIds.Count,
                OwnerViewId = ownerViewId,
                OwnerViewName = ownerViewName,
                Category = group.Category?.Name
            };

            if (includeMembers)
                info.MemberIds = memberIds.Select(RevitCompat.GetId).ToArray();

            return info;
        }

        private static long? ToValidId(ElementId id)
        {
            var value = RevitCompat.GetIdOrNull(id);
            return value.HasValue && value.Value > 0
                ? value
                : null;
        }

        private class ListGroupInfo
        {
            [JsonProperty("groupId")]
            public long GroupId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("groupTypeId")]
            public long? GroupTypeId { get; set; }

            [JsonProperty("groupTypeName")]
            public string GroupTypeName { get; set; }

            [JsonProperty("isAttached")]
            public bool IsAttached { get; set; }

            [JsonProperty("attachedParentId")]
            public long? AttachedParentId { get; set; }

            [JsonProperty("memberCount")]
            public int MemberCount { get; set; }

            [JsonProperty("ownerViewId")]
            public long? OwnerViewId { get; set; }

            [JsonProperty("ownerViewName")]
            public string OwnerViewName { get; set; }

            [JsonProperty("category")]
            public string Category { get; set; }

            [JsonProperty("memberIds", NullValueHandling = NullValueHandling.Ignore)]
            public long[] MemberIds { get; set; }
        }
    }
}
