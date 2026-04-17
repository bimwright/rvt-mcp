using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin.Handlers
{
    public class DetectSystemElementsHandler : IRevitCommand
    {
        public string Name => "detect_system_elements";
        public string Description => "Detect all elements in a MEP system from a seed element. Uses connector traversal to find pipes, fittings, accessories, and equipment connected to the given element. Returns element IDs by category and bounding box in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementId"":{""type"":""integer"",""description"":""Element ID to trace connected system""}},""required"":[""elementId""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var elementId = request.Value<long?>("elementId");
            if (elementId == null)
                return CommandResult.Fail("elementId is required.");

            var seedElement = doc.GetElement(RevitCompat.ToElementId(elementId.Value));
            if (seedElement == null)
                return CommandResult.Fail($"Element {elementId.Value} not found in model.");

            // Get the ConnectorManager for the seed element
            ConnectorManager seedCm = GetConnectorManager(seedElement);
            if (seedCm == null)
                return CommandResult.Fail($"Element {elementId.Value} has no MEP connectors.");

            // BFS traversal
            var visited = new HashSet<long>();
            var queue = new Queue<Element>();
            visited.Add(RevitCompat.GetId(seedElement.Id));
            queue.Enqueue(seedElement);

            string systemName = null;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                ConnectorManager cm = GetConnectorManager(current);
                if (cm == null) continue;

                foreach (Connector connector in cm.Connectors)
                {
                    // Try to get system name from first non-null MEPSystem
                    if (systemName == null)
                    {
                        try { systemName = connector.MEPSystem?.Name; } catch { }
                    }

                    ConnectorSet refs;
                    try { refs = connector.AllRefs; } catch { continue; }

                    foreach (Connector refConn in refs)
                    {
                        var owner = refConn.Owner;
                        if (owner == null) continue;

                        // Skip logical connectors (e.g. system pseudo-connectors with no real element)
                        long ownerId = RevitCompat.GetId(owner.Id);
                        if (ownerId <= 0) continue;
                        if (visited.Contains(ownerId)) continue;

                        visited.Add(ownerId);
                        queue.Enqueue(owner);
                    }
                }
            }

            // Classify elements by category
            var pipes = new List<long>();
            var fittings = new List<long>();
            var accessories = new List<long>();
            var equipment = new List<long>();

            // Bounding box aggregation (in feet, convert at end)
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool hasBounds = false;

            foreach (long id in visited)
            {
                var el = doc.GetElement(RevitCompat.ToElementId(id));
                if (el == null) continue;

                // Classify
                long catId = RevitCompat.GetIdOrNull(el.Category?.Id) ?? 0;
                if (catId == (long)BuiltInCategory.OST_PipeCurves)
                    pipes.Add(id);
                else if (catId == (long)BuiltInCategory.OST_PipeFitting)
                    fittings.Add(id);
                else if (catId == (long)BuiltInCategory.OST_PipeAccessory)
                    accessories.Add(id);
                else if (catId == (long)BuiltInCategory.OST_MechanicalEquipment)
                    equipment.Add(id);

                // Aggregate bounding box
                BoundingBoxXYZ bb = null;
                try { bb = el.get_BoundingBox(null); } catch { }
                if (bb == null) continue;

                hasBounds = true;
                if (bb.Min.X < minX) minX = bb.Min.X;
                if (bb.Min.Y < minY) minY = bb.Min.Y;
                if (bb.Min.Z < minZ) minZ = bb.Min.Z;
                if (bb.Max.X > maxX) maxX = bb.Max.X;
                if (bb.Max.Y > maxY) maxY = bb.Max.Y;
                if (bb.Max.Z > maxZ) maxZ = bb.Max.Z;
            }

            const double feetToMm = 304.8;

            object boundingBox = hasBounds
                ? (object)new
                {
                    minX = Math.Round(minX * feetToMm, 1),
                    minY = Math.Round(minY * feetToMm, 1),
                    minZ = Math.Round(minZ * feetToMm, 1),
                    maxX = Math.Round(maxX * feetToMm, 1),
                    maxY = Math.Round(maxY * feetToMm, 1),
                    maxZ = Math.Round(maxZ * feetToMm, 1)
                }
                : null;

            return CommandResult.Ok(new
            {
                systemName,
                elementCount = visited.Count,
                boundingBox,
                byCategory = new
                {
                    pipes,
                    fittings,
                    accessories,
                    equipment
                }
            });
        }

        private static ConnectorManager GetConnectorManager(Element el)
        {
            try
            {
                if (el is MEPCurve curve)
                    return curve.ConnectorManager;

                if (el is FamilyInstance fi)
                    return fi.MEPModel?.ConnectorManager;
            }
            catch { }

            return null;
        }
    }
}
