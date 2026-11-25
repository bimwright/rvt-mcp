using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>FC-TGT-07: pure classification of the MCP client's AppContainer sandbox.</summary>
    public class ClientSandboxTests
    {
        private const string Normal = @"C:\Users\x\AppData\Local";
        private const string Sandboxed = @"C:\Users\x\AppData\Local\Packages\Vendor.Client_abc123\AC";

        [Fact]
        public void FC_TGT_07_appcontainer_token_alone_is_detected()
        {
            var r = ClientSandbox.Classify(true, Normal);

            Assert.True(r.Detected);
            Assert.Equal("appcontainer", r.Kind);
            Assert.Equal(new[] { "token_is_appcontainer" }, r.Evidence);
        }

        [Fact]
        public void FC_TGT_07_packages_ac_path_alone_is_detected()
        {
            var r = ClientSandbox.Classify(false, Sandboxed);

            Assert.True(r.Detected);
            Assert.Equal("appcontainer", r.Kind);
            Assert.Equal(new[] { "local_app_data_under_packages_ac" }, r.Evidence);
        }

        [Fact]
        public void FC_TGT_07_token_and_path_report_both_evidence_items()
        {
            var r = ClientSandbox.Classify(true, Sandboxed + @"\#!sandbox.{0A1B2C3D-0000-0000-0000-000000000000}");

            Assert.True(r.Detected);
            Assert.Equal(new[] { "token_is_appcontainer", "local_app_data_under_packages_ac" }, r.Evidence);
        }

        [Fact]
        public void FC_TGT_07_neither_signal_is_not_detected()
        {
            var r = ClientSandbox.Classify(false, Normal);

            Assert.False(r.Detected);
            Assert.Null(r.Kind);
            Assert.Empty(r.Evidence);
        }

        [Fact]
        public void FC_TGT_07_unknown_token_with_normal_path_is_not_detected()
        {
            Assert.False(ClientSandbox.Classify(null, Normal).Detected);
            Assert.False(ClientSandbox.Classify(null, null).Detected);
            Assert.False(ClientSandbox.Classify(null, "").Detected);
        }

        [Fact]
        public void FC_TGT_07_unknown_token_with_sandboxed_path_is_detected_by_path()
        {
            var r = ClientSandbox.Classify(null, Sandboxed);

            Assert.True(r.Detected);
            Assert.Equal(new[] { "local_app_data_under_packages_ac" }, r.Evidence);
        }

        [Theory]
        [InlineData(@"C:\Users\x\AppData\Local\Packages\Vendor.Client_abc123\AC\")]
        [InlineData(@"C:\Users\x\AppData\Local\Packages\Vendor.Client_abc123\AC\\")]
        [InlineData("C:/Users/x/AppData/Local/Packages/Vendor.Client_abc123/AC")]
        [InlineData("C:/Users/x/AppData/Local/Packages/Vendor.Client_abc123/AC/")]
        [InlineData(@"c:\users\x\appdata\local\packages\vendor.client_abc123\ac")]
        public void FC_TGT_07_path_rule_tolerates_trailing_separators_forward_slashes_and_case(string path)
        {
            Assert.True(ClientSandbox.IsUnderPackagesAc(path));
        }

        [Theory]
        [InlineData(@"C:\Users\x\AppData\Local")]
        [InlineData(@"C:\Users\x\AppData\Local\Packages\Foo\ACX")]
        [InlineData(@"C:\Users\x\AppData\Local\PackagesX\Foo\AC")]
        [InlineData(@"C:\Users\x\AppData\Local\Packages\Foo\Bar\AC")]
        [InlineData(@"C:\Users\x\AppData\Local\Packages\AC")]
        [InlineData(@"C:\Users\x\AppData\Local\XPackages\Foo\AC")]
        public void FC_TGT_07_lookalike_paths_are_not_detected(string path)
        {
            Assert.False(ClientSandbox.IsUnderPackagesAc(path));
            Assert.False(ClientSandbox.Classify(false, path).Detected);
        }

        [Fact]
        public void FC_TGT_07_token_probe_never_throws()
        {
            var value = ClientSandbox.TokenIsAppContainer();

            // The test runner is not an AppContainer process; off Windows the probe reports unknown.
            Assert.True(value == null || value == false);
        }
    }
}
