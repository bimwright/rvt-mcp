using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Tests
{
    internal static class ScriptBaselineFixture
    {
        internal static string Root([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;

        internal static string Read(string name) => File.ReadAllText(Path.Combine(Root(), name));

        internal static JArray OutcomePairs() => JArray.Parse(Read("outcome-pairs.json"));

        // These stand-ins test compiler/storage behavior only, never native Revit APIs.
        private const string Api = @"
namespace Autodesk.Revit.DB { public class Document {} }
namespace Autodesk.Revit.DB.Architecture { public class Stairs {} }
namespace Autodesk.Revit.DB.Electrical { public class CableTray {} }
namespace Autodesk.Revit.DB.Mechanical { public class Duct {} }
namespace Autodesk.Revit.DB.Plumbing { public class Pipe {} }
namespace Autodesk.Revit.DB.Structure { public enum StructuralType { NonStructural, Beam } }
namespace Autodesk.Revit.UI {
    public class UIApplication { public UIDocument ActiveUIDocument => null; }
    public class UIDocument { public Autodesk.Revit.DB.Document Document => null; }
}
namespace RvtMcp.Plugin {
    public interface IRevitCommand {
        string Name { get; }
        string Description { get; }
        string ParametersSchema { get; }
        CommandResult Execute(Autodesk.Revit.UI.UIApplication app, string paramsJson);
    }
    public class CommandResult {
        public bool Success { get; set; }
        public object Data { get; set; }
        public static CommandResult Ok(object data) => new CommandResult { Success = true, Data = data };
    }
}";

        internal static byte[] Compile(string source)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("ScriptBaselineWitness",
                new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Api) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            if (!result.Success)
                throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
            return bytes.ToArray();
        }

        internal static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        internal static byte[] CreateLegacyDatabase(string path)
        {
            var source = Read("legacy-command.txt");
            var dll = Compile(source);
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = Read("legacy-bake-v1.sql");
            command.Parameters.Add("$legacy_dll", SqliteType.Blob).Value = dll;
            command.Parameters.AddWithValue("$legacy_source", source);
            command.ExecuteNonQuery();
            return dll;
        }

        internal static void CopyHistory(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (var source in Directory.GetFiles(Path.Combine(Root(), "history")))
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
            // File.Copy keeps the checkout's write time; the current log stands for today's live file,
            // otherwise daily rotation (keyed on the last write) archives it before the test appends.
            File.SetLastWriteTimeUtc(Path.Combine(directory, "mcp-calls.jsonl"), DateTime.UtcNow);
        }
    }
}
