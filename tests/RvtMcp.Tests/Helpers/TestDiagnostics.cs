using System;
using System.IO;
using System.Runtime.CompilerServices;
using RvtMcp.Plugin;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Gateway and transport tests emit diagnostics. Before any test runs, the default
    /// logger is pointed at a temporary root so no test writes the user's real rvt-mcp.log.
    /// Tests that set their own override restore this one afterwards.
    /// </summary>
    internal static class TestDiagnostics
    {
        internal static readonly string Root = Path.Combine(Path.GetTempPath(), "rvt-test-diagnostics-" + Guid.NewGuid().ToString("N"));

        [ModuleInitializer]
        internal static void Initialize()
        {
            DiagnosticLog.LocalAppDataOverride = Root;
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                DiagnosticLog.Flush(5000);
                try { DeleteTempDirectory(Root, "rvt-test-diagnostics-"); } catch (IOException) { }
            };
        }

        internal static void DeleteTempDirectory(string path, string prefix)
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(path);
            if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete a directory outside the test temp root.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
