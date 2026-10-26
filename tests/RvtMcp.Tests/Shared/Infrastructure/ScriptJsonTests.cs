using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;
using Xunit;

namespace RvtMcp.Tests
{
    public class ScriptJsonTests
    {
        internal static string Fixture(string name, [CallerFilePath] string source = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "fixtures", "ScriptIdentity", name));

        public static IEnumerable<object[]> Vectors() => JArray.Parse(File.ReadAllText(Fixture("canonical-json-v1.json")))
            .Select(v => new object[] { v.Value<string>("id")!, v.Value<string>("input")!, v.Value<string>("canonical")!, v.Value<string>("sha256")! });

        [Theory]
        [MemberData(nameof(Vectors))]
        public void Canonical_json_matches_independent_fixed_vectors(string id, string input, string canonical, string hash)
        {
            var parsed = ScriptJson.Parse(input);
            Assert.True(canonical == parsed.CanonicalJson, id + ": canonical text mismatch.");
            Assert.Equal(hash, ScriptIdentity.CanonicalHash(input));
            Assert.Equal(canonical, ScriptJson.Parse(parsed.CompactJson).CanonicalJson);
        }

        [Theory]
        [InlineData("{\"a\":1,\"a\":2}")]
        [InlineData("{\"a\":1,\"\\u0061\":2}")]
        [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
        [InlineData("{'a':1}")]
        [InlineData("{\"a\":1,}")]
        [InlineData("[1,]")]
        [InlineData("{\"n\":NaN}")]
        [InlineData("{\"n\":Infinity}")]
        [InlineData("{\"n\":01}")]
        [InlineData("{\"n\":+1}")]
        [InlineData("{\"n\":1.}")]
        [InlineData("{\"n\":1e+}")]
        [InlineData("{\"n\":.1}")]
        [InlineData("/*comment*/{}")]
        [InlineData("{} trailing")]
        [InlineData("{\"a\":\"\\x20\"}")]
        [InlineData("{\"a\":\"\\u000g\"}")]
        [InlineData("{\"a\":\"line\nbreak\"}")]
        [InlineData("")]
        public void Invalid_or_duplicate_json_is_rejected_before_identity(string input)
        {
            Assert.Equal(ScriptErrorCodes.InputInvalid, Assert.Throws<ScriptContractException>(() => ScriptJson.Parse(input)).ErrorCode);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("\"{\\\"a\\\":1}\"")]
        public void Data_does_not_coerce_arrays_scalars_or_json_strings_to_objects(string json)
        {
            Assert.Equal(ScriptErrorCodes.InputInvalid, Assert.Throws<ScriptContractException>(() => ScriptData.FromJson(json)).ErrorCode);
        }

        [Fact]
        public void Absent_null_and_empty_object_have_distinct_presence()
        {
            Assert.Same(ScriptData.Absent, ScriptData.FromJson(null));
            Assert.Same(ScriptData.Absent, ScriptData.FromJson(" null "));
            var empty = ScriptData.FromJson(" {} ");
            Assert.True(empty.IsProvided);
            Assert.Equal("{}", empty.Json);
            Assert.Equal(2, empty.ByteCount);
            Assert.NotNull(empty.Hash);
            Assert.Null(ScriptData.Absent.Json);
            Assert.Null(ScriptData.Absent.Hash);
        }

        [Fact]
        public void Data_preserves_numeric_lexemes_for_dispatch_and_exact_identity_for_hashing()
        {
            var data = ScriptData.FromJson(" { \"n\": 9007199254740993, \"x\": 1.00e0 } ");
            Assert.Equal("{\"n\":9007199254740993,\"x\":1.00e0}", data.Json);
            Assert.Equal("{\"n\":9007199254740993e0,\"x\":1e0}", data.CanonicalJson);
            Assert.NotEqual(data.Hash, ScriptData.FromJson("{\"n\":9007199254740992,\"x\":1}").Hash);
        }

        [Fact]
        public void Nested_duplicate_keys_are_scoped_to_each_object_and_depth_is_bounded()
        {
            Assert.NotNull(ScriptData.FromJson("{\"a\":{\"x\":1},\"b\":{\"x\":2}}"));
            string Nested(int count) => string.Concat(Enumerable.Repeat("{\"x\":", count)) + "0" + new string('}', count);
            Assert.NotNull(ScriptData.FromJson(Nested(64)));
            Assert.Equal(ScriptErrorCodes.InputInvalid,
                Assert.Throws<ScriptContractException>(() => ScriptData.FromJson(Nested(65))).ErrorCode);
        }

        [Fact]
        public void Data_budget_measures_compact_utf8_bytes_at_the_exact_boundary()
        {
            var overhead = Encoding.UTF8.GetByteCount("{\"v\":\"\"}");
            var payload = new string('ệ', (ScriptLimits.DataBytes - overhead) / 3);
            payload += new string('x', ScriptLimits.DataBytes - overhead - Encoding.UTF8.GetByteCount(payload));
            var json = "{\"v\":\"" + payload + "\"}";
            Assert.Equal(ScriptLimits.DataBytes, ScriptData.FromJson(json).ByteCount);
            Assert.Equal(ScriptErrorCodes.DataTooLarge,
                Assert.Throws<ScriptContractException>(() => ScriptData.FromJson("{\"v\":\"" + payload + "x\"}")).ErrorCode);
        }

        [Fact]
        public void Raw_data_guard_runs_before_parsing_and_allows_bounded_whitespace()
        {
            var atLimit = new string(' ', ScriptLimits.RawDataBytes - 2) + "{}";
            Assert.Equal("{}", ScriptData.FromJson(atLimit).Json);
            var tooLong = new string(' ', ScriptLimits.RawDataBytes + 1) + "invalid-json";
            Assert.Equal(ScriptErrorCodes.DataTooLarge,
                Assert.Throws<ScriptContractException>(() => ScriptData.FromJson(tooLong)).ErrorCode);
            var unicodeTooLarge = new string('ệ', ScriptLimits.RawDataBytes / 3 + 1);
            Assert.Equal(ScriptErrorCodes.DataTooLarge,
                Assert.Throws<ScriptContractException>(() => ScriptData.FromJson(unicodeTooLarge)).ErrorCode);
        }

        [Fact]
        public void Compact_data_over_budget_rejects_a_large_exponent()
        {
            var json = "{\"n\":1e" + new string('9', ScriptLimits.DataBytes) + "}";
            Assert.True(json.Length < ScriptLimits.RawDataBytes);
            Assert.Equal(ScriptErrorCodes.DataTooLarge,
                Assert.Throws<ScriptContractException>(() => ScriptData.FromJson(json)).ErrorCode);
        }

        [Theory]
        [InlineData("{\"n\":1e400}")]
        [InlineData("{\"n\":-1.7976931348623159e308}")]
        [InlineData("{\"a\":[0.5,{\"n\":1E309}]}")]
        public void Data_rejects_fractions_and_exponents_outside_a_finite_double(string json)
        {
            Assert.Equal(ScriptErrorCodes.InputInvalid,
                Assert.Throws<ScriptContractException>(() => ScriptData.FromJson(json)).ErrorCode);
            Assert.NotNull(ScriptIdentity.CanonicalHash(json));
        }

        [Fact]
        public void Data_keeps_exact_large_integers_and_finite_double_edges()
        {
            var data = ScriptData.FromJson("{\"n\":123456789012345678901234567890,\"tiny\":1e-400,\"max\":1.7976931348623157e308}");
            Assert.Equal("{\"n\":123456789012345678901234567890,\"tiny\":1e-400,\"max\":1.7976931348623157e308}", data.Json);
        }

        [Fact]
        public void Escape_decoding_preserves_strings_without_unicode_normalization()
        {
            Assert.Equal("{\"s\":\"\\ud800\"}", ScriptJson.Parse("{\"s\":\"\\ud800\"}").CanonicalJson);
            Assert.Equal(ScriptIdentity.CanonicalHash("{\"s\":\"/\"}"), ScriptIdentity.CanonicalHash("{\"s\":\"\\/\"}"));
            Assert.NotEqual(ScriptIdentity.CanonicalHash("{\"s\":\"é\"}"), ScriptIdentity.CanonicalHash("{\"s\":\"e\\u0301\"}"));
        }
    }
}
