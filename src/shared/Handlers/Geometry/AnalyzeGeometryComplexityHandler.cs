using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AnalyzeGeometryComplexityHandler : IRevitCommand
    {
        public string Name => "analyze_geometry_complexity";
        public string Description => "Analyze and rank elements by geometric complexity (based on solid, face, edge, mesh, and curve counts) to find performance-heavy families or elements.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""view_id"": { ""type"": ""integer"" },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Medium"" },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""maximum"": 500 }
  }
}";

        private class ScannedComplexityItem
        {
            public long ElementId { get; set; }
            public string Name { get; set; }
            public string Category { get; set; }
            public int SolidCount { get; set; }
            public int FaceCount { get; set; }
            public int EdgeCount { get; set; }
            public int MeshCount { get; set; }
            public int CurveCount { get; set; }
            public int GeometryInstanceCount { get; set; }
            public int ComplexityScore { get; set; }
        }

        private struct ComplexityCounts
        {
            public int SolidCount;
            public int FaceCount;
            public int EdgeCount;
            public int CurveCount;
            public int MeshCount;
            public int MeshVertexCount;
            public int InstanceCount;
        }

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
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var elementIdsToken = request["element_ids"] ?? request["elementIds"];
            var categoriesToken = request["categories"];
            var viewIdToken = request["view_id"] ?? request["viewId"];

            bool hasIds = elementIdsToken != null && elementIdsToken.Type == JTokenType.Array && elementIdsToken.Count() > 0;
            bool hasCategories = categoriesToken != null && categoriesToken.Type == JTokenType.Array && categoriesToken.Count() > 0;
            bool hasView = viewIdToken != null && viewIdToken.Type == JTokenType.Integer;

            if (!hasIds && !hasCategories && !hasView)
            {
                return CommandResult.Fail("Accidental whole-model scan prevented. You must supply at least one of 'element_ids', 'categories', or 'view_id' to scope the complexity analysis.");
            }

            var limitToken = request["limit"];
            int limit = limitToken != null ? limitToken.Value<int>() : 200;
            if (limit < 1)
                return CommandResult.Fail("limit must be at least 1.");
            if (limit > 500)
                return CommandResult.Fail("Limit exceeded: limit cannot exceed the hard maximum of 500.");

            var detailLevelStr = (request["detail_level"] ?? request["detailLevel"])?.Value<string>() ?? "Medium";
            if (!TryParseDetailLevel(detailLevelStr, out var detailLevelEnum))
                return CommandResult.Fail("detail_level must be one of: Coarse, Medium, Fine.");

            var options = new Options
            {
                DetailLevel = detailLevelEnum,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            IEnumerable<Element> elementsToScan;
            if (hasIds)
            {
                var ids = new List<long>();
                foreach (var token in elementIdsToken)
                {
                    if (token.Type == JTokenType.Integer) ids.Add(token.Value<long>());
                }

                var list = new List<Element>();
                foreach (var id in ids)
                {
                    if (RevitCompat.CanRepresentElementId(id))
                    {
                        try
                        {
                            var el = doc.GetElement(RevitCompat.ToElementId(id));
                            if (el != null) list.Add(el);
                        }
                        catch {}
                    }
                }
                elementsToScan = list;
            }
            else
            {
                FilteredElementCollector collector;
                if (hasView)
                {
                    long vId = viewIdToken.Value<long>();
                    if (!RevitCompat.CanRepresentElementId(vId))
                        return CommandResult.Fail(RevitCompat.ElementIdRangeError(vId));

                    var view = doc.GetElement(RevitCompat.ToElementId(vId)) as View;
                    if (view == null)
                        return CommandResult.Fail($"View with ID {vId} not found.");

                    collector = new FilteredElementCollector(doc, view.Id);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                collector.WhereElementIsNotElementType();

                if (hasCategories)
                {
                    var catNames = categoriesToken.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    var resolvedCats = ResolveCategories(doc, catNames);
                    if (resolvedCats.Count > 0)
                    {
                        var catFilter = new ElementMulticategoryFilter(resolvedCats.Select(c => c.Id).ToList());
                        collector.WherePasses(catFilter);
                    }
                    else
                    {
                        return CommandResult.Fail("None of the specified categories could be resolved.");
                    }
                }

                elementsToScan = collector;
            }

            var scannedData = new List<ScannedComplexityItem>();
            var failed = new List<object>();

            int scannedCount = 0;
            int totalSolids = 0;
            int totalFaces = 0;
            int totalEdges = 0;
            bool scanTruncated = false;

            foreach (var elem in elementsToScan)
            {
                if (scannedCount >= limit)
                {
                    scanTruncated = true;
                    break;
                }

                scannedCount++;
                ComplexityCounts counts = new ComplexityCounts();

                try
                {
                    var geom = elem.get_Geometry(options);
                    if (geom != null)
                    {
                        foreach (GeometryObject obj in geom)
                        {
                            TraverseGeometry(obj, ref counts);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = RevitCompat.GetId(elem.Id), error = "Complexity calculation failed: " + ex.Message });
                    continue;
                }

                // Complexity Score Proposal:
                // face_count + edge_count + mesh_vertex_count + (solid_count * 5) + (geometry_instance_count * 10)
                int score = counts.FaceCount + counts.EdgeCount + counts.MeshVertexCount + (counts.SolidCount * 5) + (counts.InstanceCount * 10);

                totalSolids += counts.SolidCount;
                totalFaces += counts.FaceCount;
                totalEdges += counts.EdgeCount;

                scannedData.Add(new ScannedComplexityItem
                {
                    ElementId = RevitCompat.GetId(elem.Id),
                    Name = elem.Name ?? string.Empty,
                    Category = elem.Category?.Name ?? string.Empty,
                    SolidCount = counts.SolidCount,
                    FaceCount = counts.FaceCount,
                    EdgeCount = counts.EdgeCount,
                    MeshCount = counts.MeshCount,
                    CurveCount = counts.CurveCount,
                    GeometryInstanceCount = counts.InstanceCount,
                    ComplexityScore = score
                });
            }

            // Rank descending
            var sortedData = scannedData
                .OrderByDescending(d => d.ComplexityScore)
                .ToList();

            bool truncated = scanTruncated;
            var returnedData = sortedData.Take(limit).ToList();

            var rankedResults = new List<object>();
            int rank = 1;
            foreach (var d in returnedData)
            {
                rankedResults.Add(new
                {
                    element_id = d.ElementId,
                    name = d.Name,
                    category = d.Category,
                    solid_count = d.SolidCount,
                    face_count = d.FaceCount,
                    edge_count = d.EdgeCount,
                    mesh_count = d.MeshCount,
                    curve_count = d.CurveCount,
                    geometry_instance_count = d.GeometryInstanceCount,
                    complexity_score = d.ComplexityScore,
                    rank = rank++
                });
            }

            int topComplexityScore = sortedData.Count > 0 ? sortedData[0].ComplexityScore : 0;

            return CommandResult.Ok(new
            {
                detail_level = detailLevelStr,
                scanned = scannedCount,
                returned = rankedResults.Count,
                limit = limit,
                truncated = truncated,
                truncation_reason = truncated ? "scan_limit" : null,
                summary = new
                {
                    total_solids = totalSolids,
                    total_faces = totalFaces,
                    total_edges = totalEdges,
                    top_complexity_score = topComplexityScore
                },
                elements = rankedResults,
                failed = failed,
                error = (string)null
            });
        }

        private static ICollection<Category> ResolveCategories(Document doc, IEnumerable<string> categoryNames)
        {
            var resolved = new List<Category>();
            var allCategories = doc.Settings.Categories;

            foreach (var name in categoryNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                Category found = null;
                foreach (Category cat in allCategories)
                {
                    if (cat.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = cat;
                        break;
                    }
                }

                if (found == null)
                {
                    foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
                    {
                        if (bic.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                found = Category.GetCategory(doc, bic);
                                if (found != null) break;
                            }
                            catch {}
                        }
                    }
                }

                if (found != null)
                {
                    resolved.Add(found);
                }
            }
            return resolved;
        }

        private static bool TryParseDetailLevel(string value, out ViewDetailLevel detailLevel)
        {
            if (value.Equals("Coarse", StringComparison.OrdinalIgnoreCase))
            {
                detailLevel = ViewDetailLevel.Coarse;
                return true;
            }
            if (value.Equals("Medium", StringComparison.OrdinalIgnoreCase))
            {
                detailLevel = ViewDetailLevel.Medium;
                return true;
            }
            if (value.Equals("Fine", StringComparison.OrdinalIgnoreCase))
            {
                detailLevel = ViewDetailLevel.Fine;
                return true;
            }

            detailLevel = ViewDetailLevel.Medium;
            return false;
        }

        private static void TraverseGeometry(GeometryObject geomObj, ref ComplexityCounts counts)
        {
            if (geomObj == null) return;

            if (geomObj is Solid solid)
            {
                counts.SolidCount++;
                try
                {
                    var faces = solid.Faces;
                    if (faces != null) counts.FaceCount += faces.Size;
                }
                catch {}

                try
                {
                    var edges = solid.Edges;
                    if (edges != null) counts.EdgeCount += edges.Size;
                }
                catch {}
            }
            else if (geomObj is GeometryInstance instance)
            {
                counts.InstanceCount++;
                try
                {
                    var instGeom = instance.GetInstanceGeometry();
                    if (instGeom != null)
                    {
                        foreach (GeometryObject nested in instGeom)
                        {
                            TraverseGeometry(nested, ref counts);
                        }
                    }
                }
                catch {}
            }
            else if (geomObj is Mesh mesh)
            {
                counts.MeshCount++;
                try
                {
                    var vertices = mesh.Vertices;
                    if (vertices != null) counts.MeshVertexCount += vertices.Count;
                }
                catch {}
            }
            else if (geomObj is Curve)
            {
                counts.CurveCount++;
            }
        }
    }
}
