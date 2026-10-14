using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListTitleblocksHandler : IRevitCommand
    {
        public string Name => "list_titleblocks";
        public string Description => "List loaded titleblock family types and count their sheet placements";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""name_pattern"": { ""type"": ""string"" },
    ""include_inactive"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""default"": 1000, ""minimum"": 1, ""maximum"": 10000 }
  }
}";

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
            catch (Exception ex)
            {
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            var namePattern = request.Value<string>("name_pattern") ?? request.Value<string>("namePattern") ?? "";
            var includeInactive = request.Value<bool?>("include_inactive") ?? request.Value<bool?>("includeInactive") ?? true;
            var limit = request.Value<int?>("limit") ?? 1000;

            limit = Math.Max(1, Math.Min(limit, 10000));

            // Get loaded title blocks types
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .ToList();

            // Count title block instances in the document
            var instances = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            var instanceCounts = instances
                .GroupBy(inst => RevitCompat.GetId(inst.GetTypeId()))
                .ToDictionary(g => g.Key, g => g.Count());

            var titleblockDtos = new List<object>();

            foreach (var symbol in symbols)
            {
                try
                {
                    var typeId = RevitCompat.GetId(symbol.Id);
                    var familyId = symbol.Family != null ? RevitCompat.GetId(symbol.Family.Id) : 0;
                    var familyName = symbol.FamilyName ?? "";
                    var typeName = symbol.Name ?? "";
                    var displayName = $"{familyName}: {typeName}";

                    int count = 0;
                    instanceCounts.TryGetValue(typeId, out count);

                    bool isActive = count > 0;

                    if (!includeInactive && !isActive)
                        continue;

                    if (!string.IsNullOrEmpty(namePattern) &&
                        !displayName.Contains(namePattern, StringComparison.OrdinalIgnoreCase) &&
                        !familyName.Contains(namePattern, StringComparison.OrdinalIgnoreCase) &&
                        !typeName.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    titleblockDtos.Add(new
                    {
                        type_id = typeId,
                        family_id = familyId,
                        family_name = familyName,
                        type_name = typeName,
                        display_name = displayName,
                        is_active = isActive,
                        sheet_instance_count = count
                    });
                }
                catch
                {
                    // Skip symbols that throw during introspection
                }
            }

            var total = titleblockDtos.Count;
            var returned = titleblockDtos.Take(limit).ToList();

            return CommandResult.Ok(new
            {
                total = total,
                returned = returned.Count,
                titleblocks = returned
            });
        }
    }
}
