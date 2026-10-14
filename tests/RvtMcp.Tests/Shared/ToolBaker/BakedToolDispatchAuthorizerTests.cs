using System;
using System.IO;
using RvtMcp.Plugin.ToolBaker;
using Xunit;

namespace RvtMcp.Tests
{
    public class BakedToolDispatchAuthorizerTests
    {
        [Fact]
        public void TryAuthorize_RejectsBuiltInNameMissingFromRegistry()
        {
            using var sandbox = new TempDir();
            var registry = new BakedToolRegistry(sandbox.Path);

            var allowed = BakedToolDispatchAuthorizer.TryAuthorize(registry, "show_message", isLoadedBakedCommand: true, out var error);

            Assert.False(allowed);
            Assert.Equal("Baked tool 'show_message' not found. Use list_baked_tools to see available tools.", error);
        }

        [Fact]
        public void TryAuthorize_RejectsBuiltInNamePresentInRegistryButNotLoadedAsBaked()
        {
            using var sandbox = new TempDir();
            var registry = new BakedToolRegistry(sandbox.Path);
            registry.Save(new BakedToolMeta
            {
                Name = "show_message",
                Description = "collision test command",
                ParametersSchema = "{}",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
            }, "source");

            var allowed = BakedToolDispatchAuthorizer.TryAuthorize(
                registry,
                "show_message",
                isLoadedBakedCommand: false,
                out var error);

            Assert.False(allowed);
            Assert.Equal("Baked tool 'show_message' is registered but not loaded. Restart Revit to reload baked tools.", error);
        }

        [Fact]
        public void TryAuthorize_AllowsNamePresentInRegistryAndLoadedAsBaked()
        {
            using var sandbox = new TempDir();
            var registry = new BakedToolRegistry(sandbox.Path);
            registry.Save(new BakedToolMeta
            {
                Name = "custom_baked_command",
                Description = "test command",
                ParametersSchema = "{}",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
            }, "source");

            var allowed = BakedToolDispatchAuthorizer.TryAuthorize(
                registry,
                "custom_baked_command",
                isLoadedBakedCommand: true,
                out var error);

            Assert.True(allowed);
            Assert.Null(error);
        }

        [Fact]
        public void TryAuthorize_RejectsMissingRegistry()
        {
            var allowed = BakedToolDispatchAuthorizer.TryAuthorize(null, "custom_baked_command", isLoadedBakedCommand: true, out var error);

            Assert.False(allowed);
            Assert.Equal("Baked tool 'custom_baked_command' not found. Use list_baked_tools to see available tools.", error);
        }

        [Fact]
        public void TryValidateParameters_RejectsInvalidBakedToolParams()
        {
            var meta = new BakedToolMeta
            {
                Name = "custom_baked_command",
                Description = "test command",
                ParametersSchema = @"{""type"":""object"",""properties"":{""height"":{""type"":""integer""}},""required"":[""height""]}",
            };

            var valid = BakedToolDispatchAuthorizer.TryValidateParameters(meta, @"{""height"":""not-an-int""}", out var error);

            Assert.False(valid);
            Assert.Contains("field 'height' must be integer", error);
        }

        [Fact]
        public void TryValidateParameters_AllowsValidBakedToolParams()
        {
            var meta = new BakedToolMeta
            {
                Name = "custom_baked_command",
                Description = "test command",
                ParametersSchema = @"{""type"":""object"",""properties"":{""height"":{""type"":""integer""}},""required"":[""height""]}",
            };

            var valid = BakedToolDispatchAuthorizer.TryValidateParameters(meta, @"{""height"":3000}", out var error);

            Assert.True(valid);
            Assert.Null(error);
        }

        [Fact]
        public void TryGetCompatWarning_warns_on_untested_revit_version()
        {
            var meta = new BakedToolMeta
            {
                Name = "custom_baked_command",
                CompatMap = @"{""origin"":""2026"",""tested"":{""2026"":{""ok"":true}}}"
            };

            var hasWarning = BakedToolDispatchAuthorizer.TryGetCompatWarning(meta, "2027", out var warning);

            Assert.True(hasWarning);
            Assert.Contains("untested in Revit 2027", warning);
        }

        [Fact]
        public void TryAuthorize_rejects_archived_tool_with_guidance()
        {
            using var sandbox = new TempDir();
            var registry = new BakedToolRegistry(sandbox.Path);
            registry.Save(new BakedToolMeta
            {
                Name = "old_baked_command",
                Description = "archived",
                ParametersSchema = "{}",
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                LifecycleState = "archived"
            }, "source");

            var allowed = BakedToolDispatchAuthorizer.TryAuthorize(
                registry,
                "old_baked_command",
                isLoadedBakedCommand: true,
                out var error);

            Assert.False(allowed);
            Assert.Contains("is archived", error);
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bimwright-baked-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
        }
    }
}
