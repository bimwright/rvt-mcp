using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.ToolCatalog;
using ToolCatalogDto = RvtMcp.ToolCatalog.ToolCatalog;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ToolCatalogTask5Tests
    {
        [Fact]
        public void Builder_uses_active_toolset_registration_and_real_timeout_policies()
        {
            var config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "query", "families", "meta" },
                EnableToolbaker = false
            };

            var catalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);

            Assert.NotEmpty(catalog.Tools);
            Assert.True(catalog.CataloguedBuiltInCount >= catalog.Tools.Count);
            Assert.Contains(catalog.Tools, entry => entry.McpName == "revit_get_current_view_info");
            Assert.DoesNotContain(catalog.Tools, entry => entry.McpName == "revit_create_wall");

            var longRun = Assert.Single(catalog.Tools.Where(entry => entry.McpName == "revit_load_family_from_path"));
            Assert.Equal("long_run_parameter", longRun.Timeout.PolicyKind);
            Assert.Equal(600, longRun.Timeout.ParameterDefaultSeconds);
            Assert.Equal(1, longRun.Timeout.ParameterMinimumSeconds);
            Assert.Equal(900, longRun.Timeout.ParameterMaximumSeconds);
            Assert.Equal(5, longRun.Timeout.TransportGraceSeconds);

            var local = Assert.Single(catalog.Tools.Where(entry => entry.McpName == "revit_list_available_targets"));
            Assert.Equal("server_local", local.Timeout.PolicyKind);
            Assert.Null(local.Timeout.DefaultSeconds);
        }

        [Fact]
        public void Builder_read_only_filter_does_not_expose_write_toolsets()
        {
            var config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "all" },
                ReadOnly = true
            };

            var catalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);

            Assert.NotEmpty(catalog.Tools);
            Assert.DoesNotContain(catalog.Tools, entry => entry.McpName == "revit_create_line_based_element");
            Assert.DoesNotContain(catalog.Tools, entry => entry.McpName == "revit_send_code_to_revit");
            Assert.Contains(catalog.Tools, entry => entry.McpName == "revit_get_current_view_info");
        }

        [Fact]
        public void Codec_accepts_server_catalog_and_round_trips_nullable_server_local_policy()
        {
            var source = new ToolCatalogDto(
                ToolCatalogDto.SupportedSchemaVersion,
                "0.6.4+test",
                3,
                new[]
                {
                    new ToolCatalogEntry(
                        "revit_get_current_view_info",
                        "query",
                        "Read the active view.",
                        null,
                        new TimeoutPolicy(60, null, null, null, 5, "fixed")),
                    new ToolCatalogEntry(
                        "revit_list_available_targets",
                        "meta",
                        "List available Revit targets.",
                        null,
                        new TimeoutPolicy(null, null, null, null, 5, "server_local"))
                });

            var json = JsonConvert.SerializeObject(source, Formatting.None,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Include });
            var result = ToolCatalogCodec.ParseAndValidate(json);

            Assert.True(result.IsValid, result.Error);
            Assert.Equal(2, result.Catalog.Tools.Count);
            Assert.Null(result.Catalog.Tools[1].Timeout.DefaultSeconds);
        }

        [Theory]
        [InlineData("duplicate")]
        [InlineData("too_long")]
        [InlineData("bad_timeout")]
        [InlineData("count_too_small")]
        public void Codec_rejects_invalid_catalog_contracts(string kind)
        {
            var root = new JObject
            {
                ["schema_version"] = ToolCatalogDto.SupportedSchemaVersion,
                ["server_version"] = "0.6.4",
                ["catalogued_built_in_count"] = kind == "count_too_small" ? 0 : 2,
                ["tools"] = new JArray
                {
                    new JObject
                    {
                        ["mcp_name"] = "revit_test",
                        ["toolset"] = "query",
                        ["short_description_en"] = kind == "too_long" ? new string('x', 161) : "Test tool.",
                        ["timeout"] = kind == "bad_timeout"
                            ? new JObject
                            {
                                ["policy_kind"] = "long_run_parameter",
                                ["transport_grace_seconds"] = 5,
                                ["parameter_default_seconds"] = 901,
                                ["parameter_minimum_seconds"] = 1,
                                ["parameter_maximum_seconds"] = 900
                            }
                            : new JObject
                            {
                                ["policy_kind"] = "fixed",
                                ["default_seconds"] = 60,
                                ["transport_grace_seconds"] = 5
                            }
                    }
                }
            };

            if (kind == "duplicate")
                ((JArray)root["tools"]).Add(((JArray)root["tools"])[0].DeepClone());

            var result = ToolCatalogCodec.ParseAndValidate(root);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        [Fact]
        public void Codec_counts_unicode_scalars_and_store_is_connection_scoped()
        {
            var emojiDescription = string.Concat(Enumerable.Repeat("😀", 160));
            var root = new JObject
            {
                ["schema_version"] = ToolCatalogDto.SupportedSchemaVersion,
                ["server_version"] = "0.6.4",
                ["catalogued_built_in_count"] = 1,
                ["tools"] = new JArray
                {
                    new JObject
                    {
                        ["mcp_name"] = "revit_emoji",
                        ["toolset"] = "query",
                        ["short_description_en"] = emojiDescription,
                        ["timeout"] = new JObject
                        {
                            ["policy_kind"] = "fixed",
                            ["default_seconds"] = 60,
                            ["transport_grace_seconds"] = 5
                        }
                    }
                }
            };

            ToolCatalogStore.Clear();
            ToolCatalogStore.BeginConnection();
            Assert.Equal(ToolCatalogStatus.ConnectedNoCatalog, ToolCatalogStore.Status);

            var result = ToolCatalogStore.AcceptJson(root.ToString(Formatting.None));
            Assert.True(result.IsValid, result.Error);
            Assert.Equal(ToolCatalogStatus.Current, ToolCatalogStore.Status);
            Assert.NotNull(ToolCatalogStore.Current);

            ToolCatalogStore.Clear();
            Assert.Equal(ToolCatalogStatus.NotConnected, ToolCatalogStore.Status);
            Assert.Null(ToolCatalogStore.Current);
        }
    }
}
