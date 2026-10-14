using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateTextNoteHandler : IRevitCommand
    {
        public string Name => "create_text_note";
        public string Description => "Create a text note in a view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""text"", ""x"", ""y""],
  ""properties"": {
    ""text"": { ""type"": ""string"" },
    ""x"": { ""type"": ""number"" },
    ""y"": { ""type"": ""number"" },
    ""view_id"": { ""type"": ""integer"" },
    ""text_type_id"": { ""type"": ""integer"" },
    ""width"": { ""type"": ""number"", ""default"": 0, ""description"": ""Optional wrapping width in mm. 0 means unwrapped."" },
    ""rotation_deg"": { ""type"": ""number"", ""default"": 0 }
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
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var text = request.Value<string>("text");
            if (string.IsNullOrEmpty(text))
                return CommandResult.Fail("text is required and cannot be empty.");

            if (request["x"] == null || request["y"] == null)
                return CommandResult.Fail("x and y coordinates are required.");

            double x = request.Value<double>("x"); // mm
            double y = request.Value<double>("y"); // mm
            long? viewId = request.Value<long?>("view_id");
            long? textTypeId = request.Value<long?>("text_type_id");
            double width = request.Value<double>("width"); // mm
            double rotationDeg = request.Value<double>("rotation_deg");

            if (width < 0)
                return CommandResult.Fail("width cannot be negative.");

            // Resolve View
            View view = null;
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value))
                    return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                if (view == null)
                    return CommandResult.Fail("View ID " + viewId.Value + " does not resolve to a View.");
            }
            else
            {
                view = doc.ActiveView;
            }

            if (view == null)
                return CommandResult.Fail("No target view could be resolved.");

            if (!EnsureViewCanHostAnnotation(view, out var viewError))
                return CommandResult.Fail(viewError);

            // Resolve TextNoteType
            TextNoteType textType = null;
            if (textTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(textTypeId.Value))
                    return CommandResult.Fail("text_type_id " + RevitCompat.ElementIdRangeError(textTypeId.Value));

                textType = doc.GetElement(RevitCompat.ToElementId(textTypeId.Value)) as TextNoteType;
                if (textType == null)
                    return CommandResult.Fail("Text type ID " + textTypeId.Value + " does not resolve to a TextNoteType.");
            }
            else
            {
                var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                if (defaultId != ElementId.InvalidElementId)
                {
                    textType = doc.GetElement(defaultId) as TextNoteType;
                }
                if (textType == null)
                {
                    textType = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNoteType))
                        .FirstOrDefault() as TextNoteType;
                }
            }

            if (textType == null)
                return CommandResult.Fail("No TextNoteType found in the project.");

            TextNote note = null;
            using (var tx = new Transaction(doc, "RvtMcp: create text note"))
            {
                tx.Start();
                try
                {
                    // Compute position
                    XYZ pos;
                    try
                    {
                        pos = view.Origin + (view.RightDirection * (x / 304.8)) + (view.UpDirection * (y / 304.8));
                    }
                    catch
                    {
                        pos = new XYZ(x / 304.8, y / 304.8, 0);
                    }

                    // Create note
                    if (width <= 0)
                    {
                        note = TextNote.Create(doc, view.Id, pos, text, textType.Id);
                    }
                    else
                    {
                        note = TextNote.Create(doc, view.Id, pos, width / 304.8, text, textType.Id);
                    }

                    if (note == null)
                        throw new Exception("TextNote.Create returned null.");

                    // Apply rotation if needed
                    if (Math.Abs(rotationDeg) > 0.001)
                    {
                        var rad = rotationDeg * Math.PI / 180.0;
                        var axis = Line.CreateBound(pos, pos + view.ViewDirection);
                        ElementTransformUtils.RotateElement(doc, note.Id, axis, rad);
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Create text note transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create text note: " + ex.Message);
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["created"] = true,
                ["text_note_id"] = RevitCompat.GetId(note.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["text_type_id"] = RevitCompat.GetId(note.GetTypeId()),
                ["position"] = new JObject
                {
                    ["unit"] = "mm",
                    ["x"] = Math.Round(note.Coord.X * 304.8, 1),
                    ["y"] = Math.Round(note.Coord.Y * 304.8, 1),
                    ["z"] = Math.Round(note.Coord.Z * 304.8, 1)
                },
                ["width"] = Math.Round(note.Width * 304.8, 1),
                ["rotation_deg"] = rotationDeg,
                ["error"] = null
            });
        }

        private static bool EnsureViewCanHostAnnotation(View view, out string error)
        {
            error = null;
            if (view == null)
            {
                error = "Target view is null.";
                return false;
            }
            if (view.IsTemplate)
            {
                error = "Cannot add annotations to a view template.";
                return false;
            }
            var vt = view.ViewType;
            if (vt == ViewType.Schedule || vt == ViewType.ColumnSchedule || vt == ViewType.ProjectBrowser || vt == ViewType.SystemBrowser || vt == ViewType.Legend)
            {
                error = "View type '" + vt + "' does not support annotations.";
                return false;
            }
            return true;
        }
    }
}
