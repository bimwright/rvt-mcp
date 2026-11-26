namespace RvtMcp.Server
{
    partial class Program
    {
        // INSTR-MAX-2KB (BW-MCP-UPGRADE-001): Claude Code truncates server instructions at 4,096 characters;
        // the family cap is stricter (<= 2,048 UTF-8 bytes) so four gateways open together stay small and
        // clients with lower limits still read them whole.
        // INSTR-ORDER: product keywords, session start (target -> health -> survey -> report), tool index,
        // and closing Safety & permissions block (FC-SRV-06 / BW-PERM-001 R-C).
        // Detail belonging to one tool lives in that tool's description (INSTR-MOVE-TO-TOOL).
        internal const string Product =
            "rvt-mcp - Autodesk Revit 2022-2027 MCP gateway (revit_*, mm). "
            + "Use for .rvt, BIM, walls, doors, windows, floors, ceilings, roofs, levels, grids, rooms, sheets, schedules, families, "
            + "views, templates, filters, MEP (ducts, pipes, trays, conduits, HVAC, lighting, plumbing), structural (columns, beams, foundations, rebar), "
            + "dimensions, tags, keynotes, worksets, phases, links, coordinates, parameters, materials, clash, IFC, DWG, NWC, PDF.";

        internal const string Session =
            "Start: revit_list_available_targets, then revit_switch_target(\"pid:<n>\") to verify instance/document before writes "
            + "(--read-only: --target pid:<n>). Then revit_health(check_updates=true) once; follow update_guidance. "
            + "Subagents never switch. Check revit_get_operation_status before retrying a timed-out write; REVIT_BUSY means not started. "
            // INSTR-SURVEY-HINT
            + "Before modifying existing elements call revit_survey_change_impact; afterwards read the result back.";

        internal const string Reporting =
            "After Revit work that changed the model or found elements to check, call revit_report_task_result once "
            + "before your final answer (not for chat-only answers).";

        internal const string ToolIndex =
            "Toolsets (defaults query,create,view,meta; --toolsets all for rest):\n"
            + "- core: query (ai_element_filter), create, modify, delete, view, documents (save_document, save_checkpoint)\n"
            + "- model: sheets (get_viewport_geometry), schedule, families, annotation, graphics, organization, parameters, materials, rooms, links\n"
            + "- eng/meta: mep, structural, geometry, export, workflows, lint, toolbaker (run_baked_tool), meta (get_document_state, open_model, set_dialog_answers)";

        // INSTR-SAFETY-BLOCK / FC-SRV-06 / BW-PERM-001 R-C: always the last block.
        internal const string SafetyAndPermissions =
            "Safety & permissions: prefer read tools and dry_run previews. "
            + "Before deleting or overwriting anything not created in this session, name exact element IDs or paths, "
            + "get user confirmation, then pass those literal values. "
            + "If a permission check or auto mode refuses a call, do not repeat it via revit_batch_execute, a baked tool, or revit_send_code_to_revit; tell the user. "
            + "revit_send_code_to_revit is full-trust C#, not a sandbox. Work in the user's project folder; give full paths.";

        private const string ServerInstructionsText =
            Product + "\n\n" + Session + "\n\n" + Reporting + "\n\n" + ToolIndex + "\n\n" + SafetyAndPermissions;
    }
}
