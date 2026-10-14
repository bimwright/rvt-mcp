using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ShowElementInViewHandler : IRevitCommand
    {
        public string Name => "show_element_in_view";
        public string Description => "Activate view (optional), set element selection (optional), and zoom/show elements. UI-only, no document Transaction.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""view_id"":{""type"":""integer""},""activate_view"":{""type"":""boolean"",""default"":true},""select"":{""type"":""boolean"",""default"":true},""zoom"":{""type"":""boolean"",""default"":true}},""required"":[""element_ids""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var idsToken = req["element_ids"] as JArray;
            if (idsToken == null || !idsToken.Any())
                return CommandResult.Fail("element_ids is required and must be non-empty.");

            var viewIdParam = req.Value<long?>("view_id");
            var doActivate = req.Value<bool?>("activate_view") ?? true;
            var doSelect = req.Value<bool?>("select") ?? true;
            var doZoom = req.Value<bool?>("zoom") ?? true;

            var ids = idsToken
                .Select(t => RevitCompat.ToElementId(t.Value<long>()))
                .Where(id => doc.GetElement(id) != null)
                .ToList();
            if (!ids.Any()) return CommandResult.Fail("No valid element ids resolved.");

            try
            {
                if (viewIdParam.HasValue && doActivate)
                {
                    var view = doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View;
                    if (view != null && !view.IsTemplate) uidoc.ActiveView = view;
                }

                if (doSelect) uidoc.Selection.SetElementIds(ids);
                if (doZoom) uidoc.ShowElements(ids);

                return CommandResult.Ok(new
                {
                    element_count = ids.Count,
                    activated_view_id = uidoc.ActiveView != null ? (long?)RevitCompat.GetId(uidoc.ActiveView.Id) : null,
                    selected = doSelect,
                    zoomed = doZoom
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to show elements: {ex.Message}");
            }
        }
    }
}
