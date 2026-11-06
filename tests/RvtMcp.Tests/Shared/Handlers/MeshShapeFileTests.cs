using System;
using System.IO;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Input checks and triangle cleanup behind revit_create_mesh_shapes. These run
    /// without Revit because the parsing and validation live in MeshShapeFile.
    /// </summary>
    public class MeshShapeFileTests
    {
        // A 1 m cube, faces wound counter-clockwise seen from outside.
        private static double[][] CubeVertices() => new[]
        {
            new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 }, new double[] { 1000, 1000, 0 }, new double[] { 0, 1000, 0 },
            new double[] { 0, 0, 1000 }, new double[] { 1000, 0, 1000 }, new double[] { 1000, 1000, 1000 }, new double[] { 0, 1000, 1000 }
        };

        private static int[][] CubeFaces() => new[]
        {
            new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
            new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 }
        };

        private static MeshShapeSpec Cube(int[][] faces = null) => new MeshShapeSpec
        {
            Name = "Cube", Category = "GenericModel", Vertices = CubeVertices(), Faces = faces ?? CubeFaces()
        };

        [Fact]
        public void Consistently_wound_cube_is_closed()
        {
            var prepared = MeshShapeFile.Prepare(Cube(), 0);
            Assert.True(prepared.IsClosed);
            Assert.Equal(6, prepared.Loops.Count);
        }

        [Fact]
        public void Cube_with_one_flipped_face_is_not_closed()
        {
            // Every undirected edge is still used twice, but the flipped face cannot bound a solid.
            var faces = CubeFaces();
            faces[1] = new[] { 7, 6, 5, 4 };
            Assert.False(MeshShapeFile.Prepare(Cube(faces), 0).IsClosed);
        }

        [Fact]
        public void Cube_missing_a_face_is_open()
        {
            var faces = CubeFaces();
            Assert.False(MeshShapeFile.Prepare(Cube(new[] { faces[0], faces[1], faces[2], faces[3], faces[4] }), 0).IsClosed);
        }

        [Fact]
        public void Degenerate_triangle_is_skipped_and_counted()
        {
            var shape = new MeshShapeSpec
            {
                Name = "Sliver",
                Vertices = new[] { new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 }, new double[] { 2000, 0, 0 } },
                Faces = new[] { new[] { 0, 1, 2 } }
            };
            var prepared = MeshShapeFile.Prepare(shape, 0);
            Assert.Empty(prepared.Loops);
            Assert.Equal(1, prepared.SkippedDegenerate);
        }

        [Fact]
        public void Out_of_range_index_names_the_shape()
        {
            var shape = new MeshShapeSpec { Name = "Broken", Vertices = CubeVertices(), Faces = new[] { new[] { 0, 1, 99 } } };
            var ex = Assert.Throws<InvalidDataException>(() => MeshShapeFile.Prepare(shape, 3));
            Assert.Contains("shapes[3] 'Broken'", ex.Message);
        }

        [Fact]
        public void Non_finite_vertex_is_rejected()
        {
            var verts = CubeVertices();
            verts[2] = new[] { 0, double.NaN, 0 };
            Assert.Throws<InvalidDataException>(() => MeshShapeFile.Prepare(new MeshShapeSpec { Name = "Nan", Vertices = verts, Faces = CubeFaces() }, 0));
        }

        [Theory]
        [InlineData("Canopy", null)]
        [InlineData("Canopy [east]", "[")]
        [InlineData("Roof:skin", ":")]
        public void Names_with_characters_Revit_rejects_are_refused(string name, string badChar)
        {
            var error = MeshShapeFile.ValidateName(name, "Shape");
            if (badChar == null) Assert.Null(error);
            else Assert.Contains("'" + badChar + "'", error);
        }

        [Fact]
        public void Invalid_material_name_is_refused()
        {
            var error = MeshShapeFile.ValidateMaterial(new MeshMaterialSpec { Name = "Steel|Grey", Color = new[] { 1, 2, 3 } }, 0);
            Assert.Contains("'|'", error);
        }

        [Fact]
        public void Category_names_are_normalized()
        {
            Assert.Equal("StructuralColumns", MeshShapeFile.NormalizeCategory("structural columns"));
            Assert.Equal("SpecialtyEquipment", MeshShapeFile.NormalizeCategory("Specialty_Equipment"));
            Assert.Equal("GenericModel", MeshShapeFile.NormalizeCategory(null));
            Assert.Null(MeshShapeFile.NormalizeCategory("Doors"));
        }

        [Theory]
        [InlineData(@"C:\models\canopy.json", true)]
        [InlineData("C:/models/canopy.JSON", true)]
        [InlineData(@"models\canopy.json", false)]
        [InlineData(@"\models\canopy.json", false)]
        [InlineData("C:canopy.json", false)]
        [InlineData(@"\\server\share\canopy.json", false)]
        [InlineData("//server/share/canopy.json", false)]
        [InlineData(@"C:\models\canopy.txt", false)]
        [InlineData("", false)]
        public void Only_local_absolute_json_paths_are_read(string path, bool allowed)
        {
            Assert.Equal(allowed, MeshShapeFile.ValidatePath(path) == null);
        }

        [Theory]
        [InlineData(@"\\server\share\canopy.json")]
        [InlineData("C:canopy.json")]
        [InlineData(@"C:\models\canopy.txt")]
        [InlineData("")]
        public void Path_refusals_survive_redaction_unchanged(string path)
        {
            // Error text goes through BakeRedactor, which replaces anything that looks like a path, so a
            // refusal must not quote one or the agent gets a garbled message.
            var reason = MeshShapeFile.ValidatePath(path);
            Assert.Equal(reason, BakeRedactor.RedactForBake(reason));
        }

        [Fact]
        public void Load_reads_materials_and_shapes()
        {
            var path = Path.Combine(Path.GetTempPath(), "mesh-shape-file-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"materials\":[{\"name\":\"Concrete\",\"color\":[200,200,200]}],\"shapes\":[{\"name\":\"Slab\",\"vertices\":[[0,0,0],[1000,0,0],[0,1000,0]],\"faces\":[[0,1,2]]}],\"extra\":1}");
                var spec = MeshShapeFile.Load(path);
                Assert.Single(spec.Materials);
                Assert.Single(spec.Shapes);
                Assert.Equal("Slab", spec.Shapes[0].Name);
            }
            finally { File.Delete(path); }
        }
    }
}
