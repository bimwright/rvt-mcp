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
    partial class Program
    {
        // MCP InitializeResult metadata. ServerInstructions is the single most important
        // signal for Tool Search discoverability — Claude Code/Desktop's Tool Search ranks
        // servers by (1) instructions, (2) literal tool name. Without this populated, an
        // agent asking "list Revit tools" returns nothing even though 224 tools are exposed.
        // Anthropic truncates this field at 2KB; the keyword-dense first paragraph carries
        // the discoverability load if the SDK or proxy truncates later.
        // InformationalVersion carries "+githash"; report clean semver to MCP clients.
        internal static readonly string ServerVersion =
            (Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0").Split('+')[0];

        private static void ConfigureMcpServerOptions(ModelContextProtocol.Server.McpServerOptions opts)
        {
            opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "rvt-mcp",
                Title = "Revit MCP",
                Version = ServerVersion,
                Description = "Model Context Protocol gateway for Autodesk Revit 2022-2027",
                WebsiteUrl = "https://github.com/bimwright/rvt-mcp"
            };
            // A newer release known from the add-in's cached daily check (no network here).
            opts.ServerInstructions = RvtMcp.Plugin.Update.UpdateNoticeText.ComposeInstructions(
                ServerInstructionsText, RvtMcp.Plugin.Update.UpdateNoticeText.ForAgent(UpdateStatus.Current()));
        }

        // Anthropic Tool Search truncates this at 2 KB; the constant below is kept
        // under 2048 UTF-8 bytes. Lead with the keyword paragraph (highest discriminative
        // signal for queries like "list Revit tools"), then a compact toolset-name index
        // — 2 examples per toolset — so semantic search for individual ops still resolves.
        private const string ServerInstructionsText =
@"rvt-mcp — MCP gateway for Autodesk Revit 2022-2027. Use whenever user works with .rvt, Revit, BIM, walls, doors, windows, floors, ceilings, roofs, levels, grids, rooms, sheets, schedules, families, views, view templates, view filters, MEP (ducts, pipes, trays, conduits, HVAC, lighting, plumbing), structural (columns, beams, foundations, rebar), dimensions, tags, annotations, keynotes, worksets, phases, links, parameters, materials, IFC, DWG, NWC, PDF.

Multi-Revit: list via revit_list_available_targets (a year can repeat); pin with revit_switch_target(""pid:<n>""); never guess. Defaults: query,create,view,meta. --toolsets all for export/clash. Do not retry clash/export after 60s timeout.

Before model changes, use the revit_change prompt: survey, agree scope, record why.

Tools (prefix revit_<verb>_<noun>, lengths in mm):
- query: get_current_view_info, ai_element_filter, get_element_details
- create: create_grid, create_room
- modify: operate_element, set_element_parameter_values
- delete: delete_element
- view: create_view, capture_view_image
- sheets: create_sheet, renumber_sheets, align_viewports, viewport_geometry
- schedule: create_schedule, list_schedules
- families: list_loaded_families, load_family_from_path
- mep: create_duct, analyze_mep_network
- annotation: tag_elements, create_dimensions
- graphics: create_view_filter, override_element_graphics
- export: export_pdf, export_dwg, export_ifc, export_nwc
- materials: list_materials, assign_material_to_element
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
