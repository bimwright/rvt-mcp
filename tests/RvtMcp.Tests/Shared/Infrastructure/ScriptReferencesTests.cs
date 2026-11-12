using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    // Issue #38: an add-in DLL whose metadata Roslyn cannot read (CLR still loads it) must not break every compile.
    public class ScriptReferencesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtmcp-refs-" + Guid.NewGuid().ToString("N"));

        public ScriptReferencesTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static IEnumerable<string> PlatformPaths() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

        // An unsigned library: its Assembly.PublicKey is nil, which points at #Blob[0].
        private string WriteLibrary(string name)
        {
            var compilation = CSharpCompilation.Create(name,
                new[] { CSharpSyntaxTree.ParseText("namespace " + name + " { public class Gate { } }") },
                PlatformPaths().Select(p => MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var path = Path.Combine(_dir, name + ".dll");
            var emitted = compilation.Emit(path);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            return path;
        }

        // ECMA-335 requires #Blob[0] == 0x00. A non-zero byte breaks System.Reflection.Metadata ("Read out of bounds"),
        // while the CLR treats the nil public key as empty and still loads the file.
        private string WriteTamperedLibrary(string name)
        {
            var path = WriteLibrary(name);
            var bytes = File.ReadAllBytes(path);
            int offset;
            using (var pe = new PEReader(new MemoryStream(bytes)))
                offset = pe.PEHeaders.MetadataStartOffset + pe.GetMetadataReader().GetHeapMetadataOffset(HeapIndex.Blob);
            Assert.Equal(0, bytes[offset]);
            bytes[offset] = 0xCE;
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static Diagnostic[] CompileErrors(IEnumerable<MetadataReference> references)
        {
            var compilation = CSharpCompilation.Create("Script_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText("public static class S { public static int Run() => 1; }") },
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            return compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        }

        [Fact]
        public void TamperedLibrary_ReproducesCs0009_WithPlainReferences()
        {
            var bad = WriteTamperedLibrary("Issue38Tampered");
            var errors = CompileErrors(PlatformPaths().Append(bad).Select(p => MetadataReference.CreateFromFile(p)));
            Assert.Contains(errors, d => d.Id == "CS0009");
        }

        [Fact]
        public void FromPaths_SkipsUnreadableMetadata_AndCompileSucceeds()
        {
            var good = WriteLibrary("Issue38Good");
            var bad = WriteTamperedLibrary("Issue38Tampered");

            var references = ScriptReferences.FromPaths(PlatformPaths().Append(good).Append(bad), out var skipped);

            var skip = Assert.Single(skipped);
            Assert.Equal("Issue38Tampered", skip.Name);
            Assert.Equal(bad, skip.Path);
            Assert.False(string.IsNullOrWhiteSpace(skip.Reason));
            Assert.Contains(references, r => string.Equals(r.Display, good, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(references, r => string.Equals(r.Display, bad, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(CompileErrors(references));
        }

        [Fact]
        public void FromPaths_SkipsMissingOrNonManagedFiles()
        {
            var missing = Path.Combine(_dir, "Missing.dll");
            var native = Path.Combine(_dir, "Native.dll");
            File.WriteAllBytes(native, new byte[] { 0x4D, 0x5A, 0, 0 });

            var references = ScriptReferences.FromPaths(new[] { missing, native }, out var skipped);

            Assert.Empty(references);
            Assert.Equal(new[] { "Missing", "Native" }, skipped.Select(s => s.Name).ToArray());
        }
    }
}
