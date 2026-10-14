using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListWorksetsHandler : IRevitCommand
    {
        public string Name => "list_worksets";
        public string Description => "List worksets in the active Revit document, optionally including non-type element counts.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""includeElementCounts"":{""type"":""boolean"",""default"":false,""description"":""Include non-type element counts per workset.""}}}";

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

            var includeElementCounts = request.Value<bool?>("includeElementCounts") ?? false;
            var isWorkshared = doc.IsWorkshared;

            if (!isWorkshared)
            {
                return CommandResult.Ok(new
                {
                    isWorkshared = false,
                    activeWorksetId = (long?)null,
                    activeWorksetName = (string)null,
                    includeElementCounts,
                    count = 0,
                    worksets = new WorksetInfo[0]
                });
            }

            var worksetTable = doc.GetWorksetTable();
            var activeWorksetId = GetActiveWorksetId(worksetTable);
            var activeWorksetName = GetActiveWorksetName(worksetTable, activeWorksetId);
            var ownerByWorksetId = GetOwnerByWorksetId(doc);

            var worksets = new FilteredWorksetCollector(doc)
                .ToWorksets()
                .Select(workset => BuildWorksetInfo(doc, workset, ownerByWorksetId, includeElementCounts))
                .OrderBy(workset => workset.Kind)
                .ThenBy(workset => workset.Name)
                .ThenBy(workset => workset.Id)
                .ToArray();

            return CommandResult.Ok(new
            {
                isWorkshared = true,
                activeWorksetId,
                activeWorksetName,
                includeElementCounts,
                count = worksets.Length,
                worksets
            });
        }

        private static WorksetInfo BuildWorksetInfo(
            Document doc,
            Workset workset,
            IDictionary<long, string> ownerByWorksetId,
            bool includeElementCounts)
        {
            var id = ToValidWorksetId(workset.Id);
            ownerByWorksetId.TryGetValue(id, out var owner);

            return new WorksetInfo
            {
                Id = id,
                Name = workset.Name,
                Kind = workset.Kind.ToString(),
                IsOpen = workset.IsOpen,
                IsEditable = workset.IsEditable,
                Owner = string.IsNullOrWhiteSpace(owner) ? null : owner,
                ElementCount = includeElementCounts ? CountElementsOnWorkset(doc, workset.Id) : (int?)null
            };
        }

        private static long? GetActiveWorksetId(WorksetTable worksetTable)
        {
            try
            {
                var activeId = worksetTable?.GetActiveWorksetId();
                return activeId == null ? (long?)null : ToValidWorksetId(activeId);
            }
            catch
            {
                return null;
            }
        }

        private static string GetActiveWorksetName(WorksetTable worksetTable, long? activeWorksetId)
        {
            if (!activeWorksetId.HasValue)
                return null;

            try
            {
                return worksetTable?.GetWorkset(new WorksetId((int)activeWorksetId.Value))?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static IDictionary<long, string> GetOwnerByWorksetId(Document doc)
        {
            var owners = new Dictionary<long, string>();

            try
            {
                var centralPath = doc.GetWorksharingCentralModelPath();
                if (centralPath == null)
                    return owners;

                foreach (WorksetPreview preview in WorksharingUtils.GetUserWorksetInfo(centralPath))
                {
                    var id = ToValidWorksetId(preview.Id);
                    if (id <= 0 || string.IsNullOrWhiteSpace(preview.Owner))
                        continue;

                    owners[id] = preview.Owner;
                }
            }
            catch
            {
                return owners;
            }

            return owners;
        }

        private static int CountElementsOnWorkset(Document doc, WorksetId worksetId)
        {
            var provider = new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM));
            var rule = new FilterIntegerRule(provider, new FilterNumericEquals(), worksetId.IntegerValue);
            var filter = new ElementParameterFilter(rule);

            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .GetElementCount();
        }

        private static long ToValidWorksetId(WorksetId worksetId)
        {
            if (worksetId == null || worksetId.IntegerValue <= 0)
                return 0;

            return worksetId.IntegerValue;
        }

        private class WorksetInfo
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("kind")]
            public string Kind { get; set; }

            [JsonProperty("isOpen")]
            public bool IsOpen { get; set; }

            [JsonProperty("isEditable")]
            public bool IsEditable { get; set; }

            [JsonProperty("owner", NullValueHandling = NullValueHandling.Ignore)]
            public string Owner { get; set; }

            [JsonProperty("elementCount", NullValueHandling = NullValueHandling.Ignore)]
            public int? ElementCount { get; set; }
        }
    }
}
