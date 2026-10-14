using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetGroupMembersHandler : IRevitCommand
    {
        public string Name => "get_group_members";
        public string Description => "Get a Revit group's metadata and a bounded page of member elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""groupId"":{""type"":""integer"",""description"":""Element id of the Revit group to inspect.""},""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""max_results"":{""type"":""integer"",""default"":200,""minimum"":1,""maximum"":1000}},""required"":[""groupId""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long groupId;
            ResponsePaging.Options paging;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                var parsedGroupId = request.Value<long?>("groupId");
                if (!parsedGroupId.HasValue)
                    return CommandResult.Fail("groupId integer is required.");
                if (!ResponsePaging.TryParse(request, "start_index", "max_results", 200, 1000, out paging, out var pagingError))
                    return CommandResult.Fail(pagingError);

                groupId = parsedGroupId.Value;
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            Element element;
            try
            {
                element = doc.GetElement(RevitCompat.ToElementId(groupId));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Invalid groupId {groupId}: {ex.Message}");
            }

            if (element == null)
                return CommandResult.Fail($"Element {groupId} was not found; expected an Autodesk.Revit.DB.Group.");

            var group = element as Group;
            if (group == null)
                return CommandResult.Fail($"Element {groupId} is a {element.GetType().Name}, not an Autodesk.Revit.DB.Group.");

            var memberIds = group.GetMemberIds()
                .OrderBy(RevitCompat.GetId)
                .ToArray();
            var page = ResponsePaging.Slice(memberIds, paging.StartIndex, paging.MaxResults);
            var members = new List<object>();

            foreach (var memberId in page.Items)
            {
                var member = doc.GetElement(memberId);
                members.Add(BuildMemberInfo(doc, memberId, member));
            }

            return CommandResult.Ok(new
            {
                group = BuildGroupInfo(doc, group, memberIds.Length),
                memberCount = page.TotalCount,
                start_index = page.StartIndex,
                returned_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_index = page.NextIndex,
                members
            });
        }

        private static object BuildGroupInfo(Document doc, Group group, int memberCount)
        {
            var groupType = group.GroupType ?? doc.GetElement(group.GetTypeId()) as GroupType;
            var categoryId = RevitCompat.GetIdOrNull(group.Category?.Id);
            var ownerViewId = ToValidId(group.OwnerViewId);
            var ownerView = ownerViewId.HasValue ? GetElementById(doc, ownerViewId.Value) : null;

            return new
            {
                groupId = RevitCompat.GetId(group.Id),
                name = Safe(() => group.Name),
                category = Safe(() => group.Category?.Name),
                categoryId,
                groupTypeId = ToValidId(groupType?.Id),
                groupTypeName = Safe(() => groupType?.Name),
                isAttached = IsAttachedDetailGroup(group, categoryId),
                attachedParentId = ToValidId(group.AttachedParentId),
                ownerViewId,
                ownerViewName = GetElementName(ownerView),
                isPinned = SafeBool(() => group.Pinned),
                memberCount
            };
        }

        private static object BuildMemberInfo(Document doc, ElementId memberId, Element member)
        {
            var typeId = member == null ? null : ToValidId(SafeElementId(() => member.GetTypeId()));
            var typeElement = typeId.HasValue ? GetElementById(doc, typeId.Value) : null;
            var ownerViewId = member == null ? null : ToValidId(SafeElementId(() => member.OwnerViewId));
            var ownerView = ownerViewId.HasValue ? GetElementById(doc, ownerViewId.Value) : null;

            return new
            {
                elementId = RevitCompat.GetId(memberId),
                name = GetElementName(member),
                category = Safe(() => member?.Category?.Name),
                typeId,
                typeName = GetElementName(typeElement),
                ownerViewId,
                ownerViewName = GetElementName(ownerView),
                isPinned = member == null ? null : SafeBool(() => member.Pinned)
            };
        }

        private static bool IsAttachedDetailGroup(Group group, long? categoryId)
        {
            return group.IsAttached
                   || categoryId == (long)BuiltInCategory.OST_IOSAttachedDetailGroups;
        }

        private static Element GetElementById(Document doc, long id)
        {
            try
            {
                return doc.GetElement(RevitCompat.ToElementId(id));
            }
            catch
            {
                return null;
            }
        }

        private static long? ToValidId(ElementId id)
        {
            var value = RevitCompat.GetIdOrNull(id);
            return value.HasValue && value.Value > 0
                ? value
                : null;
        }

        private static ElementId SafeElementId(Func<ElementId> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
        }

        private static bool? SafeBool(Func<bool> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
        }

        private static string GetElementName(Element element)
        {
            if (element == null)
                return null;

            return Safe(() => element.Name);
        }

        private static T Safe<T>(Func<T> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return default(T);
            }
        }
    }
}
