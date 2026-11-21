#nullable enable
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    /// <summary>Reads the Revit API of the running Revit. Kept apart from SendCodeTools so --read-only and --disable-send-code keep it.</summary>
    [McpServerToolType, Toolset("meta")]
    public class ApiTools
    {
        [McpServerTool(Name = "revit_describe_api", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "List one Revit API type as loaded in the running Revit, to write send_code against this version: constructors and static factories, " +
            "properties with get/set, methods with parameter types, enum values, obsolete marks, base types and direct subtypes; inherited members are grouped by declaring type. " +
            "Reads no document and changes nothing. type is a short or full name; member keeps members whose name contains it. " +
            "Example: type=\"FamilyManager\", member=\"Parameter\".")]
        public static async Task<string> DescribeApi(string type, string? member = null)
        {
            var result = await ToolGateway.CallWithParamsAsync("describe_api", () =>
            {
                if (string.IsNullOrWhiteSpace(type)) throw new System.ArgumentException("type is required, e.g. \"Wall\" or \"Autodesk.Revit.DB.Wall\".");
                return new { type, member };
            });
            // An add-in older than this gateway does not know the command.
            return result.Contains("Unknown command: describe_api")
                ? "Error: CAPABILITY_REQUIRED: the Revit add-in predates revit_describe_api. Update the gateway and Revit add-in together, then restart Revit."
                : result;
        }
    }
}
