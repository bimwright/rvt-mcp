using System;
using System.Collections.Generic;

namespace RvtMcp.Plugin
{
    /// <summary>File/document read policy shared with the plugin. Kept equal to MCP annotations by tests.</summary>
    public static class ToolReadPolicy
    {
        public static readonly ISet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "analyze_usage_patterns",
            "activate_view",
            "ai_element_filter",
            "analyze_geometry_complexity",
            "analyze_mep_network",
            "analyze_model_statistics",
            "analyze_sheet_layout",
            "analyze_structural_connections",
            "analyze_view_naming_patterns",
            "audit_families",
            "clash_detection",
            "compute_element_area",
            "compute_element_volume",
            "detect_firm_profile",
            "detect_system_elements",
            "find_elements_in_volume",
            "find_mep_disconnects",
            "find_overlapping_elements",
            "find_schedule_elements",
            "find_undimensioned_elements",
            "find_untagged_elements",
            "get_assembly_members",
            "get_available_family_types",
            "get_current_target",
            "get_current_view_info",
            "get_change_records",
            "get_element_bounding_box",
            "get_element_centroid",
            "get_element_details",
            "get_element_geometry",
            "get_element_parameters",
            "get_element_relationships",
            "get_family_instances",
            "get_group_members",
            "get_link_coordinate_system",
            "get_link_elements",
            "get_material_properties",
            "get_material_quantities",
            "get_mep_element_connectors",
            "get_model_warnings_summary",
            "get_panel_schedule",
            "get_print_settings",
            "get_project_coordinate_system",
            "get_room_boundaries",
            "get_room_openings",
            "get_schedulable_fields",
            "get_schedule_data",
            "get_schedule_definition",
            "get_schedule_formulas",
            "get_selected_elements",
            "get_structural_loads",
            "get_system_inventory",
            "get_titleblock_parameters",
            "get_type_parameters",
            "get_view_visibility",
            "list_areas",
            "list_assemblies",
            "list_available_targets",
            "list_baked_tools",
            "list_export_settings",
            "list_family_types_in_family",
            "list_groups",
            "list_keynotes",
            "list_linked_cad",
            "list_linked_models",
            "list_loaded_families",
            "list_materials",
            "list_mep_systems",
            "list_phases",
            "list_project_parameter_bindings",
            "list_project_parameters",
            "list_rebar",
            "list_recent_models",
            "list_revisions",
            "list_rooms",
            "list_saved_selections",
            "list_schedules",
            "list_shared_parameters",
            "list_sheets",
            "list_titleblocks",
            "list_view_filters",
            "list_view_templates",
            "list_worksets",
            "load_selection",
            "measure_distance_between_elements",
            "project_point_onto_face",
            "raycast_from_point",
            "select_elements",
            "show_element_in_view",
            "show_message",
            "suggest_view_name_corrections",
            "survey_change_impact",
            "switch_target",
            "workflow_model_audit",
        };

        public static bool IsReadOnly(string command) => command != null && ReadOnlyCommands.Contains(command);

        public static string Rejection(string command, bool readOnly, bool enableSendCode)
        {
            if (string.Equals(command, "send_code_to_revit", StringComparison.OrdinalIgnoreCase)
                && !enableSendCode)
                return "SEND_CODE_DISABLED: send_code is disabled for this session.";
            if (readOnly && !IsReadOnly(command))
                return "READ_ONLY: this tool can change documents or files and is disabled for this session.";
            return null;
        }
    }
}
