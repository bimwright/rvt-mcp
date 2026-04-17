using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Plugin.Handlers
{
    public class OperateElementHandler : IRevitCommand
    {
        public string Name => "operate_element";
        public string Description => "Operate on elements: select, hide, unhide, isolate, setColor";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""operation"":{""type"":""string"",""enum"":[""move"",""rotate"",""mirror"",""copy"",""color""]},""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""r"":{""type"":""integer""},""g"":{""type"":""integer""},""b"":{""type"":""integer""}},""required"":[""operation"",""elementIds""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            var uidoc = app.ActiveUIDocument;
            if (doc == null || uidoc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var operation = request.Value<string>("operation")?.ToLower() ?? "";
            var elementIds = request["elementIds"]?.ToObject<long[]>() ?? new long[0];

            if (elementIds.Length == 0)
                return CommandResult.Fail("elementIds array is required.");

            var ids = elementIds.Select(id => RevitCompat.ToElementId(id)).ToList();
            var view = doc.ActiveView;

            switch (operation)
            {
                case "select":
                    uidoc.Selection.SetElementIds(ids);
                    return CommandResult.Ok(new { operation, count = ids.Count });

                case "hide":
                    using (var tx = new Transaction(doc, "MCP: Hide elements"))
                    {
                        tx.Start();
                        view.HideElements(ids);
                        tx.Commit();
                    }
                    return CommandResult.Ok(new { operation, count = ids.Count });

                case "unhide":
                    using (var tx = new Transaction(doc, "MCP: Unhide elements"))
                    {
                        tx.Start();
                        view.UnhideElements(ids);
                        tx.Commit();
                    }
                    return CommandResult.Ok(new { operation, count = ids.Count });

                case "isolate":
                    using (var tx = new Transaction(doc, "MCP: Isolate elements"))
                    {
                        tx.Start();
                        view.IsolateElementsTemporary(ids);
                        tx.Commit();
                    }
                    return CommandResult.Ok(new { operation, count = ids.Count });

                case "setcolor":
                    var r = request.Value<byte?>("r") ?? 255;
                    var g = request.Value<byte?>("g") ?? 0;
                    var b = request.Value<byte?>("b") ?? 0;
                    using (var tx = new Transaction(doc, "MCP: Set element color"))
                    {
                        tx.Start();
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(new Color(r, g, b));
                        ogs.SetSurfaceForegroundPatternColor(new Color(r, g, b));
                        foreach (var id in ids)
                            view.SetElementOverrides(id, ogs);
                        tx.Commit();
                    }
                    return CommandResult.Ok(new { operation, count = ids.Count, color = $"RGB({r},{g},{b})" });

                default:
                    return CommandResult.Fail($"Unknown operation '{operation}'. Supported: select, hide, unhide, isolate, setcolor");
            }
        }
    }
}
