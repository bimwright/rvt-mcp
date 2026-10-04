using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetStructuralLoadHandler : IRevitCommand
    {
        public string Name => "set_structural_load";
        public string Description => "Update force/moment components of an existing structural load. action='update' supported; action='create' returns not_implemented (deferred).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""create"",""update""]},""load_id"":{""type"":""integer""},""force_x"":{""type"":""number""},""force_y"":{""type"":""number""},""force_z"":{""type"":""number""},""moment_x"":{""type"":""number""},""moment_y"":{""type"":""number""},""moment_z"":{""type"":""number""}},""required"":[""action""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var action = (req.Value<string>("action") ?? "").ToLowerInvariant();

            if (action == "create")
            {
                return CommandResult.Ok(new
                {
                    status = "not_implemented",
                    reason = "Load creation deferred to a future wave; spike PointLoad/LineLoad/AreaLoad.Create across R22-R27 first."
                });
            }

            if (action != "update")
                return CommandResult.Fail("action must be 'update' (create is deferred).");

            var loadId = req.Value<long?>("load_id");
            if (!loadId.HasValue) return CommandResult.Fail("load_id is required for update.");

            var load = doc.GetElement(RevitCompat.ToElementId(loadId.Value));
            if (load == null) return CommandResult.Fail($"load_id {loadId} not found.");

            using (var tx = new Transaction(doc, "RvtMcp: Update structural load"))
            {
                tx.Start();
                try
                {
                    var changed = 0;
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_FORCE_FX, req, "force_x");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_FORCE_FY, req, "force_y");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_FORCE_FZ, req, "force_z");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_MOMENT_MX, req, "moment_x");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_MOMENT_MY, req, "moment_y");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_MOMENT_MZ, req, "moment_z");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        load_id = loadId.Value,
                        changed_fields = changed,
                        status = changed == 0 ? "no_changes" : "updated"
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to update load: {ex.Message}");
                }
            }
        }

        private static int SetIfPresent(Element element, BuiltInParameter builtInParameter, JObject request, string key)
        {
            var value = request.Value<double?>(key);
            if (!value.HasValue) return 0;

            var parameter = element.get_Parameter(builtInParameter);
            if (parameter == null || parameter.IsReadOnly) return 0;

            parameter.Set(value.Value);
            return 1;
        }
    }
}
