using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("families")]
    public class FamiliesTools
    {
        [McpServerTool(Name = "revit_list_loaded_families", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all loaded families (loadable + in-place + system) in the active document grouped by category. Returns id, name, category, kind (system|loadable|inplace), type_count, optional instance_count, and is_editable. Filter via categoryFilter (case-insensitive substring) and kindFilter (all|system|loadable|inplace).")]
        public static async Task<string> ListLoadedFamilies(string categoryFilter = "", string kindFilter = "all", bool includeInstanceCount = false, int limit = 1000)
            => await ToolGateway.CallAsync("list_loaded_families", new { category_filter = categoryFilter, kind_filter = kindFilter, include_instance_count = includeInstanceCount, limit });

        [McpServerTool(Name = "revit_load_family_from_path", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Load or replace family definitions; restore model changes with Revit Undo. Load an .rfa family. Response is compact by default; set includeSymbols=true for a bounded symbol preview (maxSymbolResults hard max 1000). timeout_seconds: plugin wait 1-900s, default 600 - a large family load can exceed 60s.")]
        public static async Task<string> LoadFamilyFromPath(string path, bool overwriteExisting = true, bool overwriteParameterValues = false, bool includeSymbols = false, int maxSymbolResults = 200, int timeout_seconds = 600)
        {
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            return await ToolGateway.CallAsync("load_family_from_path", new { path, overwrite_existing = overwriteExisting, overwrite_parameter_values = overwriteParameterValues, include_symbols = includeSymbols, max_symbol_results = maxSymbolResults }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_unload_family", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Delete a family and optionally its instances; restore them with Revit Undo. Remove (purge) a loadable family from the document. Identify by familyId or familyName. cascadeDeleteInstances=true to also delete placed instances; otherwise error if instances exist. dryRun=true returns the projected effect without changing the model. System families cannot be unloaded.")]
        public static async Task<string> UnloadFamily(string familyId = "", string familyName = "", bool cascadeDeleteInstances = false, bool dryRun = false)
            => await ToolGateway.CallAsync("unload_family", new { family_id = familyId, family_name = familyName, cascade_delete_instances = cascadeDeleteInstances, dry_run = dryRun });

        [McpServerTool(Name = "revit_duplicate_family_type", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Duplicate a FamilySymbol or system type within its family under newTypeName, optionally setting type parameter overrides (JSON object as string, parameter name → value). Returns the new type id. Works for FamilySymbol and ElementType subclasses (WallType, FloorType, etc.).")]
        public static async Task<string> DuplicateFamilyType(string sourceTypeId, string newTypeName, string typeParameterOverrides = "")
            => await ToolGateway.CallWithParamsAsync("duplicate_family_type", () =>
            {
                var parsedOverrides = string.IsNullOrWhiteSpace(typeParameterOverrides) ? null : JObject.Parse(typeParameterOverrides);
                return new { source_type_id = sourceTypeId, new_type_name = newTypeName, type_parameter_overrides = parsedOverrides };
            });

        [McpServerTool(Name = "revit_rename_family_type", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Rename an existing type; restore its name with Revit Undo. Rename a FamilySymbol or system type. Must be unique within the family. Catches Autodesk.Revit.Exceptions.ArgumentException for duplicate/invalid names and returns a clean error DTO without throwing.")]
        public static async Task<string> RenameFamilyType(string typeId, string newName)
            => await ToolGateway.CallAsync("rename_family_type", new { type_id = typeId, new_name = newName });

        [McpServerTool(Name = "revit_audit_families", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read-only family audit with bounded sections. Page each section with startIndex + limitPerSection (hard max 500).")]
        public static async Task<string> AuditFamilies(bool includeUnused = true, bool includeInplace = true, bool includeDuplicateNames = true, bool includeHighTypeCount = true, int highTypeCountThreshold = 20, int startIndex = 0, int limitPerSection = 100)
            => await ToolGateway.CallAsync("audit_families", new { include_unused = includeUnused, include_inplace = includeInplace, include_duplicate_names = includeDuplicateNames, include_high_type_count = includeHighTypeCount, high_type_count_threshold = highTypeCountThreshold, start_index = startIndex, limit_per_section = limitPerSection });

        [McpServerTool(Name = "revit_replace_family_type", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace instance types; restore them with Revit Undo. Replace all instances of FamilySymbol A with FamilySymbol B across the project, active view, or selection. Both types must be the same category. dryRun=true previews counts without changing the model. Target symbol is auto-activated.")]
        public static async Task<string> ReplaceFamilyType(string fromTypeId, string toTypeId, string scope = "all", long? viewId = null, bool dryRun = false)
            => await ToolGateway.CallAsync("replace_family_type", new { from_type_id = fromTypeId, to_type_id = toTypeId, scope, view_id = viewId, dry_run = dryRun });

        [McpServerTool(Name = "revit_get_family_instances", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List placed instances of a Family (or a specific type within it) with location/host/level DTOs in mm. viewOnly=true restricts to the active view. Returns location_kind (point|line|null), coordinates in mm, host_id/name, mark.")]
        public static async Task<string> GetFamilyInstances(string familyId = "", string familyName = "", string typeName = "", bool viewOnly = false, int limit = 1000)
            => await ToolGateway.CallAsync("get_family_instances", new { family_id = familyId, family_name = familyName, type_name = typeName, view_only = viewOnly, limit });

        [McpServerTool(Name = "revit_list_family_types_in_family", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List a bounded page of types in one family. Use parameterNames to return only needed values; maxTypes hard max 500.")]
        public static async Task<string> ListFamilyTypesInFamily(string familyId = "", string familyName = "", bool includeParameterValues = true, bool includeBuiltInOnly = false, int startIndex = 0, int maxTypes = 100, string[] parameterNames = null)
            => await ToolGateway.CallAsync("list_family_types_in_family", new { family_id = familyId, family_name = familyName, include_parameter_values = includeParameterValues, include_built_in_only = includeBuiltInOnly, start_index = startIndex, max_types = maxTypes, parameter_names = parameterNames });

        [McpServerTool(Name = "revit_export_family_to_path", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Save a loadable family from the current project back to an .rfa file at outputPath. Writes to disk (not ReadOnly). Rejects in-place and system families. overwriteExisting=false errors if the file already exists. Uses doc.EditFamily + Document.SaveAs." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportFamilyToPath(string outputPath, string familyId = "", string familyName = "", bool overwriteExisting = false)
            => await ToolGateway.CallAsync("export_family_to_path", new { family_id = familyId, family_name = familyName, output_path = outputPath, overwrite_existing = overwriteExisting });
    }
}
