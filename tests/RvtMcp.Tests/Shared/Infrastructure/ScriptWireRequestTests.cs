using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;
using Xunit;

namespace RvtMcp.Tests
{
    public class ScriptWireRequestTests
    {
        [Fact]
        public void Wire_round_trip_preserves_input_precision_and_pinned_module_revisions()
        {
            var module = new ScriptModuleRevision("helper", "public class Helper {}\r\n");
            var input = new ScriptInput("return 7;", ScriptData.FromJson("{\"n\":1.000000000000000000001}"), new[] { "helper" }, compileOnly: true);
            var original = new ScriptWireRequest(input, new[] { module });
            var metadata = JObject.FromObject(original.Metadata());
            var roundTrip = ScriptWireRequest.Parse(original.Parameters().ToString(Formatting.None));
            roundTrip.ValidateMetadata(metadata);
            Assert.Equal(input.Data.Json, roundTrip.Input.Data.Json);
            Assert.Equal(module.Hash, Assert.Single(roundTrip.Modules).Hash);
            Assert.True(roundTrip.Input.CompileOnly);
        }

        [Fact]
        public void Changed_module_and_forged_metadata_are_refused()
        {
            var module = new ScriptModuleRevision("helper", "class Helper {}");
            var original = new ScriptWireRequest(new ScriptInput("return 7;", modules: new[] { "helper" }), new[] { module });
            var parameters = original.Parameters();
            parameters["resolved_modules"]![0]!["code"] = "class Helper { public int Value; }";
            Assert.Equal(ScriptErrorCodes.ModuleChanged,
                Assert.Throws<ScriptContractException>(() => ScriptWireRequest.Parse(parameters.ToString())).ErrorCode);
            var metadata = JObject.FromObject(original.Metadata());
            metadata["script_code_sha256"] = new string('0', 64);
            Assert.Equal(ScriptErrorCodes.InputInvalid,
                Assert.Throws<ScriptContractException>(() => original.ValidateMetadata(metadata)).ErrorCode);
        }

        [Theory]
        [InlineData("\"modules\":\"helper\"")]
        [InlineData("\"compile_only\":\"false\"")]
        [InlineData("\"data\":{\"n\":7}")]
        public void Invalid_internal_fields_never_fall_back_to_code_only(string field)
        {
            var raw = "{\"code\":\"return 7;\",\"script_protocol\":1," + field + "}";
            Assert.True(ScriptWireRequest.HasEnhancedOptions(JObject.Parse(raw)));
            Assert.Equal(ScriptErrorCodes.InputInvalid, Assert.Throws<ScriptContractException>(() => ScriptWireRequest.Parse(raw)).ErrorCode);
        }

        [Fact]
        public void Integrated_phase_one_contract_advertises_its_protocol_version()
        {
            Assert.True(ScriptProtocol.IsReady);
            Assert.Equal(1, ScriptProtocol.Describe().Value<int>("version"));
            Assert.Equal("send_code_v2", ScriptProtocol.Describe().Value<string>("capability"));
        }
    }
}
