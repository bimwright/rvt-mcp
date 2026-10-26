using System;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Scripting
{
    public static class ScriptPresentation
    {
        public static string Label(JObject script, JObject operation = null, Func<string, string> translate = null)
        {
            if (script == null) return null;
            var kind = script.Value<string>("execution_kind");
            string Text(string key, string fallback)
            {
                var text = translate?.Invoke(key);
                return string.IsNullOrEmpty(text) || text == key ? fallback : text;
            }
            var label = kind == "compile_only" || kind == "module_compile" ? Text("script.label.compile", "Compile check")
                : kind == "baked_script" ? Text("script.label.baked", "Baked script") : Text("script.label.execute", "Script execute");
            var native = operation?.Value<string>("outcome");
            return label + (native == "partial" || native == "rolled_back" || native == "cancelled"
                ? " · " + Text("script.native." + native, "native: " + native) : "");
        }
    }
}
