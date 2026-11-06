namespace RvtMcp.Server
{
    partial class Program
    {
        // Anthropic Tool Search truncates this at 2 KB; the constant below is kept
        // under 2048 UTF-8 bytes. Lead with the keyword paragraph (highest discriminative
        // signal for queries like "list Revit tools"), then the report rule (clients that load
        // tool descriptions on demand never read revit_report_task_result's), then a compact
        // toolset-name index — 1-2 examples per toolset — so semantic search for individual ops still resolves.
        private const string ServerInstructionsText =
@"rvt-mcp — Autodesk Revit 2022-2027 MCP gateway. Use for .rvt, BIM, walls, doors, windows, floors, ceilings, roofs, levels, grids, rooms, sheets, schedules, families, views, templates, filters, MEP (ducts, pipes, trays, conduits, HVAC, lighting, plumbing), structural (columns, beams, foundations, rebar), dimensions, tags, annotations, keynotes, worksets, phases, links, parameters, materials, IFC, DWG, NWC, PDF.

Connect: list targets; revit_health(). For writes pin and verify with revit_switch_target(""pid:<n>""); subagents never switch. --read-only: set --target pid:<n> at startup; restart to change. check_updates=true opts in; unknown is not current. Follow update_guidance; never auto-install/downgrade or retry after timeout. Defaults: query,create,view,meta; --toolsets all for others.

After Revit work that changed the model or found elements to check, call revit_report_task_result once before your final answer (not for chat-only answers).

Documents: call revit_get_document_state before saving and follow its policy; checkpoint only before risky work.

Tools (prefix revit_<verb>_<noun>, lengths in mm):
- query: get_current_view_info, ai_element_filter
- create: create_grid
- modify: operate_element, set_element_parameter_values
- delete: delete_element
- view: capture_view_image
- sheets: create_sheet, viewport_geometry
- schedule: list_schedules
- families: list_loaded_families
- mep: analyze_mep_network
- annotation: create_dimensions
- graphics: create_view_filter
- export: export_pdf
- materials: list_materials
- geometry: clash_detection
- rooms: list_rooms
- links: project coordinates, link/acquire/publish
- parameters: create_shared_parameter
- organization: apply_view_template, save_selection
- workflows: workflow_model_audit
- documents: save_document, save_checkpoint
- structural: create_structural_column, create_rebar_set
- meta: open_model, send_code_to_revit, batch_execute
- lint: find_untagged_elements, get_model_warnings_summary
- toolbaker: list_baked_tools, run_baked_tool";
    }
}
