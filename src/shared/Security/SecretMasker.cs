using System.Text.RegularExpressions;

namespace Bimwright.Rvt.Plugin
{
    public static class SecretMasker
    {
        private static readonly Regex[] Patterns = new[]
        {
            // OpenAI / Anthropic style: sk-... or sk-ant-...
            new Regex(@"sk-[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled),
            // Bearer tokens
            new Regex(@"(?i)bearer\s+[A-Za-z0-9_\-\.=]+", RegexOptions.Compiled),
            // Authorization header
            new Regex(@"(?i)""authorization""\s*:\s*""[^""]+""", RegexOptions.Compiled),
            // Generic api_key / apikey JSON field
            new Regex(@"(?i)""api[_\-]?key""\s*:\s*""[^""]+""", RegexOptions.Compiled),
            // password JSON field
            new Regex(@"(?i)""password""\s*:\s*""[^""]+""", RegexOptions.Compiled),
        };

        public static string Mask(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var result = input;
            foreach (var rx in Patterns)
                result = rx.Replace(result, "[REDACTED]");
            return result;
        }
    }
}
