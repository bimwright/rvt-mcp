using System.Threading.Tasks;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CreateToolShellTests
    {
        [Fact]
        public async Task Create_mesh_shapes_sends_file_flags_and_default_timeout()
        {
            var sent = await Capture.Send(() => CreateTools.CreateMeshShapes("C:/m/a.json", replace_existing: true, update_materials: false, mesh_only: true));

            Assert.Equal("create_mesh_shapes", sent.Command);
            Assert.Equal(600, sent.TimeoutSeconds);
            var json = sent.Json();
            Assert.Equal("C:/m/a.json", json.Value<string>("file"));
            Assert.True(json.Value<bool>("replace_existing"));
            Assert.False(json.Value<bool>("update_materials"));
            Assert.True(json.Value<bool>("mesh_only"));
        }

        [Fact]
        public async Task Create_mesh_shapes_passes_timeout_seconds()
        {
            var sent = await Capture.Send(() => CreateTools.CreateMeshShapes("C:/m/a.json", timeout_seconds: 900));

            Assert.Equal(900, sent.TimeoutSeconds);
        }

        [Fact]
        public async Task Create_mesh_shapes_rejects_timeout_over_900()
        {
            var text = await CreateTools.CreateMeshShapes("C:/m/a.json", timeout_seconds: 901);

            Assert.Contains("1 and 900", text);
        }
    }
}
