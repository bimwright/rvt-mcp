using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Runs RoslynBridge without loading Roslyn into the default load context (issue #68). Another add-in
    /// such as pyRevit can load a different Microsoft.CodeAnalysis there first, and the default context holds
    /// one version per assembly name. On .NET 8/10 the bridge therefore runs from a second copy of this
    /// assembly in an isolated context next to the Roslyn that ships with it. .NET Framework has no load
    /// contexts, so the 2022-2024 shells call the bridge directly as before.
    /// </summary>
    internal static class RoslynHost
    {
        internal const string CompilerAssembly = "Microsoft.CodeAnalysis";
        private static readonly object Gate = new object();
        private static Type _bridge;

        internal static ScriptCompileResult CompileScript(ScriptInput input, IReadOnlyList<ScriptModuleRevision> modules,
            string[] referencePaths, Action compileStarting, out SkippedScriptReference[] skipped)
        {
            var request = new JObject
            {
                ["code"] = input.Code,
                ["data"] = input.Data.Json,
                ["modules"] = new JArray(input.Modules),
                ["compile_only"] = input.CompileOnly,
                ["result_contract"] = input.ResultContract,
                ["usage_context"] = input.UsageContext,
                ["resolved"] = new JArray((modules ?? Array.Empty<ScriptModuleRevision>()).Select(m =>
                    new JObject { ["name"] = m.Name, ["code"] = m.Code, ["requires"] = new JArray(m.Requires) }))
            };
            var reply = Call<ScriptCompileReply>(nameof(RoslynBridge.CompileScript), request.ToString(Formatting.None), referencePaths, compileStarting);
            skipped = reply.Skipped ?? Array.Empty<SkippedScriptReference>();
            if (reply.ContractCode != null) throw new ScriptContractException(reply.ContractCode, reply.ContractMessage);
            return reply.Result;
        }

        internal static RoslynCompileOutput CompileLegacy(string code, string[] referencePaths)
            => Call<RoslynCompileOutput>(nameof(RoslynBridge.CompileLegacy), code, referencePaths);

        internal static RoslynCompileOutput CompileTool(string sourceCode, string[] referencePaths)
            => Call<RoslynCompileOutput>(nameof(RoslynBridge.CompileTool), sourceCode, referencePaths);

        /// <summary>A failure to load the compiler as SCRIPT_COMPILER_UNAVAILABLE, or null for any other exception.</summary>
        internal static ScriptContractException CompilerUnavailable(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var file = (e as FileLoadException)?.FileName ?? (e as FileNotFoundException)?.FileName;
                if (file != null && file.StartsWith(CompilerAssembly, StringComparison.OrdinalIgnoreCase))
                    return new ScriptContractException(ScriptErrorCodes.CompilerUnavailable, DescribeUnavailable(e.Message,
                        AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.GetName().Name == CompilerAssembly
                            ? Tuple.Create(a.GetName().Version, a.Location) : null).Where(a => a != null),
                        Path.GetDirectoryName(typeof(RoslynHost).Assembly.Location)));
            }
            return null;
        }

        internal static string DescribeUnavailable(string detail, IEnumerable<Tuple<Version, string>> loadedCompilers, string ownDirectory)
        {
            var others = loadedCompilers.Where(c => !string.IsNullOrEmpty(c.Item2) &&
                !string.Equals(Path.GetDirectoryName(c.Item2), ownDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
            return "rvt-mcp could not load its C# compiler. " +
                (others.Length == 0 ? "" : "Another Revit add-in already loaded " +
                    string.Join("; ", others.Select(c => CompilerAssembly + " " + c.Item1 + " from " + AddinFolder(Path.GetDirectoryName(c.Item2)))) + ". ") +
                "This is an add-in conflict inside Revit, not a script error: do not rewrite the script; report it to the user. Detail: " + detail;
        }

        /// <summary>
        /// The last three folders, joined by '/', without the drive or Users\&lt;name&gt; (for example
        /// pyRevit-Master/bin/netcore). Response redaction cuts a full Windows path down to its last folder,
        /// which would hide the add-in's name.
        /// </summary>
        internal static string AddinFolder(string directory)
        {
            var parts = (directory ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.EndsWith(":", StringComparison.Ordinal)).ToList();
            if (parts.Count >= 2 && string.Equals(parts[0], "Users", StringComparison.OrdinalIgnoreCase)) parts.RemoveRange(0, 2);
            return string.Join("/", parts.Skip(Math.Max(0, parts.Count - 3)));
        }

        private static T Call<T>(string method, params object[] arguments)
        {
            try
            {
                var json = (string)Bridge().GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments);
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                var unavailable = CompilerUnavailable(inner);
                if (unavailable != null) throw unavailable;
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }

        private static Type Bridge()
        {
            lock (Gate)
            {
                if (_bridge != null) return _bridge;
#if NETFRAMEWORK
                _bridge = typeof(RoslynBridge);
#else
                var self = typeof(RoslynHost).Assembly;
                _bridge = string.IsNullOrEmpty(self.Location) ? typeof(RoslynBridge)
                    : new RoslynLoadContext(Path.GetDirectoryName(self.Location), self.GetName().Name)
                        .LoadFromAssemblyName(self.GetName()).GetType(typeof(RoslynBridge).FullName, true);
#endif
                return _bridge;
            }
        }
    }

#if !NETFRAMEWORK
    /// <summary>Loads the bridge assembly and Microsoft.CodeAnalysis* from the add-in folder; everything else is shared with the default context.</summary>
    internal sealed class RoslynLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;
        private readonly string _bridgeAssembly;

        internal RoslynLoadContext(string directory, string bridgeAssembly) : base("RvtMcp.Roslyn")
        {
            _directory = directory;
            _bridgeAssembly = bridgeAssembly;
        }

        protected override Assembly Load(AssemblyName name)
        {
            if (!string.Equals(name.Name, _bridgeAssembly, StringComparison.Ordinal) &&
                !name.Name.StartsWith(RoslynHost.CompilerAssembly, StringComparison.Ordinal)) return null;
            var path = Path.Combine(_directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
#endif
}
