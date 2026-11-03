using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Builds DirectShapes from a JSON mesh file (materials + shapes with vertices in mm and 0-based
    /// tri/quad faces) in one transaction. The payload is a file because large meshes do not fit in
    /// tool arguments. Parsing and validation live in MeshShapeFile so they can be tested without Revit.
    /// </summary>
    public class CreateMeshShapesHandler : IRevitCommand
    {
        public const string AppId = "RvtMcp.MeshShapes";
        private const double MmToFt = 1.0 / 304.8;
        private const int MaxListed = 200;

        public string Name => "create_mesh_shapes";

        public string Description => "Create DirectShapes (with materials) from a JSON mesh file: {materials:[{name,color,transparency,shininess}], shapes:[{name,category,material,vertices(mm),faces}]}";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""file"":{""type"":""string"",""description"":""Absolute local path to the .json mesh file""},""replace_existing"":{""type"":""boolean"",""description"":""Delete DirectShapes previously created by this tool with the same names first (default false)""},""update_materials"":{""type"":""boolean"",""description"":""Overwrite color/transparency/shininess of existing materials with the same name (default false: existing materials are reused unchanged)""},""mesh_only"":{""type"":""boolean"",""description"":""Build Mesh geometry directly instead of trying solids first (faster for huge shapes; default false)""}},""required"":[""file""]}";

        private class MaterialCounts
        {
            public int Created { get; set; }
            public int Updated { get; set; }
            public int Reused { get; set; }
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("Open a project document; DirectShapes are created in projects here.");

            var request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson);
            var file = request.Value<string>("file")?.Trim().Trim('"');
            var replace = request.Value<bool?>("replace_existing") ?? false;
            var updateMaterials = request.Value<bool?>("update_materials") ?? false;
            var meshOnly = request.Value<bool?>("mesh_only") ?? false;
            var pathError = MeshShapeFile.ValidatePath(file);
            if (pathError != null) return CommandResult.Fail(pathError);
            if (!File.Exists(file)) return CommandResult.Fail("File not found: " + file);

            var clock = Stopwatch.StartNew();
            MeshShapeFileSpec spec;
            var prepared = new List<PreparedMeshShape>();
            try
            {
                spec = MeshShapeFile.Load(file);
                for (int i = 0; i < spec.Materials.Count; i++)
                {
                    var error = MeshShapeFile.ValidateMaterial(spec.Materials[i], i);
                    if (error != null) return CommandResult.Fail(error + " Nothing was changed.");
                }
                var totalFaces = spec.Shapes.Sum(s => (long)(s?.Faces?.Length ?? 0));
                if (totalFaces > MeshShapeFile.MaxTotalFaces)
                    return CommandResult.Fail("File has " + totalFaces + " faces; the limit per call is " + MeshShapeFile.MaxTotalFaces + ". Split it into several files. Nothing was changed.");
                for (int i = 0; i < spec.Shapes.Count; i++)
                    prepared.Add(MeshShapeFile.Prepare(spec.Shapes[i], i));
                // Free the raw arrays early; the prepared loops hold what is needed.
                spec.Shapes = null;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Could not read mesh file: " + ex.Message + " Nothing was changed.");
            }
            var parseMs = clock.ElapsedMilliseconds;

            var warnings = new List<string>();
            var duplicateNames = prepared.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).Take(10).ToList();
            if (duplicateNames.Count > 0)
                warnings.Add("Duplicate shape names in file (each still gets its own element): " + string.Join(", ", duplicateNames));

            var materials = new MaterialCounts();
            var deleted = 0;
            var created = new List<KeyValuePair<string, long>>();
            var outcomes = new Dictionary<string, int>();
            var categoryFallbacks = new HashSet<string>();
            string currentShape = null;

            using (var tx = new Transaction(doc, "MCP: Create mesh shapes"))
            {
                tx.Start();
                try
                {
                    var materialIds = PrepareMaterials(doc, spec.Materials, prepared, updateMaterials, materials, warnings);
                    if (replace) deleted = DeletePreviousShapes(doc, prepared);

                    var categoryIds = new Dictionary<string, ElementId>();
                    foreach (var shape in prepared)
                    {
                        currentShape = shape.Name;
                        if (shape.Loops.Count == 0)
                        {
                            warnings.Add("Shape '" + shape.Name + "' has no usable faces; skipped.");
                            continue;
                        }

                        var categoryId = ResolveCategory(doc, shape.Category, categoryIds, categoryFallbacks);
                        var materialId = ElementId.InvalidElementId;
                        if (!string.IsNullOrWhiteSpace(shape.Material) && materialIds.TryGetValue(shape.Material, out var id)) materialId = id;

                        var objects = Build(shape, materialId, meshOnly, out var outcome);
                        if (objects == null || objects.Count == 0)
                            throw new InvalidOperationException("TessellatedShapeBuilder produced no geometry (outcome " + outcome + ")");
                        outcomes.TryGetValue(outcome, out var count);
                        outcomes[outcome] = count + 1;

                        var directShape = DirectShape.CreateElement(doc, categoryId);
                        directShape.ApplicationId = AppId;
                        directShape.ApplicationDataId = shape.Name;
                        directShape.SetShape(objects);
                        directShape.SetName(shape.Name);
                        created.Add(new KeyValuePair<string, long>(shape.Name, RevitCompat.GetId(directShape.Id)));
                    }
                    currentShape = null;

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; nothing was created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    var where = currentShape != null ? "shape '" + currentShape + "'" : "materials/cleanup step";
                    return CommandResult.Fail("Failed at " + where + ": " + ex.Message + ". Transaction rolled back; nothing was changed.");
                }
            }

            if (categoryFallbacks.Count > 0)
                warnings.Add("Categories not allowed for DirectShape, used GenericModel instead: " + string.Join(", ", categoryFallbacks));

            var listed = new Dictionary<string, long>();
            foreach (var pair in created.Take(MaxListed))
                if (!listed.ContainsKey(pair.Key)) listed[pair.Key] = pair.Value;

            return CommandResult.Ok(new
            {
                file,
                shapesInFile = prepared.Count,
                shapesCreated = created.Count,
                deletedExisting = deleted,
                materialsCreated = materials.Created,
                materialsUpdated = materials.Updated,
                materialsReused = materials.Reused,
                inputFaces = prepared.Sum(p => p.InputFaces),
                facesBuilt = prepared.Sum(p => p.Loops.Count),
                skippedDegenerateFaces = prepared.Sum(p => p.SkippedDegenerate),
                splitNonPlanarQuads = prepared.Sum(p => p.SplitQuads),
                closedShapes = prepared.Count(p => p.IsClosed),
                geometryOutcomes = outcomes,
                elementIds = listed,
                elementIdsTruncated = created.Count > MaxListed,
                warnings,
                parseMs,
                totalMs = clock.ElapsedMilliseconds
            });
        }

        /// <summary>
        /// Creates the file's missing materials and returns name -> id for every material the shapes use.
        /// Existing materials are reused as they are unless update_materials is set.
        /// </summary>
        private static Dictionary<string, ElementId> PrepareMaterials(Document doc, IList<MeshMaterialSpec> specs, IList<PreparedMeshShape> shapes,
            bool update, MaterialCounts counts, List<string> warnings)
        {
            // Revit material names are unique regardless of case.
            var ids = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var spec in specs)
            {
                if (existing.TryGetValue(spec.Name, out var material))
                {
                    ids[spec.Name] = material.Id;
                    if (!update)
                    {
                        counts.Reused++;
                        continue;
                    }
                    counts.Updated++;
                }
                else
                {
                    material = (Material)doc.GetElement(Material.Create(doc, spec.Name));
                    existing[spec.Name] = material;
                    counts.Created++;
                }
                material.Color = new Color((byte)spec.Color[0], (byte)spec.Color[1], (byte)spec.Color[2]);
                if (spec.Transparency.HasValue) material.Transparency = spec.Transparency.Value;
                if (spec.Shininess.HasValue) material.Shininess = spec.Shininess.Value;
                ids[spec.Name] = material.Id;
            }

            // Shapes may use materials that are already in the model but not in the file.
            foreach (var name in shapes.Select(s => s.Material).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                if (ids.ContainsKey(name)) continue;
                if (existing.TryGetValue(name, out var material)) ids[name] = material.Id;
                else warnings.Add("Material '" + name + "' not found; shapes using it get no material.");
            }
            return ids;
        }

        /// <summary>Deletes DirectShapes this tool created earlier under the same names; returns how many.</summary>
        private static int DeletePreviousShapes(Document doc, IEnumerable<PreparedMeshShape> shapes)
        {
            var names = new HashSet<string>(shapes.Select(s => s.Name), StringComparer.Ordinal);
            var old = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                .Where(d => d.ApplicationId == AppId && names.Contains(d.ApplicationDataId ?? ""))
                .Select(d => d.Id)
                .ToList();
            if (old.Count > 0) doc.Delete(old);
            return old.Count;
        }

        private static ElementId ResolveCategory(Document doc, string category, Dictionary<string, ElementId> cache, HashSet<string> fallbacks)
        {
            if (cache.TryGetValue(category, out var id)) return id;
            id = new ElementId(MapCategory(category));
            if (!DirectShape.IsValidCategoryId(id, doc))
            {
                fallbacks.Add(category);
                id = new ElementId(BuiltInCategory.OST_GenericModel);
            }
            cache[category] = id;
            return id;
        }

        private static IList<GeometryObject> Build(PreparedMeshShape shape, ElementId materialId, bool meshOnly, out string outcome)
        {
            var builder = new TessellatedShapeBuilder();
            builder.OpenConnectedFaceSet(shape.IsClosed && !meshOnly);
            foreach (var loop in shape.Loops)
            {
                var points = new List<XYZ>(loop.Length);
                foreach (var p in loop) points.Add(new XYZ(p[0] * MmToFt, p[1] * MmToFt, p[2] * MmToFt));
                builder.AddFace(new TessellatedFace(points, materialId));
            }
            builder.CloseConnectedFaceSet();
            builder.Target = meshOnly ? TessellatedShapeBuilderTarget.Mesh : TessellatedShapeBuilderTarget.AnyGeometry;
            builder.Fallback = meshOnly ? TessellatedShapeBuilderFallback.Salvage : TessellatedShapeBuilderFallback.Mesh;
            builder.Build();
            var result = builder.GetBuildResult();
            outcome = result.Outcome.ToString();
            if (!result.AreObjectsAvailable) return null;
            return result.GetGeometricalObjects();
        }

        private static BuiltInCategory MapCategory(string category)
        {
            switch (category)
            {
                case "Mass": return BuiltInCategory.OST_Mass;
                case "Planting": return BuiltInCategory.OST_Planting;
                case "Furniture": return BuiltInCategory.OST_Furniture;
                case "SpecialtyEquipment": return BuiltInCategory.OST_SpecialityEquipment;
                case "StructuralColumns": return BuiltInCategory.OST_StructuralColumns;
                case "StructuralFraming": return BuiltInCategory.OST_StructuralFraming;
                case "Roofs": return BuiltInCategory.OST_Roofs;
                case "Floors": return BuiltInCategory.OST_Floors;
                case "Walls": return BuiltInCategory.OST_Walls;
                case "Site": return BuiltInCategory.OST_Site;
                case "Entourage": return BuiltInCategory.OST_Entourage;
                default: return BuiltInCategory.OST_GenericModel;
            }
        }
    }
}
