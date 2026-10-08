using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class SendCodeTools
    {
        [McpServerTool(Name = "revit_send_code_to_revit"), System.ComponentModel.Description("Compile + run C# inside Revit for workflows not covered by typed tools. Variables: doc (Document), uidoc (UIDocument), app (UIApplication) - doc and uidoc are null when no model is open, which is legal: use app.OpenAndActivateDocument(path) to open one, or prefer the typed revit_open_model tool. Write a C# body ending in return; helper type declarations may accompany the body. Or provide a complete public McpDynamicScript with public static object Run(UIApplication app). For transactions/StairsEditScope.Commit, RvtMcp.Plugin.SafeFailuresPreprocessor records/deletes warnings and rolls back errors; report HadWarnings/Messages as committed_with_warnings; inspect HadErrors and commit status before reporting success. Output above 700 KiB auto-spills to a local same-machine file with schema and preview; there is no output parameter. Remote clients receive preview but cannot read the local file. Namespaces: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI.")]
        public static async Task<string> SendCodeToRevit(string code)
            => await ToolGateway.CallAsync("send_code_to_revit", new { code });
    }
}
