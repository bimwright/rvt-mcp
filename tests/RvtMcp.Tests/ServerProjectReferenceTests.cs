using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace RvtMcp.Tests
{
    public class ServerProjectReferenceTests
    {
        // `dotnet build src/RvtMcp.sln` builds RvtMcp.Server twice: as a solution project and
        // through this test project's reference, whose AdditionalProperties make it a separate
        // MSBuild instance. Parallel builds run both at once, so sharing an obj directory makes
        // them collide on its files (MSB3371).
        [Fact]
        public void Test_server_instance_does_not_share_the_solution_instance_obj_directory()
        {
            var root = GetRepoRoot();
            var server = Path.Combine(root, "src", "server", "RvtMcp.Server.csproj");
            var tests = Path.Combine(root, "tests", "RvtMcp.Tests", "RvtMcp.Tests.csproj");

            var solutionObj = IntermediateDirectory(server, new Dictionary<string, string>());
            var testObj = IntermediateDirectory(server, ServerReferenceProperties(tests));

            Assert.NotEqual(solutionObj, testObj, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> ServerReferenceProperties(string testsProject)
        {
            using var json = JsonDocument.Parse(MsBuild(testsProject, "-getItem:ProjectReference"));
            var reference = json.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
                .Single(item => item.GetProperty("Identity").GetString()!
                    .EndsWith("RvtMcp.Server.csproj", StringComparison.OrdinalIgnoreCase));
            return reference.GetProperty("AdditionalProperties").GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim());
        }

        private static string IntermediateDirectory(string project, Dictionary<string, string> globalProperties)
        {
            var args = globalProperties.Select(p => $"-p:{p.Key}={p.Value}")
                .Append("-getProperty:IntermediateOutputPath").ToArray();
            var path = MsBuild(project, args).Trim();
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, path)).TrimEnd('\\', '/');
        }

        private static string MsBuild(string project, params string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("msbuild");
            start.ArgumentList.Add(project);
            start.ArgumentList.Add("-p:Configuration=Debug");
            foreach (var arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "dotnet msbuild timed out");
            Assert.True(process.ExitCode == 0, $"dotnet msbuild failed: {stderr.Result}{stdout.Result}");
            return stdout.Result;
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
