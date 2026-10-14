using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateGroupFromElementsHandler : IRevitCommand
    {
        public string Name => "create_group_from_elements";
        public string Description => "Create a Revit group from existing elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""name"":{""type"":""string""}},""required"":[""elementIds""]}";

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
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            var idsToken = request["elementIds"];
            if (idsToken == null || idsToken.Type != JTokenType.Array)
                return CommandResult.Fail("elementIds integer array is required.");

            var parseResult = ParseElementIds((JArray)idsToken);
            if (!string.IsNullOrEmpty(parseResult.Error))
                return CommandResult.Fail(parseResult.Error);

            var missingIds = new List<long>();
            var elementIds = new List<ElementId>();

            foreach (var id in parseResult.ElementIds)
            {
                var elementId = RevitCompat.ToElementId(id);
                if (doc.GetElement(elementId) == null)
                {
                    missingIds.Add(id);
                    continue;
                }

                elementIds.Add(elementId);
            }

            if (missingIds.Count > 0)
                return CommandResult.Fail("elementIds contains ids that do not exist: " + string.Join(", ", missingIds));

            if (elementIds.Count < 2)
                return CommandResult.Fail("At least 2 distinct existing elements are required to create a group.");

            var requestedName = request.Value<string>("name");
            if (requestedName != null)
                requestedName = requestedName.Trim();

            using (var tx = new Transaction(doc, "MCP: Create group from elements"))
            {
                tx.Start();
                try
                {
                    var group = doc.Create.NewGroup(elementIds);
                    var groupType = group.GroupType ?? doc.GetElement(group.GetTypeId()) as GroupType;

                    if (!string.IsNullOrEmpty(requestedName))
                    {
                        if (groupType == null)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("Created group has no group type; cannot set name.");
                        }

                        groupType.Name = requestedName;
                    }

                    var memberIds = group.GetMemberIds().Select(RevitCompat.GetId).ToArray();
                    var groupName = Safe(() => group.Name);
                    var groupTypeName = Safe(() => groupType?.Name);
                    var groupTypeId = ToValidId(groupType?.Id);
                    var groupId = RevitCompat.GetId(group.Id);

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit group creation. Transaction status: " + status);

                    return CommandResult.Ok(new
                    {
                        groupId,
                        groupName,
                        groupTypeId,
                        groupTypeName,
                        memberCount = memberIds.Length,
                        memberIds
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted())
                        tx.RollBack();

                    return CommandResult.Fail($"Failed to create group from elements: {ex.Message}");
                }
            }
        }

        private static ParseElementIdsResult ParseElementIds(JArray idsToken)
        {
            var ids = new List<long>();
            var invalidIds = new List<string>();
            var seen = new HashSet<long>();

            foreach (var token in idsToken)
            {
                if (token.Type != JTokenType.Integer)
                {
                    invalidIds.Add(token.ToString(Formatting.None));
                    continue;
                }

                long id;
                try
                {
                    id = token.Value<long>();
                }
                catch
                {
                    invalidIds.Add(token.ToString(Formatting.None));
                    continue;
                }

                if (!IsElementIdValueValid(id))
                {
                    invalidIds.Add(id.ToString());
                    continue;
                }

                if (seen.Add(id))
                    ids.Add(id);
            }

            if (invalidIds.Count > 0)
                return ParseElementIdsResult.Fail("elementIds contains invalid ids: " + string.Join(", ", invalidIds));

            if (ids.Count < 2)
                return ParseElementIdsResult.Fail("At least 2 distinct elementIds are required.");

            return ParseElementIdsResult.Ok(ids);
        }

        private static bool IsElementIdValueValid(long id)
        {
            if (id <= 0)
                return false;

#if !REVIT2024_OR_GREATER
            if (id > int.MaxValue)
                return false;
#endif

            return true;
        }

        private static long? ToValidId(ElementId id)
        {
            var value = RevitCompat.GetIdOrNull(id);
            return value.HasValue && value.Value > 0
                ? value
                : null;
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

        private class ParseElementIdsResult
        {
            public IList<long> ElementIds { get; private set; }
            public string Error { get; private set; }

            public static ParseElementIdsResult Ok(IList<long> elementIds)
            {
                return new ParseElementIdsResult { ElementIds = elementIds };
            }

            public static ParseElementIdsResult Fail(string error)
            {
                return new ParseElementIdsResult
                {
                    ElementIds = new long[0],
                    Error = error
                };
            }
        }
    }
}
