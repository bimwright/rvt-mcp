using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    internal static class ScriptCompileHost
    {
        internal static JObject Capture(int year)
        {
            using (var process = Process.GetCurrentProcess())
                return new JObject { ["host_year"] = year, ["process_id"] = process.Id,
                    ["runtime_identity"] = "CLR " + Environment.Version + "; " + typeof(object).Assembly.FullName,
                    ["identity_basis"] = "loaded_runtime" };
        }
    }
}
