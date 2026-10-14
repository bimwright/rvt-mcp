using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ActivateViewHandler : IRevitCommand
    {
        public string Name => "activate_view";
        public string Description => "Set UIDocument.ActiveView. UI-only operation, no document Transaction. Resolves view by view_id or view_name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""view_name"":{""type"":""string""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var viewName = req.Value<string>("view_name");

            View view = null;
            if (viewIdParam.HasValue)
            {
                view = doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View;
            }
            else if (!string.IsNullOrWhiteSpace(viewName))
            {
                view = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));
            }

            if (view == null) return CommandResult.Fail("Could not resolve view (provide view_id or view_name).");
            if (view.IsTemplate) return CommandResult.Fail("Cannot activate a view template.");

            try
            {
                uidoc.ActiveView = view;
                return CommandResult.Ok(new
                {
                    activated_view_id = RevitCompat.GetId(view.Id),
                    view_name = view.Name,
                    view_type = view.ViewType.ToString()
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to activate view: {ex.Message}");
            }
        }
    }
}
