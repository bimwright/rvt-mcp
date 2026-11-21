using System;
using System.Text;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Short agent name for the toast title from the MCP client's clientInfo.name ("claude-code" -> "Claude").
    /// The client declares the name, so it is display text only: control and bidi characters are dropped and the
    /// result is capped.
    /// </summary>
    internal static class AgentDisplayName
    {
        public const int MaxLength = 20;

        // First match wins, so "cursor-vscode" is Cursor, not VS Code.
        private static readonly string[][] Known =
        {
            new[] { "claude", "Claude" },
            new[] { "codex", "Codex" },
            new[] { "cline", "Cline" },
            new[] { "cursor", "Cursor" },
            new[] { "windsurf", "Windsurf" },
            new[] { "antigravity", "Antigravity" },
            new[] { "gemini", "Gemini" },
            new[] { "copilot", "Copilot" },
            new[] { "visual studio code", "VS Code" },
            new[] { "vscode", "VS Code" },
        };

        public static string For(string clientName)
        {
            var clean = Clean(clientName);
            if (clean == null) return null;
            var lower = clean.ToLowerInvariant();
            foreach (var known in Known)
                if (lower.Contains(known[0])) return known[1];
            return clean.Length <= MaxLength ? clean : clean.Substring(0, MaxLength - 1).TrimEnd() + "…";
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var text = new StringBuilder(Math.Min(value.Length, 128));
            var space = false;
            foreach (var c in value.Length > 128 ? value.Substring(0, 128) : value)
            {
                if (IsFormat(c)) continue;
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = text.Length > 0; continue; }
                if (space) { text.Append(' '); space = false; }
                text.Append(c);
            }
            return text.Length == 0 ? null : text.ToString();
        }

        // Bidi overrides, zero-width and other invisible format characters.
        private static bool IsFormat(char c) => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;
    }
}
