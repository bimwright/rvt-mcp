using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server.Bake;

namespace RvtMcp.Server
{
    internal static class CodeModuleToolRegistration
    {
        internal static readonly Type[] Definitions = {typeof(CodeModuleReadTools), typeof(CodeModuleWriteTools)};
        internal static IEnumerable<Type> Resolve(HashSet<string> enabled, RvtMcpConfig config, bool ready)
        {
            if (!ready || !(enabled.Contains("meta") || enabled.Contains("toolbaker"))) yield break;
            yield return typeof(CodeModuleReadTools);
            if (config?.ReadOnlyOrDefault != true && config?.EnableSendCodeOrDefault != false) yield return typeof(CodeModuleWriteTools);
        }
    }

    internal static class CodeModuleTools
    {
        internal static Func<BakeDb> StoreFactory = () => new BakeDb(new BakePaths());
        internal static CodeModuleToolCore Core(BakeDb db) => new CodeModuleToolCore(new CodeModuleStore(db));
        internal static string Error(Exception error)
        {
            var message = error is ScriptContractException contract ? contract.ErrorCode + ": " + contract.Message : error.Message;
            var result = new JObject { ["success"] = false, ["error"] = McpResponsePrivacy.RedactErrorForResponse(message) };
            if (error is ModuleValidationException validation && validation.Receipt != null)
            {
                result["compile_receipt"] = new JObject(); SafetyResponseMetadata.Attach((JObject)result["compile_receipt"], validation.Receipt);
                ToolGateway.CarryOnce(validation.Receipt, result);
            }
            return result.ToString(Formatting.None);
        }
    }

    [McpServerToolType, Toolset("meta")]
    public sealed class CodeModuleReadTools
    {
        [McpServerTool(Name="revit_list_code_modules", ReadOnly=true, Destructive=false, Idempotent=true, OpenWorld=false), System.ComponentModel.Description(
            "List stored C# helper modules without Revit. Source is omitted unless include_code=true. Page with start_index and limit (1-500). Includes dependency and scoped compile metadata.")]
        public static string ListCodeModules(string name_filter="", bool include_code=false, int start_index=0, int limit=100)
        {
            try { using var db=CodeModuleTools.StoreFactory(); return CodeModuleTools.Core(db).List(name_filter,include_code,start_index,limit).ToString(Formatting.None); }
            catch(Exception ex){return CodeModuleTools.Error(ex);}
        }
    }

    [McpServerToolType, Toolset("meta")]
    public sealed class CodeModuleWriteTools
    {
        [McpServerTool(Name="revit_save_code_module", ReadOnly=false, Destructive=true, Idempotent=false, OpenWorld=false), System.ComponentModel.Description(
            "Save a C# declarations-only helper after compile-only validation on the pinned capable Revit host. New names require expected_hash=null; replacements require the current revision hash. Changes local storage; Revit Undo cannot restore it.")]
        public static async Task<string> SaveCodeModule(string name, string code, string description="", string[] requires=null, string expected_hash=null)
        {
            try
            {
                CodeModuleToolCore.CheckWritePolicy("save_code_module");
                using var db=CodeModuleTools.StoreFactory();
                return (await CodeModuleTools.Core(db).SaveAsync(name,code,description,requires,expected_hash,
                    TargetCallContext.Current?.Cancellation ?? default)).ToString(Formatting.None);
            }
            catch(Exception ex){return CodeModuleTools.Error(ex);}
        }

        [McpServerTool(Name="revit_delete_code_module", ReadOnly=false, Destructive=true, Idempotent=false, OpenWorld=false), System.ComponentModel.Description(
            "Delete a module head using its current expected_hash. Refuses live module dependents; retained revisions are immutable. Does not need Revit. Changes local storage; Revit Undo cannot restore it. No force delete.")]
        public static string DeleteCodeModule(string name, string expected_hash)
        {
            try { CodeModuleToolCore.CheckWritePolicy("delete_code_module"); using var db=CodeModuleTools.StoreFactory();return CodeModuleTools.Core(db).Delete(name,expected_hash).ToString(Formatting.None); }
            catch(Exception ex){return CodeModuleTools.Error(ex);}
        }
    }
}
