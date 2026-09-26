using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace RvtMcp.Tests
{
    public class OversizedResponseScopeContractTests
    {
        public static IEnumerable<object[]> ToolScopes()
        {
            yield return Scope("revit_get_selected_elements", "GetSelectedElementsHandler.cs", "startIndex", "maxResults");
            yield return Scope("revit_get_material_quantities", "GetMaterialQuantitiesHandler.cs", "materialNameFilter", "maxResults");
            yield return Scope("revit_get_group_members", "GetGroupMembersHandler.cs", "startIndex", "maxResults");
            yield return Scope("revit_list_assemblies", "ListAssembliesHandler.cs", "maxResults", "maxMembersPerAssembly");
            yield return Scope("revit_get_assembly_members", "GetAssemblyMembersHandler.cs", "startIndex", "maxResults");
            yield return Scope("revit_load_family_from_path", "LoadFamilyFromPathHandler.cs", "includeSymbols", "maxSymbolResults");
            yield return Scope("revit_audit_families", "AuditFamiliesHandler.cs", "startIndex", "limitPerSection");
            yield return Scope("revit_list_family_types_in_family", "ListFamilyTypesInFamilyHandler.cs", "maxTypes", "parameterNames");
            yield return Scope("revit_color_elements", "ColorElementsHandler.cs", "maxGroups");
            yield return Scope("revit_analyze_sheet_layout", "AnalyzeSheetLayoutHandler.cs", "startViewport", "maxViewports");
            yield return Scope("revit_list_export_settings", "ListExportSettingsHandler.cs", "kindFilter", "maxResults");
            yield return Scope("revit_get_print_settings", "GetPrintSettingsHandler.cs", "kindFilter", "maxResults");
            yield return Scope("revit_detect_system_elements", "DetectSystemElementsHandler.cs", "startElement", "maxElements");
            yield return Scope("revit_get_panel_schedule", "GetPanelScheduleHandler.cs", "startCircuit", "maxCircuits");
            yield return Scope("revit_show_message", "ShowMessageHandler.cs", "echoMessage", "maxEchoChars");
            yield return Scope("revit_list_baked_tools", "ListBakedToolsHandler.cs", "nameFilter", "limit");
            yield return Scope("revit_analyze_view_naming_patterns", "AnalyzeViewNamingPatternsHandler.cs", "maxPatterns", "startOutlier", "maxOutliers");
            yield return Scope("revit_tag_all_areas", "TagAllAreasHandler.cs", "limit");
            yield return Scope("revit_delete_view_template", "DeleteViewTemplateHandler.cs", "maxUsedByViews");
            yield return Scope("revit_save_selection", "SaveSelectionHandler.cs", "includeElementIds", "maxElementIdResults");
            yield return Scope("revit_load_selection", "LoadSelectionHandler.cs", "startIndex", "maxResults");
        }

        [Theory]
        [MemberData(nameof(ToolScopes))]
        public void Group3_tool_exposes_approved_scope_in_server_and_handler(
            string toolName,
            string handlerFile,
            string[] mcpParameters)
        {
            var root = GetRepoRoot();
            var program = File.ReadAllText(Path.Combine(root, "src", "server", "Program.cs"));
            var marker = "Name = \"" + toolName + "\"";
            var markerIndex = program.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(markerIndex >= 0, "Missing tool marker: " + toolName);
            var toolBlock = program.Substring(markerIndex, Math.Min(1800, program.Length - markerIndex));

            var handler = File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", handlerFile));
            foreach (var parameter in mcpParameters)
            {
                Assert.Contains(parameter, toolBlock);
                Assert.Contains(ToWireName(parameter), handler);
            }
        }

        [Fact]
        public void Every_approved_group2_tool_has_a_command_specific_recovery_hint()
        {
            var root = GetRepoRoot();
            // Keep the approved command baseline independent of maintainer research notes.
            var group2Tools = File.ReadAllLines(Path.Combine(
                root, "tests", "RvtMcp.Tests", "Golden", "response-size-scoped-commands.txt"));
            var catalog = File.ReadAllText(Path.Combine(root, "src", "shared", "Infrastructure", "ResponseSizePolicyCatalog.cs"));

            Assert.Equal(97, group2Tools.Length);
            Assert.Equal(group2Tools.Length, group2Tools.Distinct(StringComparer.Ordinal).Count());
            Assert.All(group2Tools, command => Assert.False(string.IsNullOrWhiteSpace(command)));
            foreach (var command in group2Tools)
                Assert.Contains("[\"" + command + "\"]", catalog);
        }

        [Theory]
        [InlineData("AiElementFilterHandler.cs")]
        [InlineData("GetScheduleDataHandler.cs")]
        [InlineData("FindScheduleElementsHandler.cs")]
        [InlineData("PurgeUnusedHandler.cs")]
        [InlineData("ListRebarHandler.cs")]
        [InlineData("GetStructuralLoadsHandler.cs")]
        [InlineData("AnalyzeStructuralConnectionsHandler.cs")]
        [InlineData("GetModelWarningsSummaryHandler.cs")]
        [InlineData("QueryKeiDatabaseHandler.cs")]
        public void Existing_limit_scope_declares_a_schema_hard_maximum(string handlerFile)
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", "shared", "Handlers", handlerFile));
            Assert.Contains("maximum", source);
        }

        [Theory]
        [InlineData("ComputeRoomFinishesHandler.cs")]
        [InlineData("ExportRoomDataHandler.cs")]
        [InlineData("GetMaterialTakeoffHandler.cs")]
        [InlineData("WorkflowTakeoffReportHandler.cs")]
        [InlineData("BatchExecuteHandler.cs")]
        [InlineData("ExportSharedParameterFileHandler.cs")]
        [InlineData("RunBakedToolHandler.cs")]
        [InlineData("WorkflowDataRoundtripHandler.cs")]
        public void Approved_group4_handler_schemas_accept_only_inline_or_file_output(string handlerFile)
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", "shared", "Handlers", handlerFile));
            var outputIndex = source.IndexOf("\"\"output\"\"", StringComparison.Ordinal);
            Assert.True(outputIndex >= 0, "Missing output schema property in " + handlerFile);
            var outputSchema = source.Substring(outputIndex, Math.Min(300, source.Length - outputIndex));
            Assert.Contains("\"\"enum\"\"", outputSchema);
            Assert.Contains("\"\"inline\"\"", outputSchema);
            Assert.Contains("\"\"file\"\"", outputSchema);
        }

        [Fact]
        public void Adaptive_suggestion_list_exposes_state_and_bounded_paging()
        {
            var root = GetRepoRoot();
            var program = File.ReadAllText(Path.Combine(root, "src", "server", "Program.cs"));
            var handler = File.ReadAllText(Path.Combine(root, "src", "server", "Handlers", "ListBakeSuggestionsHandler.cs"));

            Assert.Contains("ListBakeSuggestions(string state", program);
            Assert.Contains("int startIndex", program);
            Assert.Contains("int limit", program);
            Assert.Contains("hard maximum", handler);
        }

        [Fact]
        public void Event_boundary_processes_file_and_auto_spill_before_response_guarding()
        {
            var eventHandler = File.ReadAllText(Path.Combine(
                GetRepoRoot(), "src", "shared", "Infrastructure", "McpEventHandler.cs"));
            var spillIndex = eventHandler.IndexOf("ResponseSpillProcessor", StringComparison.Ordinal);
            var guardIndex = eventHandler.IndexOf("ResponseSizeGuard.Evaluate", StringComparison.Ordinal);

            Assert.True(spillIndex >= 0, "McpEventHandler must invoke ResponseSpillProcessor.");
            Assert.True(guardIndex > spillIndex, "Spill must replace bulk data before the response-size guard runs.");
            Assert.Contains("result.Data = spillOutcome.Data", eventHandler);
        }

        [Fact]
        public void Completed_oversized_mutation_is_compacted_without_false_failure()
        {
            var root = GetRepoRoot();
            var eventHandler = File.ReadAllText(Path.Combine(root, "src", "shared", "Infrastructure", "McpEventHandler.cs"));
            var server = File.ReadAllText(Path.Combine(root, "src", "server", "Program.cs"));

            Assert.Contains("mutationCompleted", eventHandler);
            Assert.Contains("MutationResponseCompactor.Compact", eventHandler);
            Assert.Contains("success = true", eventHandler);
            Assert.Contains("data[\"_response_warning\"]", server);
        }

        [Fact]
        public void Apply_bake_omits_source_and_dll_bodies_from_plugin_response()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", "shared", "Handlers", "ApplyBakeSuggestionHandler.cs"));
            var responseStart = source.IndexOf("return CommandResult.Ok(new", StringComparison.Ordinal);
            Assert.True(responseStart >= 0);
            var response = source.Substring(responseStart, Math.Min(1000, source.Length - responseStart));

            Assert.Contains("source_code_sha256", response);
            Assert.Contains("source_code_byte_count", response);
            Assert.Contains("dll_sha256", response);
            Assert.Contains("dll_byte_count", response);
            Assert.DoesNotContain("dll_base64 =", response);
        }

        private static object[] Scope(string toolName, string handlerFile, params string[] parameters)
        {
            return new object[] { toolName, handlerFile, parameters };
        }

        private static string ToWireName(string name)
        {
            var chars = new List<char>();
            foreach (var ch in name)
            {
                if (char.IsUpper(ch))
                {
                    chars.Add('_');
                    chars.Add(char.ToLowerInvariant(ch));
                }
                else
                {
                    chars.Add(ch);
                }
            }
            return new string(chars.ToArray());
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
