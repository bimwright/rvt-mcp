using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Handlers
{
    // Revit-free parsing/validation/triangle cleanup for create_mesh_shapes.
    // Kept free of Autodesk.Revit types so it is covered by the xUnit suite.

    public class MeshMaterialSpec
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("color")] public int[] Color { get; set; }
        [JsonProperty("transparency")] public int? Transparency { get; set; }
        [JsonProperty("shininess")] public int? Shininess { get; set; }
    }

    public class MeshShapeSpec
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("material")] public string Material { get; set; }
        [JsonProperty("vertices")] public double[][] Vertices { get; set; }
        [JsonProperty("faces")] public int[][] Faces { get; set; }
    }

    public class MeshShapeFileSpec
    {
        [JsonProperty("materials")] public List<MeshMaterialSpec> Materials { get; set; }
        [JsonProperty("shapes")] public List<MeshShapeSpec> Shapes { get; set; }
    }

    /// <summary>A shape after validation: face loops as vertex coordinates in millimetres.</summary>
    public class PreparedMeshShape
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Material { get; set; }
        /// <summary>Each loop is 3 or 4 points, each point {x,y,z} in mm.</summary>
        public List<double[][]> Loops { get; } = new List<double[][]>();
        public bool IsClosed { get; set; }
        public int InputFaces { get; set; }
        public int SkippedDegenerate { get; set; }
        public int SplitQuads { get; set; }
    }

    public static class MeshShapeFile
    {
        public static readonly string[] Categories =
        {
            "GenericModel", "Mass", "StructuralColumns", "StructuralFraming", "Roofs", "Floors", "Walls", "Site",
            "Entourage", "Planting", "Furniture", "SpecialtyEquipment"
        };

        /// <summary>The whole file is parsed on Revit's UI thread, so its size is capped.</summary>
        public const long MaxFileBytes = 256L * 1024 * 1024;
        /// <summary>Faces summed over all shapes; larger imports should be split across files.</summary>
        public const int MaxTotalFaces = 2000000;

        /// <summary>Characters Revit rejects in element and material names.</summary>
        private static readonly char[] InvalidNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>Revit's short-curve tolerance is ~0.78 mm; edges shorter than this are rejected by the builder.</summary>
        public const double MinEdgeMm = 1.0;
        /// <summary>Minimum triangle area (mm²) kept; slivers below this are skipped.</summary>
        public const double MinAreaMm2 = 0.5;
        /// <summary>Max out-of-plane deviation (mm) for a quad to be kept as a single face.</summary>
        public const double PlanarTolMm = 0.5;

        /// <summary>Returns null if the path may be read, else an error message.</summary>
        public static string ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "file must be an absolute path to a .json mesh file on the Revit machine.";
            // A network path would make Revit open an SMB connection to whatever host the caller names.
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                return "file must be on a local drive; network (UNC) paths are not read.";
            // Path.IsPathRooted also accepts "\file.json" and "C:file.json", which resolve against the
            // Revit process's current drive or folder.
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                return "file must be a full local path that starts with a drive letter.";
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                return "file must end in .json.";
            if (OpenModelLocalPolicy.NormalizePath(path).StartsWith(@"\\", StringComparison.Ordinal))
                return "file is on a mapped network drive; network paths are not read.";
            return null;
        }

        /// <summary>Returns null if Revit accepts the name, else an error message.</summary>
        public static string ValidateName(string name, string what)
        {
            if (string.IsNullOrWhiteSpace(name)) return what + " has no name.";
            var bad = name.IndexOfAny(InvalidNameChars);
            return bad < 0 ? null : what + " name '" + name + "' contains '" + name[bad] + "', which Revit does not allow in names.";
        }

        public static MeshShapeFileSpec Load(string path)
        {
            var length = new FileInfo(path).Length;
            if (length > MaxFileBytes)
                throw new InvalidDataException("File is " + length / (1024 * 1024) + " MB; the limit is " + MaxFileBytes / (1024 * 1024) + " MB. Split it into several files.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var sr = new StreamReader(stream))
            using (var reader = new JsonTextReader(sr))
            {
                var serializer = JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Ignore });
                var spec = serializer.Deserialize<MeshShapeFileSpec>(reader);
                if (spec == null) throw new InvalidDataException("File is empty or not a JSON object.");
                spec.Materials = spec.Materials ?? new List<MeshMaterialSpec>();
                spec.Shapes = spec.Shapes ?? new List<MeshShapeSpec>();
                return spec;
            }
        }

        /// <summary>Returns null if valid, else an error message.</summary>
        public static string ValidateMaterial(MeshMaterialSpec m, int index)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Name)) return "materials[" + index + "] has no name.";
            var nameError = ValidateName(m.Name, "Material");
            if (nameError != null) return nameError;
            if (m.Color == null || m.Color.Length != 3 || m.Color.Any(c => c < 0 || c > 255))
                return "Material '" + m.Name + "': color must be [r,g,b] with values 0-255.";
            if (m.Transparency.HasValue && (m.Transparency < 0 || m.Transparency > 100))
                return "Material '" + m.Name + "': transparency must be 0-100.";
            if (m.Shininess.HasValue && (m.Shininess < 0 || m.Shininess > 128))
                return "Material '" + m.Name + "': shininess must be 0-128.";
            return null;
        }

        public static string NormalizeCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return "GenericModel";
            var c = category.Replace(" ", "").Replace("_", "");
            return Categories.FirstOrDefault(k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate indices, drop degenerate faces, keep planar convex quads, split the rest into triangles.
        /// Throws InvalidDataException with the shape name on structural errors (bad index, wrong arity).
        /// </summary>
        public static PreparedMeshShape Prepare(MeshShapeSpec s, int index)
        {
            var label = "shapes[" + index + "]" + (string.IsNullOrWhiteSpace(s?.Name) ? "" : " '" + s.Name + "'");
            if (s == null) throw new InvalidDataException(label + " is null.");
            if (string.IsNullOrWhiteSpace(s.Name)) throw new InvalidDataException(label + " has no name.");
            var nameError = ValidateName(s.Name, label + ": shape");
            if (nameError != null) throw new InvalidDataException(nameError);
            var cat = NormalizeCategory(s.Category);
            if (cat == null)
                throw new InvalidDataException(label + ": unknown category '" + s.Category + "'. Use one of " + string.Join(", ", Categories) + ".");
            var verts = s.Vertices ?? new double[0][];
            var faces = s.Faces ?? new int[0][];
            for (int v = 0; v < verts.Length; v++)
            {
                var p = verts[v];
                if (p == null || p.Length != 3 || p.Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                    throw new InvalidDataException(label + ": vertex " + v + " must be [x,y,z] finite numbers.");
            }

            var prepared = new PreparedMeshShape { Name = s.Name.Trim(), Category = cat, Material = s.Material, InputFaces = faces.Length };
            var edgeUse = new Dictionary<long, int>();

            for (int f = 0; f < faces.Length; f++)
            {
                var face = faces[f];
                if (face == null || (face.Length != 3 && face.Length != 4))
                    throw new InvalidDataException(label + ": face " + f + " must have 3 or 4 vertex indices.");
                foreach (var i in face)
                    if (i < 0 || i >= verts.Length)
                        throw new InvalidDataException(label + ": face " + f + " references vertex " + i + " but there are " + verts.Length + " vertices.");

                if (face.Length == 3)
                {
                    AddTriangle(prepared, verts, face[0], face[1], face[2], edgeUse);
                    continue;
                }

                if (face.Distinct().Count() < 4)
                {
                    // Collapsed quad: treat the distinct indices as a triangle if possible.
                    var d = face.Distinct().ToArray();
                    if (d.Length == 3) AddTriangle(prepared, verts, d[0], d[1], d[2], edgeUse);
                    else prepared.SkippedDegenerate++;
                    continue;
                }

                if (IsGoodQuad(verts, face))
                {
                    prepared.Loops.Add(new[] { verts[face[0]], verts[face[1]], verts[face[2]], verts[face[3]] });
                    for (int k = 0; k < 4; k++) CountEdge(edgeUse, face[k], face[(k + 1) % 4]);
                    continue;
                }

                // Split along the shorter diagonal, preserving winding.
                prepared.SplitQuads++;
                if (Dist(verts[face[0]], verts[face[2]]) <= Dist(verts[face[1]], verts[face[3]]))
                {
                    AddTriangle(prepared, verts, face[0], face[1], face[2], edgeUse);
                    AddTriangle(prepared, verts, face[0], face[2], face[3], edgeUse);
                }
                else
                {
                    AddTriangle(prepared, verts, face[0], face[1], face[3], edgeUse);
                    AddTriangle(prepared, verts, face[1], face[2], face[3], edgeUse);
                }
            }

            prepared.IsClosed = prepared.Loops.Count >= 4 && IsClosedAndConsistent(edgeUse);
            return prepared;
        }

        /// <summary>
        /// Closed and consistently wound: every directed edge is used once and its reverse once.
        /// An edge used twice in the same direction means a flipped face, which cannot bound a solid.
        /// </summary>
        private static bool IsClosedAndConsistent(Dictionary<long, int> edgeUse)
        {
            if (edgeUse.Count == 0) return false;
            foreach (var pair in edgeUse)
            {
                if (pair.Value != 1) return false;
                var reverse = ((long)(uint)pair.Key << 32) | (uint)(pair.Key >> 32);
                if (!edgeUse.TryGetValue(reverse, out var n) || n != 1) return false;
            }
            return true;
        }

        private static void AddTriangle(PreparedMeshShape p, double[][] v, int a, int b, int c, Dictionary<long, int> edgeUse)
        {
            if (a == b || b == c || a == c || IsDegenerate(v[a], v[b], v[c]))
            {
                p.SkippedDegenerate++;
                return;
            }
            p.Loops.Add(new[] { v[a], v[b], v[c] });
            CountEdge(edgeUse, a, b);
            CountEdge(edgeUse, b, c);
            CountEdge(edgeUse, c, a);
        }

        // Directed edge a->b; IsClosedAndConsistent pairs it with b->a.
        private static void CountEdge(Dictionary<long, int> edgeUse, int a, int b)
        {
            long key = ((long)a << 32) | (uint)b;
            edgeUse.TryGetValue(key, out var n);
            edgeUse[key] = n + 1;
        }

        public static bool IsDegenerate(double[] a, double[] b, double[] c)
        {
            if (Dist(a, b) < MinEdgeMm || Dist(b, c) < MinEdgeMm || Dist(c, a) < MinEdgeMm) return true;
            var n = Cross(Sub(b, a), Sub(c, a));
            return 0.5 * Len(n) < MinAreaMm2;
        }

        private static bool IsGoodQuad(double[][] v, int[] q)
        {
            var p = q.Select(i => v[i]).ToArray();
            for (int k = 0; k < 4; k++)
                if (Dist(p[k], p[(k + 1) % 4]) < MinEdgeMm) return false;
            // Normal from the two diagonals (robust for planar quads).
            var n = Cross(Sub(p[2], p[0]), Sub(p[3], p[1]));
            var len = Len(n);
            if (len < 1e-9) return false;
            n = new[] { n[0] / len, n[1] / len, n[2] / len };
            var centroid = new[] { (p[0][0] + p[1][0] + p[2][0] + p[3][0]) / 4, (p[0][1] + p[1][1] + p[2][1] + p[3][1]) / 4, (p[0][2] + p[1][2] + p[2][2] + p[3][2]) / 4 };
            foreach (var pt in p)
                if (Math.Abs(Dot(Sub(pt, centroid), n)) > PlanarTolMm) return false;
            // Convex: every corner turns the same way as the face normal, with non-trivial area.
            for (int k = 0; k < 4; k++)
            {
                var c = Cross(Sub(p[(k + 1) % 4], p[k]), Sub(p[(k + 2) % 4], p[(k + 1) % 4]));
                if (Dot(c, n) <= 2 * MinAreaMm2) return false;
            }
            return true;
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Len(double[] a) => Math.Sqrt(Dot(a, a));
        private static double Dist(double[] a, double[] b) => Len(Sub(a, b));
    }
}
