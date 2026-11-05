using System.Threading.Tasks;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class ModifyToolShellTests
    {
        [Fact]
        public async Task Set_wall_top_sends_ids_level_and_offset()
        {
            var sent = await Capture.Send(() => ModifyTools.SetWallTop(new long[] { 11, 12 }, "Roof", 150));

            Assert.Equal("set_wall_top", sent.Command);
            var json = sent.Json();
            Assert.Equal(12, json["wall_ids"].Value<long>(1));
            Assert.Equal("Roof", json.Value<string>("top_level"));
            Assert.Equal(150, json.Value<double>("top_offset"));
        }
    }
}
