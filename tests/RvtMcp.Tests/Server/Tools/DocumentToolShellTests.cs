using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class DocumentToolShellTests
    {
        [Fact]
        public async Task State_forwards_scope()
        {
            var sent = await Capture.Send(() => MetaTools.GetDocumentState("all"));
            Assert.Equal("get_document_state", sent.Command);
            Assert.Equal("all", sent.Json().Value<string>("scope"));
        }

        [Fact]
        public async Task Save_forwards_upgrade_consent_and_timeout_separately()
        {
            var sent = await Capture.Send(() => DocumentTools.SaveDocument(true, 900));
            Assert.Equal("save_document", sent.Command);
            Assert.True(sent.Json().Value<bool>("allow_format_upgrade"));
            Assert.Null(sent.Json()["timeout_seconds"]);
            Assert.Equal(900, sent.TimeoutSeconds);
        }

        [Fact]
        public async Task Save_forwards_save_as_path_only_when_given()
        {
            var plain = await Capture.Send(() => DocumentTools.SaveDocument());
            // The gateway drops null arguments, so a normal save sends no save_as_path at all.
            Assert.Null(plain.Json()["save_as_path"]);
            var saveAs = await Capture.Send(() => DocumentTools.SaveDocument(save_as_path: @"C:\Projects\Office.rvt"));
            Assert.Equal(@"C:\Projects\Office.rvt", saveAs.Json().Value<string>("save_as_path"));
        }

        [Fact]
        public async Task Checkpoint_and_manage_forward_their_explicit_arguments()
        {
            var checkpoint = await Capture.Send(() => DocumentTools.SaveCheckpoint("before", "approved", true, true, 42));
            Assert.Equal("save_checkpoint", checkpoint.Command);
            Assert.Equal("before", checkpoint.Json().Value<string>("label"));
            Assert.Equal("approved", checkpoint.Json().Value<string>("reason"));
            Assert.True(checkpoint.Json().Value<bool>("milestone"));
            Assert.True(checkpoint.Json().Value<bool>("allow_format_upgrade"));
            Assert.Equal(42, checkpoint.TimeoutSeconds);

            var manage = await Capture.Send(() => DocumentTools.ManageCheckpoints("prune", 7, false, "plan", null));
            Assert.Equal("manage_checkpoints", manage.Command);
            Assert.Equal(7, manage.Json().Value<int>("keep_last"));
            Assert.False(manage.Json().Value<bool>("dry_run"));
            Assert.Equal("plan", manage.Json().Value<string>("plan_hash"));
            Assert.Equal(600, manage.TimeoutSeconds);
        }

        [Fact]
        public async Task Restore_forwards_its_timeout_separately()
        {
            // Restoring a large model copies the whole file (SPEC r7 O6).
            var sent = await Capture.Send(() => DocumentTools.ManageCheckpoints("restore", checkpoint_id: "20260101-000000", timeout_seconds: 900));
            Assert.Equal("manage_checkpoints", sent.Command);
            Assert.Equal("20260101-000000", sent.Json().Value<string>("checkpoint_id"));
            Assert.Null(sent.Json()["timeout_seconds"]);
            Assert.Equal(900, sent.TimeoutSeconds);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(901)]
        public async Task Invalid_timeout_never_dispatches(int timeout)
        {
            var calls = 0;
            await Capture.WithSendOverride((_, __, ___) => { calls++; return Task.FromResult(new JObject()); }, async () =>
            {
                Assert.StartsWith("Error:", await DocumentTools.SaveDocument(false, timeout));
                Assert.StartsWith("Error:", await DocumentTools.SaveCheckpoint("test", timeout_seconds: timeout));
                Assert.StartsWith("Error:", await DocumentTools.ManageCheckpoints("restore", checkpoint_id: "x", timeout_seconds: timeout));
                Assert.Equal(0, calls);
            });
        }

        [Fact]
        public async Task Read_only_blocks_all_document_writes_before_dispatch()
        {
            var before = ServerState.Config;
            try
            {
                ServerState.Config = new RvtMcpConfig { ReadOnly = true };
                var calls = 0;
                await Capture.WithSendOverride((_, __, ___) => { calls++; return Task.FromResult(new JObject()); }, async () =>
                {
                    Assert.Equal("read_only_mode", JObject.Parse(await DocumentTools.SaveDocument()).Value<string>("error"));
                    Assert.Equal("read_only_mode", JObject.Parse(await DocumentTools.SaveCheckpoint("test")).Value<string>("error"));
                    Assert.Equal("read_only_mode", JObject.Parse(await DocumentTools.ManageCheckpoints("list")).Value<string>("error"));
                    Assert.Equal(0, calls);
                });
            }
            finally { ServerState.Config = before; }
        }
    }
}
