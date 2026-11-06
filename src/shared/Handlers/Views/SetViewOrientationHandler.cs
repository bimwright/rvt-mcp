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
            // Revit keeps no saved orientation for the default 3D view ({3D}, or {3D - user} when workshared).
            // It is a viewing view, so only its temporary camera moves, like an orbit, and Undo has nothing to restore.
            var saveOrientation = view.CanSaveOrientation();

            var eye = ViewPresentationSupport.ReadPointMm(request, "eye", out error);
            if (error != null) return CommandResult.Fail(error);
            var target = ViewPresentationSupport.ReadPointMm(request, "target", out error);
            if (error != null) return CommandResult.Fail(error);
            var up = ViewPresentationSupport.ReadUp(request, out error);
            if (error != null) return CommandResult.Fail(error);
            var orientation = ViewPresentationSupport.MakeOrientation(eye, target, up, out error);
            if (orientation == null) return CommandResult.Fail(error);

            var perspective = request.Value<bool?>("perspective");
            var fitArg = request.Value<bool?>("fit");
            var fit = fitArg ?? true;
            // A named view may be placed on a sheet, where turning its crop box off resizes the viewport.
            // Without an explicit fit, only the default 3D view, a viewing view, loses its crop box.
            var cropOff = fitArg ?? !saveOrientation;
            var notes = new List<string>();
            var before = view.GetOrientation();

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

                    if (saveOrientation)
                    {
                        view.SetOrientation(orientation);
                        view.SaveOrientation();
                    }
                    if (!view.IsPerspective && view.CropBoxActive)
                    {
                        if (cropOff)
                        {
                            // A crop box sized for the earlier orientation would clip the new framing.
                            view.CropBoxActive = false;
                            notes.Add("Crop box turned off so the whole model is framed.");
                        }
                        else if (fitArg == null)
                            notes.Add("Crop box kept on this named view; pass fit=true to turn it off and frame the whole model.");
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                    {
                        if (saveOrientation) ViewPresentationSupport.RestoreCamera(view, before);
                        return CommandResult.Fail("Revit did not commit the transaction; the orientation was not changed.");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    if (saveOrientation) ViewPresentationSupport.RestoreCamera(view, before);
                    return CommandResult.Fail("Failed to orient '" + view.Name + "': " + ex.Message + " Nothing was changed.");
                }
            }

            if (saveOrientation) SavedViewOrientationRefresh.Track(app, view);
            else
            {
                // Moved only after the commit, so a rolled-back toggle or crop change never leaves it aimed.
                view.SetOrientation(orientation);
                notes.Add("Default 3D view: only the temporary camera moved. It is not saved in the model and Revit Undo does not restore it; use a named 3D view or revit_create_camera_view to keep a camera.");
            }
            var zoomed = fit && !view.IsPerspective && ViewPresentationSupport.TryZoomToFit(uidoc, view);
            if (fit && !view.IsPerspective && !zoomed)
                notes.Add("View is not open in the UI so ZoomToFit was skipped; image export of an uncropped 3D view still fits the model.");
            return CommandResult.Ok(new { view = ViewPresentationSupport.Describe(view), orientation_saved = saveOrientation, zoomed_to_fit = zoomed, notes });
        }
    }
}
