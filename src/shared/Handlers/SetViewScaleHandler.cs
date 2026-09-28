using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewScaleHandler : IRevitCommand
    {
        public string Name => "set_view_scale";
        public string Description => "Set the graphical scale of a view. scale is the denominator (e.g., 50 for 1:50).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""scale"":{""type"":""integer""}},""required"":[""scale""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var scale = req.Value<int?>("scale");
            if (!scale.HasValue || scale.Value <= 0) return CommandResult.Fail("scale must be a positive integer.");

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot modify a view template.");

            using (var tx = new Transaction(doc, "RvtMcp: Set view scale"))
            {
                tx.Start();
                try
                {
                    // VIEW_SCALE reports IsReadOnly on regular plan views even though the
                    // scale is freely changeable — the supported write path is View.Scale.
                    var previous = view.Scale;
                    view.Scale = scale.Value;
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction commit status: {status}");

                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(view.Id),
                        previous_scale = previous,
                        new_scale = scale.Value
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set view scale: {ex.Message}");
                }
            }
        }
    }
}
