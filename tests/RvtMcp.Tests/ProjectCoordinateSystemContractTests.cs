using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class ProjectCoordinateSystemContractTests
    {
        [Fact]
        public void Tool_is_parameterless_read_only_and_idempotent()
        {
            var tool = typeof(ToolsetFilter).Assembly
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(method => new
                {
                    Method = method,
                    Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
                })
                .Single(item => item.Attribute?.Name == "revit_get_project_coordinate_system");

            Assert.Empty(tool.Method.GetParameters());
            Assert.True(tool.Attribute!.ReadOnly);
            Assert.True(tool.Attribute.Idempotent);
        }

        [Fact]
        public void Handler_is_registered_and_reads_all_project_coordinate_references()
        {
            var root = GetRepoRoot();
            var dispatcher = File.ReadAllText(Path.Combine(root, "src", "shared", "Infrastructure", "CommandDispatcher.cs"));
            var handler = File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", "GetProjectCoordinateSystemHandler.cs"));

            Assert.Contains("Register(new Handlers.GetProjectCoordinateSystemHandler())", dispatcher, StringComparison.Ordinal);
            Assert.Contains("InternalOrigin.Get(doc)", handler, StringComparison.Ordinal);
            Assert.Contains("BasePoint.GetProjectBasePoint(doc)", handler, StringComparison.Ordinal);
            Assert.Contains("BasePoint.GetSurveyPoint(doc)", handler, StringComparison.Ordinal);
            Assert.Contains("doc.ProjectLocations", handler, StringComparison.Ordinal);
            Assert.Contains("GetProjectPosition", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("new Transaction(", handler, StringComparison.Ordinal);
        }

        [Fact]
        public void Link_coordinate_tool_is_read_only_and_accepts_one_link_instance_id()
        {
            var tool = typeof(ToolsetFilter).Assembly
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(method => new
                {
                    Method = method,
                    Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
                })
                .Single(item => item.Attribute?.Name == "revit_get_link_coordinate_system");

            var parameter = Assert.Single(tool.Method.GetParameters());
            Assert.Equal("linkInstanceId", parameter.Name);
            Assert.Equal(typeof(long), parameter.ParameterType);
            Assert.True(tool.Attribute!.ReadOnly);
            Assert.True(tool.Attribute.Idempotent);
        }

        [Fact]
        public void Link_coordinate_handler_is_registered_and_covers_revit_and_cad_links_without_mutation()
        {
            var root = GetRepoRoot();
            var dispatcher = File.ReadAllText(Path.Combine(root, "src", "shared", "Infrastructure", "CommandDispatcher.cs"));
            var handler = File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", "GetLinkCoordinateSystemHandler.cs"));

            Assert.Contains("Register(new Handlers.GetLinkCoordinateSystemHandler())", dispatcher, StringComparison.Ordinal);
            Assert.Contains("element as RevitLinkInstance", handler, StringComparison.Ordinal);
            Assert.Contains("element as ImportInstance", handler, StringComparison.Ordinal);
            Assert.Contains("GetLinkDocument()", handler, StringComparison.Ordinal);
            Assert.Contains("BuildCoordinateSystemDto(linkDoc)", handler, StringComparison.Ordinal);
            Assert.Contains("GetTotalTransform()", handler, StringComparison.Ordinal);
            Assert.Contains("linked_project_base_point", handler, StringComparison.Ordinal);
            Assert.Contains("linked_survey_point", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("new Transaction(", handler, StringComparison.Ordinal);
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
