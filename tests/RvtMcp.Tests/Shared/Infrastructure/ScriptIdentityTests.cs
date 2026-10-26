using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using Xunit;

namespace RvtMcp.Tests
{
    public class ScriptIdentityTests
    {
        private static JObject Vectors() => JObject.Parse(File.ReadAllText(ScriptJsonTests.Fixture("source-identities-v1.json")));
        private static ScriptModuleManifestEntry Entry(JToken row) => new ScriptModuleManifestEntry(
            row.Value<string>("name")!, row.Value<string>("hash")!, row["requires"]!.Values<string>()!);

        [Fact]
        public void Module_and_ordered_manifest_identities_match_fixed_vectors()
        {
            var vectors = Vectors();
            foreach (var row in vectors["modules"]!)
            {
                var revision = new ScriptModuleRevision(row.Value<string>("name")!, row.Value<string>("source")!, row["requires"]!.Values<string>()!);
                Assert.Equal(row.Value<string>("normalized"), revision.Code);
                Assert.Equal(row.Value<string>("code_sha256"), revision.CodeSha256);
                Assert.Equal(row.Value<string>("revision_hash"), revision.Hash);
            }
            var modules = vectors["modules"]!.ToArray();
            Assert.Equal(modules[2].Value<string>("code_sha256"), modules[3].Value<string>("code_sha256"));
            Assert.NotEqual(modules[2].Value<string>("revision_hash"), modules[3].Value<string>("revision_hash"));
            var manifest = vectors["manifest"]!.Select(Entry).ToArray();
            Assert.Equal(vectors.Value<string>("manifest_hash"), ScriptIdentity.ManifestHash(manifest));
            Assert.NotEqual(ScriptIdentity.ManifestHash(manifest), ScriptIdentity.ManifestHash(manifest.AsEnumerable().Reverse()));
        }

        [Fact]
        public void Script_identity_includes_pinned_source_and_modes_and_excludes_data()
        {
            var vectors = Vectors();
            var manifest = vectors["manifest"]!.Select(Entry).ToArray();
            var code = vectors["script"]!.Value<string>("code")!;
            var first = new ScriptInput(code, ScriptData.FromJson("{\"n\":1}"), resultContract: ScriptModes.TaskV1);
            var second = new ScriptInput(code, ScriptData.FromJson("{\"n\":2}"), resultContract: ScriptModes.TaskV1);
            var key = ScriptIdentity.ScriptKey(first.Code, manifest, first.EntrypointMode, first.ResultContract);
            Assert.Equal(vectors["script"]!.Value<string>("key"), key);
            Assert.Equal(key, ScriptIdentity.ScriptKey(second.Code, manifest, second.EntrypointMode, second.ResultContract));
            Assert.NotEqual(first.Data.Hash, second.Data.Hash);
            Assert.NotEqual(key, ScriptIdentity.ScriptKey(code + " ", manifest, ScriptModes.AppData, ScriptModes.TaskV1));
            Assert.NotEqual(key, ScriptIdentity.ScriptKey(code, manifest, ScriptModes.App, ScriptModes.TaskV1));
            Assert.NotEqual(key, ScriptIdentity.ScriptKey(code, manifest, ScriptModes.AppData, ScriptModes.Legacy));
            var changed = new ScriptModuleRevision("presentation", "public static class Presentation { }  \n", new[] { "base_b", "base_a" });
            manifest[2] = new ScriptModuleManifestEntry(changed.Name, changed.Hash, changed.Requires);
            Assert.NotEqual(key, ScriptIdentity.ScriptKey(code, manifest, ScriptModes.AppData, ScriptModes.TaskV1));
        }

        [Fact]
        public void Source_normalization_changes_only_line_endings_and_never_legacy_hashes()
        {
            const string original = " \treturn 7;  \r\n// next\r";
            const string normalized = " \treturn 7;  \n// next\n";
            Assert.Equal(normalized, ScriptIdentity.NormalizeSource(original));
            Assert.Equal(ScriptIdentity.Sha256(normalized), ScriptIdentity.Sha256(ScriptIdentity.NormalizeSource(original)));
            using var sha1 = SHA1.Create();
            var oldHash = string.Concat(sha1.ComputeHash(Encoding.UTF8.GetBytes(original)).Select(b => b.ToString("x2")));
            Assert.Equal(oldHash, BakeRedactor.HashBody(original));
            Assert.NotEqual(BakeRedactor.HashBody(normalized), oldHash);
            Assert.Equal(40, oldHash.Length);
            Assert.Equal(64, ScriptIdentity.Sha256(normalized).Length);
        }

        [Fact]
        public void Crlf_source_has_one_code_hash_for_metadata_and_script_identity()
        {
            const string crlf = "return 7;\r\n";
            const string lf = "return 7;\n";
            var expected = ScriptIdentity.Sha256(lf);
            var metadata = new ScriptRuntimeMetadata(crlf);
            Assert.Equal(expected, ScriptIdentity.CodeSha256(crlf));
            Assert.Equal(expected, metadata.CodeSha256);
            Assert.Equal(expected, JObject.FromObject(metadata).Value<string>("script_code_sha256"));
            Assert.Equal(ScriptIdentity.ScriptKey(lf, null, ScriptModes.App, ScriptModes.Legacy),
                ScriptIdentity.ScriptKey(crlf, null, ScriptModes.App, ScriptModes.Legacy));
            Assert.False(typeof(ScriptRuntimeMetadata).GetProperty(nameof(ScriptRuntimeMetadata.CodeSha256))!.CanWrite);
        }

        [Theory]
        [InlineData(false, "SCRIPT_INPUT_INVALID")]
        [InlineData(true, "MODULE_CYCLE")]
        public void Dependency_contracts_reject_duplicates_and_direct_self_reference(bool selfReference, string expectedCode)
        {
            var dependencies = selfReference ? new[] { "helper" } : new[] { "a", "a" };
            var hash = new string('a', 64);
            Assert.Equal(expectedCode, Assert.Throws<ScriptContractException>(() =>
                new ScriptModuleRevision("helper", "class Helper {}", dependencies)).ErrorCode);
            Assert.Equal(expectedCode, Assert.Throws<ScriptContractException>(() =>
                new ScriptModuleManifestEntry("helper", hash, dependencies)).ErrorCode);
            Assert.Equal(expectedCode, Assert.Throws<ScriptContractException>(() =>
                ScriptIdentity.ModuleRevisionHash("helper", hash, dependencies)).ErrorCode);
        }

        [Fact]
        public void Multi_module_cycle_detection_remains_the_resolvers_responsibility()
        {
            var a = new ScriptModuleRevision("a", "class A {}", new[] { "b" });
            var b = new ScriptModuleRevision("b", "class B {}", new[] { "a" });
            Assert.Equal("b", Assert.Single(a.Requires));
            Assert.Equal("a", Assert.Single(b.Requires));
        }

        [Fact]
        public void Caller_mutation_cannot_change_a_pinned_revision_or_manifest_entry()
        {
            var dependencies = new[] { "base_a" };
            var revision = new ScriptModuleRevision("presentation", "class Helper {}", dependencies);
            var entry = new ScriptModuleManifestEntry(revision.Name, revision.Hash, dependencies);
            var hash = revision.Hash;
            dependencies[0] = "base_b";
            Assert.Equal("base_a", Assert.Single(revision.Requires));
            Assert.Equal("base_a", Assert.Single(entry.Requires));
            Assert.Equal(hash, revision.Hash);
        }

        [Theory]
        [InlineData("../module")]
        [InlineData("C:\\module")]
        [InlineData("module/name")]
        [InlineData("Upper")]
        [InlineData("con")]
        [InlineData("lpt1")]
        [InlineData("a\n")]
        public void Invalid_module_names_are_rejected(string name)
        {
            Assert.Equal(ScriptErrorCodes.InputInvalid,
                Assert.Throws<ScriptContractException>(() => new ScriptModuleRevision(name, "class Helper {}")).ErrorCode);
        }

        [Fact]
        public void Module_and_manifest_limits_are_checked_independently()
        {
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded,
                Assert.Throws<ScriptContractException>(() => new ScriptModuleRevision("a", new string('x', ScriptLimits.ModuleBytes + 1))).ErrorCode);
            var entry = new ScriptModuleManifestEntry("a", new string('a', 64));
            Assert.Equal(ScriptErrorCodes.InputInvalid,
                Assert.Throws<ScriptContractException>(() => ScriptIdentity.ManifestHash(new[] { entry, entry })).ErrorCode);
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded,
                Assert.Throws<ScriptContractException>(() => ScriptIdentity.ManifestHash(Enumerable.Repeat(entry, 33))).ErrorCode);
        }
    }
}
