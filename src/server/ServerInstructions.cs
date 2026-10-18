namespace RvtMcp.Server
{
    partial class Program
    {
        // Anthropic Tool Search truncates this at 2 KB; the constant below is kept
        // under 2048 UTF-8 bytes. Lead with the keyword paragraph (highest discriminative
        // signal for queries like "list Revit tools"), then a compact toolset-name index
        // — 2 examples per toolset — so semantic search for individual ops still resolves.
        private const string ServerInstructionsText =
@"rvt-mcp — MCP gateway for Autodesk Revit 2022-2027. Use whenever user works with .rvt, Revit, BIM, walls, doors, windows, floors, ceilings, roofs, levels, grids, rooms, sheets, schedules, families, views, view templates, view filters, MEP (ducts, pipes, trays, conduits, HVAC, lighting, plumbing), structural (columns, beams, foundations, rebar), dimensions, tags, annotations, keynotes, worksets, phases, links, parameters, materials, IFC, DWG, NWC, PDF.

Connection check: list targets then revit_health(). Writable: pin with revit_switch_target(""pid:<n>""); verify before writes. --read-only: set --target pid:<n> at startup; restart to change it. check_updates=true opts into releases; unknown is not current. Follow update_guidance; never auto-install/downgrade or retry after timeout. Defaults: query,create,view,meta; --toolsets all for others.

Tools (prefix revit_<verb>_<noun>, lengths in mm):
- query: get_current_view_info, ai_element_filter
- create: create_grid, create_room
- modify: operate_element, set_element_parameter_values
- delete: delete_element
- view: create_view, capture_view_image
- sheets: create_sheet, viewport_geometry
- schedule: create_schedule, list_schedules
- families: list_loaded_families, load_family_from_path
- mep: create_duct, analyze_mep_network
- annotation: tag_elements, create_dimensions
- graphics: create_view_filter, override_element_graphics
- export: export_pdf, export_dwg, export_ifc, export_nwc
- materials: list_materials
- geometry: clash_detection, measure_distance_between_elements
- rooms: list_rooms, compute_room_finishes
- links: project coordinates, link/acquire/publish
- parameters: create_shared_parameter, create_project_parameter
- organization: apply_view_template, save_selection
- workflows: workflow_clash_review, workflow_model_audit
- structural: create_structural_column, create_rebar_set
- meta: open_model, send_code_to_revit, batch_execute
- lint: find_untagged_elements, get_model_warnings_summary
- toolbaker: list_baked_tools, run_baked_tool";
    }
}
