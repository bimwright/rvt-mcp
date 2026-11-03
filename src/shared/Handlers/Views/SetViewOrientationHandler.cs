using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewOrientationHandler : IRevitCommand
    {
        public string Name => "set_view_orientation";
        public string Description => "Orient a 3D view's camera from eye/target/up points in mm, optionally toggling to perspective, then fit";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""view_name"":{""type"":""string""},""eye"":{""type"":""array"",""items"":{""type"":""number""}},""target"":{""type"":""array"",""items"":{""type"":""number""}},""up"":{""type"":""array"",""items"":{""type"":""number""}},""perspective"":{""type"":""boolean""},""fit"":{""type"":""boolean""}},""required"":[""eye"",""target""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            var request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson);
            var resolved = ViewPresentationSupport.ResolveView(uidoc, request, true, out var error);
            if (resolved == null) return CommandResult.Fail(error);
            var view = resolved as View3D;
            if (view == null) return CommandResult.Fail("View '" + resolved.Name + "' is not a 3D view.");
            if (view.IsLocked) return CommandResult.Fail("View '" + view.Name + "' has a locked orientation; unlock it in Revit first.");

            var eye = ViewPresentationSupport.ReadPointMm(request, "eye", out error);
            if (error != null) return CommandResult.Fail(error);
            var target = ViewPresentationSupport.ReadPointMm(request, "target", out error);
            if (error != null) return CommandResult.Fail(error);
            var up = ViewPresentationSupport.ReadUp(request, out error);
            if (error != null) return CommandResult.Fail(error);
            var orientation = ViewPresentationSupport.MakeOrientation(eye, target, up, out error);
            if (orientation == null) return CommandResult.Fail(error);

            var perspective = request.Value<bool?>("perspective");
            var fit = request.Value<bool?>("fit") ?? true;
            var notes = new List<string>();

            using (var tx = new Transaction(doc, "MCP: Set view orientation"))
            {
                tx.Start();
                try
                {
                    if (perspective == true && !view.IsPerspective)
                    {
                        if (!view.CanToggleBetweenPerspectiveAndIsometric())
                        {
                            tx.RollBack();
                            return CommandResult.Fail("View '" + view.Name + "' cannot be toggled to perspective; use revit_create_camera_view instead.");
                        }
                        view.ToggleToPerspective();
                    }
                    else if (perspective == false && view.IsPerspective)
                    {
                        if (view.CanToggleBetweenPerspectiveAndIsometric()) view.ToggleToIsometric();
                        else notes.Add("View stays perspective (cannot toggle to isometric).");
                    }

                    view.SetOrientation(orientation);
                    if (fit && !view.IsPerspective && view.CropBoxActive)
                    {
                        // A crop box sized for the earlier orientation would clip the new framing.
                        view.CropBoxActive = false;
                        notes.Add("Crop box turned off so the whole model is framed.");
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; the orientation was not changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to orient '" + view.Name + "': " + ex.Message + " Nothing was changed.");
                }
            }

            var zoomed = fit && !view.IsPerspective && ViewPresentationSupport.TryZoomToFit(uidoc, view);
            if (fit && !view.IsPerspective && !zoomed)
                notes.Add("View is not open in the UI so ZoomToFit was skipped; image export of an uncropped 3D view still fits the model.");
            return CommandResult.Ok(new { view = ViewPresentationSupport.Describe(view), zoomed_to_fit = zoomed, notes });
        }
    }
}
