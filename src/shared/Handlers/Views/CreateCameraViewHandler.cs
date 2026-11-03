using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateCameraViewHandler : IRevitCommand
    {
        public string Name => "create_camera_view";
        public string Description => "Create (or re-aim, if the name exists) a perspective 3D camera view from eye/target points in mm";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""eye"":{""type"":""array"",""items"":{""type"":""number""}},""target"":{""type"":""array"",""items"":{""type"":""number""}},""up"":{""type"":""array"",""items"":{""type"":""number""}},""far_clip"":{""type"":""boolean"",""description"":""Keep far clipping (default false = no far clip)""}},""required"":[""name"",""eye"",""target""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            var request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson);
            var name = request.Value<string>("name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return CommandResult.Fail("name is required.");

            var eye = ViewPresentationSupport.ReadPointMm(request, "eye", out var error);
            if (error != null) return CommandResult.Fail(error);
            var target = ViewPresentationSupport.ReadPointMm(request, "target", out error);
            if (error != null) return CommandResult.Fail(error);
            var up = ViewPresentationSupport.ReadUp(request, out error);
            if (error != null) return CommandResult.Fail(error);
            var orientation = ViewPresentationSupport.MakeOrientation(eye, target, up, out error);
            if (orientation == null) return CommandResult.Fail(error);
            var keepFarClip = request.Value<bool?>("far_clip") ?? false;

            // Case-insensitive, like view lookup elsewhere, so "entrance camera" re-aims "Entrance Camera".
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            var view = existing as View3D;
            if (existing != null && (view == null || !view.IsPerspective))
                return CommandResult.Fail("A view named '" + existing.Name + "' exists and is not a perspective 3D view; pick another name.");
            var created = view == null;

            using (var tx = new Transaction(doc, "MCP: Create camera view"))
            {
                tx.Start();
                try
                {
                    if (view == null)
                    {
                        var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                        if (viewType == null)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("No 3D view family type in this project.");
                        }
                        view = View3D.CreatePerspective(doc, viewType.Id);
                        view.Name = name;
                    }

                    if (view.IsLocked)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("View '" + view.Name + "' is locked.");
                    }

                    view.SetOrientation(orientation);
                    if (!keepFarClip)
                    {
                        var farClip = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                        if (farClip != null && !farClip.IsReadOnly) farClip.Set(0);
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; the camera view was not created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create camera view '" + name + "': " + ex.Message + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new { created, view = ViewPresentationSupport.Describe(view) });
        }
    }
}
