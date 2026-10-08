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
    [McpServerToolType, Toolset("parameters")]
    public class ParametersTools
    {
        [McpServerTool(Name = "revit_list_shared_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all shared parameters defined in the shared parameter file. Returns guid, name, dataTypeId, isBound, bindingKind, categories.")]
        public static async Task<string> ListSharedParameters(string sharedParameterFilePath = "", string groupName = "", bool includeBindings = true, int limit = 1000)
            => await ToolGateway.CallAsync("list_shared_parameters", new { sharedParameterFilePath, groupName, includeBindings, limit });

        [McpServerTool(Name = "revit_create_shared_parameter", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a shared parameter definition in the shared parameter file.")]
        public static async Task<string> CreateSharedParameter(string name, string dataTypeId, string groupName = "RvtMcp", string guid = "", string sharedParameterFilePath = "", bool createFileIfMissing = true, string description = "", bool visible = true, bool userModifiable = true, bool hideWhenNoValue = false)
            => await ToolGateway.CallAsync("create_shared_parameter", new { name, dataTypeId, groupName, guid, sharedParameterFilePath, createFileIfMissing, description, visible, userModifiable, hideWhenNoValue });

        [McpServerTool(Name = "revit_bind_shared_parameter", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Add or replace parameter bindings; restore document changes with Revit Undo. Bind a shared parameter from the shared parameter file to categories in the project.")]
        public static async Task<string> BindSharedParameter(string guid, string[] categories, string bindingKind = "instance", string parameterGroupId = "", string sharedParameterFilePath = "", bool allowRebind = false)
            => await ToolGateway.CallAsync("bind_shared_parameter", new { guid, categories, bindingKind, parameterGroupId, sharedParameterFilePath, allowRebind });

        [McpServerTool(Name = "revit_create_project_parameter", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a pure project parameter. Note: The public Revit API does not support non-shared project parameter creation; this command will fail explicitly stating it is unsupported.")]
        public static async Task<string> CreateProjectParameter(string name, string dataTypeId, string[] categories, string bindingKind = "instance", string parameterGroupId = "")
            => await ToolGateway.CallAsync("create_project_parameter", new { name, dataTypeId, categories, bindingKind, parameterGroupId });

        [McpServerTool(Name = "revit_list_project_parameter_bindings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all project parameter bindings in the document, including name, GUID (if shared), categories, and binding type.")]
        public static async Task<string> ListProjectParameterBindings(bool includeCategories = true, bool includeShared = true, bool includeProject = true, string nameFilter = "", string guid = "", int limit = 1000)
            => await ToolGateway.CallAsync("list_project_parameter_bindings", new { includeCategories, includeShared, includeProject, nameFilter, guid, limit });

        [McpServerTool(Name = "revit_remove_parameter_binding", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Remove parameter bindings or categories; restore them with Revit Undo. Remove a parameter binding or specific categories from a binding in the document.")]
        public static async Task<string> RemoveParameterBinding(string name = "", string guid = "", string[] categories = null, bool removeAllCategories = false, bool dryRun = true)
            => await ToolGateway.CallAsync("remove_parameter_binding", new { name, guid, categories, removeAllCategories, dryRun });

        [McpServerTool(Name = "revit_export_shared_parameter_file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export a shared parameter file as structured DTO data. output=file writes JSON to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> ExportSharedParameterFile(string sharedParameterFilePath = "", string output = "inline")
            => await ToolGateway.CallAsync("export_shared_parameter_file", new { sharedParameterFilePath, output });

        [McpServerTool(Name = "revit_set_parameter_value_by_guid", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace parameter values by GUID; restore them with Revit Undo. Set the value of a parameter by its shared GUID on one or more elements.")]
        public static async Task<string> SetParameterValueByGuid(long[] elementIds, string guid, string value, string valueType = "auto", string unit = "auto", string target = "auto", bool allOrNothing = true)
            => await ToolGateway.CallAsync("set_parameter_value_by_guid", new { elementIds, guid, value, valueType, unit, target, allOrNothing });
    }
}
