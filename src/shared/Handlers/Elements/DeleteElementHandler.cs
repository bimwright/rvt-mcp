using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DeleteElementHandler : IRevitCommand
    {
        public string Name => "delete_element";
        public string Description => "Delete elements by ID";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}}},""required"":[""elementIds""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var elementIds = request["elementIds"]?.ToObject<long[]>() ?? new long[0];

            if (elementIds.Length == 0)
                return CommandResult.Fail("elementIds array is required.");

            using (var tx = new Transaction(doc, "MCP: Delete elements"))
            {
                tx.Start();
                var deletedIds = new List<long>();
                var failedIds = new List<long>();
                var errors = new List<string>();

                foreach (var id in elementIds)
                {
                    try
                    {
                        var elId = RevitCompat.ToElementId(id);
                        if (doc.GetElement(elId) == null)
                        {
                            failedIds.Add(id);
                            errors.Add($"{id}: element not found");
                            continue;
                        }
                        doc.Delete(elId);
                        deletedIds.Add(id);
                    }
                    catch (Exception ex)
                    {
                        failedIds.Add(id);
                        errors.Add($"{id}: {ex.Message}");
                    }
                }

                if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                return CommandResult.Ok(new
                {
                    deleted = deletedIds.Count,
                    failed = failedIds.Count,
                    total = elementIds.Length,
                    deletedIds,
                    failedIds,
                    errors
                });
            }
        }
    }
}
