using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace RvtMcp.Tests
{
    public class CommandDispatcherTests
    {
        [Fact]
        public void RegisterBaked_checks_existing_baked_names_before_assignment()
        {
            var repoRoot = GetRepoRoot();
            var sourcePath = Path.Combine(repoRoot, "src", "shared", "Infrastructure", "Execution", "CommandDispatcher.cs");
            var source = File.ReadAllText(sourcePath);

            var duplicateCheck = source.IndexOf("_bakedCommands.ContainsKey(command.Name)", StringComparison.Ordinal);
            var assignment = source.IndexOf("_bakedCommands[command.Name] = command", StringComparison.Ordinal);
            Assert.True(duplicateCheck >= 0, "RegisterBaked must reject an already-live baked command name.");
            Assert.True(duplicateCheck < assignment, "RegisterBaked must check duplicate baked names before assigning.");
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
        }
    }
}
