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
    [McpServerToolType, Toolset("mep")]
    public class MepTools
    {
        [McpServerTool(Name = "revit_detect_system_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Walk an MEP system and page IDs per category with startElement + maxElements (hard max 5000).")]
        public static async Task<string> DetectSystemElements(long elementId, int startElement = 0, int maxElements = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("detect_system_elements", new { elementId, start_element = startElement, max_elements = maxElements });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_duct", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an HVAC duct between two points (mm). ductTypeId/systemTypeId/levelId default to first available / nearest level. Provide diameter for round duct OR width+height (mm) for rectangular.")]
        public static async Task<string> CreateDuct(double startX, double startY, double startZ, double endX, double endY, double endZ, long? ductTypeId = null, long? systemTypeId = null, long? levelId = null, double? width = null, double? height = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_duct", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, duct_type_id = ductTypeId, system_type_id = systemTypeId, level_id = levelId, width, height, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_pipe", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a pipe (mm). With systemTypeId omitted, a unique open piping End connector within 1 mm of start supplies system type and diameter and is connected immediately. Multiple matches or conflicting diameter fail without creating. Use startElementId/startConnectorId (Connector.Id) to disambiguate; omit systemTypeId with these selectors. No match falls back to first system type. Explicit systemTypeId creates an independent pipe. Result reports actual type/diameter and system_type_source=connector|explicit|default. pipeTypeId defaults to first available; levelId to nearest level.")]
        public static async Task<string> CreatePipe(double startX, double startY, double startZ, double endX, double endY, double endZ, long? pipeTypeId = null, long? systemTypeId = null, long? levelId = null, double? diameter = null, long? startElementId = null, int? startConnectorId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_pipe", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, pipe_type_id = pipeTypeId, system_type_id = systemTypeId, level_id = levelId, diameter, start_element_id = startElementId, start_connector_id = startConnectorId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_cable_tray", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an electrical cable tray between two points (mm). cableTrayTypeId/levelId default to first available / nearest level. Optional width+height (mm).")]
        public static async Task<string> CreateCableTray(double startX, double startY, double startZ, double endX, double endY, double endZ, long? cableTrayTypeId = null, long? levelId = null, double? width = null, double? height = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_cable_tray", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, cable_tray_type_id = cableTrayTypeId, level_id = levelId, width, height });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_conduit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an electrical conduit between two points (mm). conduitTypeId/levelId default to first available / nearest level. Optional diameter (mm).")]
        public static async Task<string> CreateConduit(double startX, double startY, double startZ, double endX, double endY, double endZ, long? conduitTypeId = null, long? levelId = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_conduit", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, conduit_type_id = conduitTypeId, level_id = levelId, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_air_terminal", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place an air terminal (diffuser/grille) family instance at a point (mm). typeId must be an Air Terminal FamilySymbol. Optional hostId for hosted placement on a duct/ceiling.")]
        public static async Task<string> CreateAirTerminal(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_air_terminal", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_lighting_fixture", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place a lighting fixture family instance at a point (mm). typeId must be a Lighting Fixture FamilySymbol. Optional hostId for hosted placement on a ceiling.")]
        public static async Task<string> CreateLightingFixture(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_lighting_fixture", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_mep_systems", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all MEP systems (mechanical/HVAC, piping/plumbing, electrical). domainFilter: all|mechanical|piping|electrical. element_count is pipes and fittings, ducts and fittings, or electrical circuit members. terminal_count excludes base equipment. Membership read failures return an error, never zero counts. Returns id, name, domain, system type, both counts, and connectivity status.")]
        public static async Task<string> ListMepSystems(string domainFilter = "all", int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_mep_systems", new { domain_filter = domainFilter, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_system_inventory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Return the full element inventory of one MEP system: network members, terminals, and base equipment, deduplicated by element ID, with category/type plus a category breakdown. Membership read failures return an error. Identify by systemId or systemName.")]
        public static async Task<string> GetSystemInventory(long? systemId = null, string systemName = "", bool includeParameters = false, int limit = 2000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_system_inventory", new { system_id = systemId, system_name = systemName, include_parameters = includeParameters, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_mep_element_connectors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Inspect all connectors on an MEP element (duct/pipe/fitting/equipment/terminal): domain, shape, position (mm), connection status, flow, direction.")]
        public static async Task<string> GetMepElementConnectors(long elementId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_mep_element_connectors", new { element_id = elementId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_connect_mep_elements", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change MEP connectivity; restore connections with Revit Undo. Connect physical connectors of two MEP elements with matching domains. Different assigned piping/HVAC system type IDs are rejected with connected=false and both types; unassigned equipment ports are allowed. A connection does not promise system merging. Optionally pin connectorIndex1/connectorIndex2 using Connector.Id, not ordinals. An existing direct connection or connection through one shared pipe/duct fitting is returned as already_connected=true without mutation.")]
        public static async Task<string> ConnectMepElements(long elementId1, long elementId2, long? connectorIndex1 = null, long? connectorIndex2 = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("connect_mep_elements", new { element_id_1 = elementId1, element_id_2 = elementId2, connector_index_1 = connectorIndex1, connector_index_2 = connectorIndex2 });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_mep_fitting", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Insert an MEP fitting at connectors of existing MEP elements. fittingKind: elbow|tee|union|cross|transition. connectors is a JSON array of {element_id, connector_index} where connector_index is the connector_id from get_mep_element_connectors: elbow/union/transition need 2, tee 3, cross 4.")]
        public static async Task<string> CreateMepFitting(string fittingKind, string connectors)
        {
            try
            {
                var parsedConnectors = JArray.Parse(connectors);
                var result = await ToolGateway.SendToRevit("create_mep_fitting", new { fitting_kind = fittingKind, connectors = parsedConnectors });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_system_classification", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change MEP system membership; restore it with Revit Undo. Add MEP elements to an existing duct/piping system. If systemId omitted, only reports current system membership (read-only). elementIds is an array of MEP element ids.")]
        public static async Task<string> SetSystemClassification(long[] elementIds, long? systemId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_system_classification", new { element_ids = elementIds, system_id = systemId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_panel_schedule", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read panel metadata and a bounded circuit page. Use startCircuit + maxCircuits (hard max 1000).")]
        public static async Task<string> GetPanelSchedule(long? panelId = null, string panelName = "", int startCircuit = 0, int maxCircuits = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_panel_schedule", new { panel_id = panelId, panel_name = panelName, start_circuit = startCircuit, max_circuits = maxCircuits });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_find_mep_disconnects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find MEP elements with open/unconnected End connectors (potential gaps in ductwork, piping, conduit). domainFilter: all|mechanical|piping|electrical. viewOnly restricts to the active view.")]
        public static async Task<string> FindMepDisconnects(string domainFilter = "all", bool viewOnly = false, int limit = 2000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_mep_disconnects", new { domain_filter = domainFilter, view_only = viewOnly, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_mep_network", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Analyze one MEP system. For piping and HVAC, element_count, category_breakdown, and open_connector_count come from the pipe or duct network; terminal_count excludes base equipment. Empty-system advice requires both counts zero and no base equipment. Membership read failures return an error. Electrical element_count is the circuit members. Identify by systemId or systemName.")]
        public static async Task<string> AnalyzeMepNetwork(long? systemId = null, string systemName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_mep_network", new { system_id = systemId, system_name = systemName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
