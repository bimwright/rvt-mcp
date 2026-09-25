using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RvtMcp.Server.Prompts
{
    /// <summary>
    /// Loads embedded prompt bodies (resource RvtMcp.Prompts.&lt;name&gt;.md) and renders
    /// them against the active toolset configuration. Spec D2: when the prompt's required
    /// toolsets are missing — or it needs send_code that is not exposed — the renderer
    /// returns a short enable notice INSTEAD of the body, so the agent never guesses.
    /// </summary>
    internal static class PromptBody
    {
        public const string ResourcePrefix = "RvtMcp.Prompts.";

        /// <summary>Returns null when the resource is not embedded. Never throws.</summary>
        public static string Load(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var asm = typeof(PromptBody).Assembly;
                using (var stream = asm.GetManifestResourceStream(ResourcePrefix + name + ".md"))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream))
                        return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        public static HashSet<string> EnabledToolsets()
            => ToolsetFilter.Resolve(ServerState.Config);

        /// <summary>
        /// Gate order: missing toolsets → send_code availability → body with {arg} substituted.
        /// The notices replace the body entirely; a prompt that cannot run is never half-shown.
        /// </summary>
        public static string Render(
            string body,
            string[] requiredToolsets,
            bool requiresSendCode,
            IReadOnlyDictionary<string, string> args = null)
        {
            if (body == null)
                return "This prompt's body was not found in the installed server build.";

            var enabled = EnabledToolsets();
            var missing = (requiredToolsets ?? Array.Empty<string>())
                .Where(t => !enabled.Contains(t))
                .ToArray();

            if (missing.Length > 0)
                return MissingToolsetsNotice(missing, enabled, ServerState.Config?.ReadOnlyOrDefault == true);
            if (requiresSendCode && !Program.IncludeSendCode(enabled, ServerState.Config))
                return SendCodeOffNotice();
            return ApplyArgs(body, args);
        }

        public static string ApplyArgs(string body, IReadOnlyDictionary<string, string> args)
        {
            if (string.IsNullOrEmpty(body) || args == null) return body;
            foreach (var kv in args)
                body = body.Replace("{" + kv.Key + "}", kv.Value ?? string.Empty);
            return body;
        }

        private static string MissingToolsetsNotice(string[] missing, HashSet<string> enabled, bool readOnly)
        {
            var merged = ToolsetFilter.KnownToolsets
                .Where(t => enabled.Contains(t) || missing.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            return
                "This prompt needs toolsets that are not enabled on this server.\n" +
                "Missing: " + string.Join(", ", missing.OrderBy(t => t, StringComparer.Ordinal)) + ".\n" +
                "Add them to the server command line, e.g.:  --toolsets " + string.Join(",", merged) + "\n" +
                "(or --toolsets all), then restart the MCP connection so the client picks up the new tool list." +
                (readOnly && missing.Any(t => ToolsetFilter.WriteCapable.Contains(t, StringComparer.OrdinalIgnoreCase))
                    ? "\nThe server also runs with --read-only, which re-strips the required write-capable toolsets. " +
                      "Keep that protection unless you explicitly authorize enabling those toolsets; only then remove --read-only and restart."
                    : string.Empty);
        }

        private static string SendCodeOffNotice()
        {
            return
                "This prompt works through revit_send_code_to_revit, which is not exposed right now.\n" +
                "send_code is removed when the server runs with --read-only or --disable-toolbaker,\n" +
                "or when neither the 'meta' nor 'toolbaker' toolset is enabled.\n" +
                "Remove those flags or enable the toolset, then restart the MCP connection.";
        }
    }
}
