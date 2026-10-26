using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ScriptBaselineOutcomeTests
    {
        public static IEnumerable<object[]> AuditCases() => ScriptBaselineFixture.OutcomePairs()
            .Select(pair => new object[] { pair.Value<string>("id")! });

        [Theory]
        [MemberData(nameof(AuditCases))]
        public void Audited_legacy_return_compiles_and_keeps_its_existing_outcome(string id)
        {
            var pair = ScriptBaselineFixture.OutcomePairs().Single(p => p.Value<string>("id") == id);
            var legacy = pair["legacy"]!;
            var returned = RunBody(legacy.Value<string>("code")!);

            Assert.True(JToken.DeepEquals(legacy["return_value"], JToken.FromObject(returned)));
            var outcome = CommandOutcome.Normalize("send_code_to_revit", true,
                new { executed = true, result = returned }, null, null);
            Assert.Equal(legacy.Value<bool>("baseline_success"), outcome.Success);
            Assert.Null(outcome.Error);
            // Script-reported rollback is data; it is not a verified native receipt.
            Assert.Equal("unknown", pair["native_operation"]!.Value<string>("outcome"));
            Assert.Equal(JTokenType.Null, pair["native_operation"]!["mutationApplied"]!.Type);
        }

        [Fact]
        public void Task_rewrites_preserve_details_and_remain_future_acceptance_fixtures()
        {
            var pairs = ScriptBaselineFixture.OutcomePairs();
            Assert.Equal(17, pairs.Count);
            Assert.Equal(17, pairs.Select(p => p.Value<string>("id")).Distinct().Count());
            foreach (var pair in pairs)
            {
                var legacy = pair["legacy"]!;
                var task = pair["task_v1"]!;
                var details = (JObject)legacy["return_value"]!.DeepClone();
                details.Remove("passed");
                details.Remove("error");
                Assert.True(JToken.DeepEquals(details, task["details"]));
                Assert.Equal(legacy["return_value"]!.Value<string>("error"), task.Value<string>("message"));
                Assert.Equal("task_v1", task.Value<string>("result_contract"));
                Assert.False(task["expected"]!.Value<bool>("success"));
                Assert.Equal("failed", task["expected"]!.Value<string>("task_status"));
                Assert.Equal("unknown", task["expected"]!.Value<string>("native_outcome"));
                Assert.StartsWith("return McpTaskResult.Fail(", task.Value<string>("code"));
                Assert.DoesNotContain(CSharpSyntaxTree.ParseText(task.Value<string>("code")!)
                    .GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
            }
            // No fake McpTaskResult implementation is compiled to claim task_v1 works in P00.
        }

        [Fact]
        public void Code_only_body_can_declare_its_own_data_variable()
        {
            Assert.Equal(7, RunBody("var data = 7; return data;"));
        }

        [Theory]
        [InlineData("string", "\"legacy\"", "legacy")]
        [InlineData("int", "7", "7")]
        [InlineData("object", "new { value = 7 }", "{\"value\":7}")]
        public void Legacy_full_source_preserves_return_types(string returnType, string expression, string expected)
        {
            var source = "using Autodesk.Revit.UI; public class McpDynamicScript { public static " +
                         returnType + " Run(UIApplication app) { return " + expression + "; } }";
            Assert.Equal(source, SendCodeSourceBuilder.Build(source));
            var returned = RunSource(source);
            var token = JToken.FromObject(returned);
            Assert.Equal(expected, token is JObject ? token.ToString(Newtonsoft.Json.Formatting.None) : token.ToString());
        }

        private static object RunBody(string code) => RunSource(SendCodeSourceBuilder.Build(code));

        private static object RunSource(string source)
        {
            var assembly = Assembly.Load(ScriptBaselineFixture.Compile(source));
            var app = Activator.CreateInstance(assembly.GetType("Autodesk.Revit.UI.UIApplication")!);
            return assembly.GetType("McpDynamicScript")!.GetMethod("Run")!.Invoke(null, new[] { app })!;
        }
    }
}
