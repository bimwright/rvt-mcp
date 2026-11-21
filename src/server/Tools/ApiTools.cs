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
            => await ToolGateway.CallWithParamsAsync("describe_api", () =>
            {
                if (string.IsNullOrWhiteSpace(type)) throw new System.ArgumentException("INVALID_ARGUMENT: type is required, e.g. \"Wall\" or \"Autodesk.Revit.DB.Wall\".");
                return new { type, member };
            });
    }
}
