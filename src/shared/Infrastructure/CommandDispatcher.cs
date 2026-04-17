using System.Collections.Generic;
using RevitMcp.Plugin.ToolBaker;

namespace RevitMcp.Plugin
{
    public class CommandDispatcher
    {
        private readonly Dictionary<string, IRevitCommand> _commands = new Dictionary<string, IRevitCommand>();

        public CommandDispatcher()
        {
            Register(new Handlers.ShowMessageHandler());
            Register(new Handlers.GetCurrentViewHandler());
            Register(new Handlers.GetSelectedElementsHandler());
            Register(new Handlers.GetFamilyTypesHandler());
            Register(new Handlers.AiElementFilterHandler());
            Register(new Handlers.AnalyzeModelStatisticsHandler());
            Register(new Handlers.GetMaterialQuantitiesHandler());
            // Phase 3: Create
            Register(new Handlers.CreateLineBasedElementHandler());
            Register(new Handlers.CreatePointBasedElementHandler());
            Register(new Handlers.CreateSurfaceBasedElementHandler());
            Register(new Handlers.CreateLevelHandler());
            Register(new Handlers.CreateGridHandler());
            Register(new Handlers.CreateRoomHandler());
            // Phase 4: Modify & Delete
            Register(new Handlers.OperateElementHandler());
            Register(new Handlers.ColorElementsHandler());
            Register(new Handlers.DeleteElementHandler());
            // Phase 5: Export & Tags
            Register(new Handlers.ExportRoomDataHandler());
            Register(new Handlers.TagAllWallsHandler());
            Register(new Handlers.TagAllRoomsHandler());
            // Phase 6: Dynamic Code
            Register(new Handlers.SendCodeToRevitHandler());
            // Phase 7: Views & Sheets
            Register(new Handlers.CreateViewHandler());
            Register(new Handlers.PlaceViewOnSheetHandler());
            // Phase 8+: MEP System Analysis
            Register(new Handlers.DetectSystemElementsHandler());
            // Phase 10: New tools (post-analysis)
            Register(new Handlers.AnalyzeSheetLayoutHandler());
            // DB Tools
            Register(new Handlers.GetActiveProjectDbHandler());
            Register(new Handlers.ReadKeiLogsHandler());
            Register(new Handlers.QueryKeiDatabaseHandler());
            // BOQ / Flow Analysis
            Register(new Handlers.FlowSortHandler());
            Register(new Handlers.SavePortDeclarationHandler());
            // MCP Prompts support
            Register(new Handlers.GetModelOverviewHandler());
            // KEI Equipment Management (Phase 4)
            Register(new Handlers.ManageEquipmentCategoriesHandler());
            Register(new Handlers.ManageEquipmentTypeHandler());
            Register(new Handlers.ManageTypedSpecsHandler());
            Register(new Handlers.ManageEquipmentInstanceHandler());
            // ToolBaker (Debug only — gated by #if in handlers)
            Register(new Handlers.BakeToolHandler());
            Register(new Handlers.ListBakedToolsHandler());
            Register(new Handlers.RunBakedToolHandler());
        }

        public void Register(IRevitCommand command)
        {
            _commands[command.Name] = command;
        }

        public IRevitCommand GetCommand(string name)
        {
            _commands.TryGetValue(name, out var command);
            return command;
        }

        /// <summary>Load all baked tools from registry and register them.</summary>
        public void LoadBakedTools(BakedToolRegistry registry)
        {
            foreach (var meta in registry.GetAll())
            {
                var source = registry.GetSource(meta.Name);
                if (source == null) continue;
                try
                {
                    var command = ToolCompiler.CompileAndLoad(source, out var error);
                    if (command != null)
                        Register(command);
                    else
                        System.Diagnostics.Debug.WriteLine($"[RevitMCP] Failed to load baked tool '{meta.Name}': {error}");
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RevitMCP] Error loading baked tool '{meta.Name}': {ex.Message}");
                }
            }
        }
    }
}
