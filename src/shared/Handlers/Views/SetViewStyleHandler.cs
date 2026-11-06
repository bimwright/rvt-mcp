using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewStyleHandler : IRevitCommand
    {
        public string Name => "set_view_style";
        public string Description => "Set a view's display style and detail level";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""view_name"":{""type"":""string""},""display_style"":{""type"":""string"",""enum"":[""wireframe"",""hidden_line"",""shaded"",""shaded_edges"",""consistent_colors"",""realistic""]},""detail_level"":{""type"":""string"",""enum"":[""coarse"",""medium"",""fine""]}}}";

        private static readonly Dictionary<string, DisplayStyle> Styles = new Dictionary<string, DisplayStyle>(StringComparer.OrdinalIgnoreCase)
        {
            ["wireframe"] = DisplayStyle.Wireframe,
            ["hidden_line"] = DisplayStyle.HLR,
            ["shaded"] = DisplayStyle.Shading,
            ["shaded_edges"] = DisplayStyle.ShadingWithEdges,
            ["consistent_colors"] = DisplayStyle.FlatColors,
            ["realistic"] = DisplayStyle.Realistic,
        };

        private static readonly Dictionary<string, ViewDetailLevel> DetailLevels = new Dictionary<string, ViewDetailLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["coarse"] = ViewDetailLevel.Coarse,
            ["medium"] = ViewDetailLevel.Medium,
            ["fine"] = ViewDetailLevel.Fine,
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            var request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson);
            var view = ViewPresentationSupport.ResolveView(uidoc, request, false, out var error);
            if (view == null) return CommandResult.Fail(error);

            DisplayStyle? style = null;
            var styleName = request.Value<string>("display_style");
            if (!string.IsNullOrWhiteSpace(styleName))
            {
                if (!Styles.TryGetValue(styleName.Trim(), out var s))
                    return CommandResult.Fail("display_style must be one of: " + string.Join(", ", Styles.Keys));
                style = s;
            }

            ViewDetailLevel? detail = null;
            var detailName = request.Value<string>("detail_level");
            if (!string.IsNullOrWhiteSpace(detailName))
            {
                if (!DetailLevels.TryGetValue(detailName.Trim(), out var d))
                    return CommandResult.Fail("detail_level must be one of: " + string.Join(", ", DetailLevels.Keys));
                detail = d;
            }

            if (!style.HasValue && !detail.HasValue)
                return CommandResult.Fail("Pass display_style, detail_level or both.");

            using (var tx = new Transaction(doc, "MCP: Set view style"))
            {
                tx.Start();
                try
                {
                    if (style.HasValue) view.DisplayStyle = style.Value;
                    if (detail.HasValue) view.DetailLevel = detail.Value;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; the view was not changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    var hint = view.ViewTemplateId != ElementId.InvalidElementId ? " The view has a view template that may control these settings." : "";
                    return CommandResult.Fail("Failed to set view style on '" + view.Name + "': " + ex.Message + hint + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                view_name = view.Name,
                display_style = NameOf(Styles, view.DisplayStyle),
                detail_level = NameOf(DetailLevels, view.DetailLevel)
            });
        }

        // Report values in the same words the tool accepts, so a caller can pass them straight back.
        private static string NameOf<T>(Dictionary<string, T> names, T value)
            => names.FirstOrDefault(pair => EqualityComparer<T>.Default.Equals(pair.Value, value)).Key ?? value.ToString();
    }
}
