using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin.Handlers
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
                var deleted = 0;
                var failed = 0;

                foreach (var id in elementIds)
                {
                    try
                    {
                        var elId = RevitCompat.ToElementId(id);
                        if (doc.GetElement(elId) != null)
                        {
                            doc.Delete(elId);
                            deleted++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                }

                tx.Commit();
                return CommandResult.Ok(new { deleted, failed, total = elementIds.Length });
            }
        }
    }
}
