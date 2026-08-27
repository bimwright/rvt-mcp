using System.Collections.Generic;
using RvtMcp.Plugin.ToolBaker;

namespace RvtMcp.Plugin
{
    public class CommandDispatcher
    {
        private readonly Dictionary<string, IRevitCommand> _commands = new Dictionary<string, IRevitCommand>();
        private readonly Dictionary<string, IRevitCommand> _bakedCommands = new Dictionary<string, IRevitCommand>();
        private readonly BakedToolRuntimeCache _runtimeCache;

        public CommandDispatcher(BakedToolRuntimeCache runtimeCache = null)
        {
            _runtimeCache = runtimeCache;
            Register(new Handlers.ShowMessageHandler());
            Register(new Handlers.GetCurrentViewHandler());
            Register(new Handlers.GetSelectedElementsHandler());
            Register(new Handlers.GetFamilyTypesHandler());
            Register(new Handlers.AiElementFilterHandler());
            Register(new Handlers.AnalyzeModelStatisticsHandler());
            Register(new Handlers.GetMaterialQuantitiesHandler());
            Register(new Handlers.GetElementDetailsHandler());
            Register(new Handlers.GetElementParametersHandler());
            Register(new Handlers.GetTypeParametersHandler());
            Register(new Handlers.ListProjectParametersHandler());
            Register(new Handlers.GetElementRelationshipsHandler());
            Register(new Handlers.ListGroupsHandler());
            Register(new Handlers.GetGroupMembersHandler());
            Register(new Handlers.ListAssembliesHandler());
            Register(new Handlers.GetAssemblyMembersHandler());
            Register(new Handlers.ListWorksetsHandler());
            // Phase 3: Create
            Register(new Handlers.CreateLineBasedElementHandler());
            Register(new Handlers.CreatePointBasedElementHandler());
            Register(new Handlers.CreateSurfaceBasedElementHandler());
            Register(new Handlers.CreateLevelHandler());
            Register(new Handlers.CreateGridHandler());
            Register(new Handlers.CreateRoomHandler());
            Register(new Handlers.CreateGroupFromElementsHandler());
            // Phase 4: Modify & Delete
            Register(new Handlers.OperateElementHandler());
            Register(new Handlers.ColorElementsHandler());
            Register(new Handlers.SetElementParameterValuesHandler());
            Register(new Handlers.SetTypeParameterValuesHandler());
            Register(new Handlers.ChangeElementTypeHandler());
            Register(new Handlers.AssignElementsToWorksetHandler());
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
            // MCP Prompts support
            Register(new Handlers.GetModelOverviewHandler());
            // Phase 11: Lint toolset (L-05 + L-13)
            Register(new Handlers.AnalyzeViewNamingPatternsHandler());
            Register(new Handlers.SuggestViewNameCorrectionsHandler());
            Register(new Handlers.DetectFirmProfileHandler());
            // Phase 12: Schedules
            Register(new Handlers.ListSchedulesHandler());
            Register(new Handlers.GetScheduleDefinitionHandler());
            Register(new Handlers.GetScheduleDataHandler());
            Register(new Handlers.GetScheduleFormulasHandler());
            Register(new Handlers.GetSchedulableFieldsHandler());
            Register(new Handlers.FindScheduleElementsHandler());
            Register(new Handlers.CreateScheduleHandler());
            Register(new Handlers.AddScheduleFieldHandler());
            Register(new Handlers.UpdateScheduleFieldHandler());
            Register(new Handlers.ApplyScheduleFilterSortHandler());
            // Phase 13: Families
            Register(new Handlers.ListLoadedFamiliesHandler());
            Register(new Handlers.LoadFamilyFromPathHandler());
            Register(new Handlers.UnloadFamilyHandler());
            Register(new Handlers.DuplicateFamilyTypeHandler());
            Register(new Handlers.RenameFamilyTypeHandler());
            Register(new Handlers.AuditFamiliesHandler());
            Register(new Handlers.ReplaceFamilyTypeHandler());
            Register(new Handlers.GetFamilyInstancesHandler());
            Register(new Handlers.ListFamilyTypesInFamilyHandler());
            Register(new Handlers.ExportFamilyToPathHandler());
            // Phase 14: MEP Systems
            Register(new Handlers.CreateDuctHandler());
            Register(new Handlers.CreatePipeHandler());
            Register(new Handlers.CreateCableTrayHandler());
            Register(new Handlers.CreateConduitHandler());
            Register(new Handlers.CreateAirTerminalHandler());
            Register(new Handlers.CreateLightingFixtureHandler());
            Register(new Handlers.ListMepSystemsHandler());
            Register(new Handlers.GetSystemInventoryHandler());
            Register(new Handlers.GetMepElementConnectorsHandler());
            Register(new Handlers.ConnectMepElementsHandler());
            Register(new Handlers.CreateMepFittingHandler());
            Register(new Handlers.SetSystemClassificationHandler());
            Register(new Handlers.GetPanelScheduleHandler());
            Register(new Handlers.FindMepDisconnectsHandler());
            Register(new Handlers.AnalyzeMepNetworkHandler());
            // Phase 15: Graphics — Filters / VG overrides / Phases
            Register(new Handlers.CreateViewFilterHandler());
            Register(new Handlers.ApplyFilterToViewHandler());
            Register(new Handlers.SetFilterOverridesHandler());
            Register(new Handlers.ListViewFiltersHandler());
            Register(new Handlers.RemoveFilterFromViewHandler());
            Register(new Handlers.OverrideElementGraphicsHandler());
            Register(new Handlers.ClearElementOverridesHandler());
            Register(new Handlers.GetViewVisibilityHandler());
            Register(new Handlers.SetCategoryVisibilityHandler());
            Register(new Handlers.ListPhasesHandler());
            Register(new Handlers.SetViewPhaseHandler());
            Register(new Handlers.SetElementPhaseHandler());
            // Phase 16: Print / Export
            Register(new Handlers.ExportPdfHandler());
            Register(new Handlers.ExportDwgHandler());
            Register(new Handlers.ExportDgnHandler());
            Register(new Handlers.ExportDwfHandler());
            Register(new Handlers.ExportIfcHandler());
            Register(new Handlers.ExportNwcHandler());
            Register(new Handlers.ExportFbxHandler());
            Register(new Handlers.ExportGbxmlHandler());
            Register(new Handlers.ExportImageHandler());
            Register(new Handlers.ExportScheduleCsvHandler());
            Register(new Handlers.ExportElementsDataHandler());
            Register(new Handlers.BatchExportSheetsHandler());
            Register(new Handlers.ListExportSettingsHandler());
            Register(new Handlers.CreateViewSheetSetHandler());
            Register(new Handlers.GetPrintSettingsHandler());

            // Wave 5: Sheets
            Register(new Handlers.CreateSheetHandler());
            Register(new Handlers.DuplicateSheetHandler());
            Register(new Handlers.CreatePlaceholderSheetHandler());
            Register(new Handlers.ListSheetsHandler());
            Register(new Handlers.SetTitleblockParametersHandler());
            Register(new Handlers.GetTitleblockParametersHandler());
            Register(new Handlers.ListTitleblocksHandler());
            Register(new Handlers.PlaceScheduleOnSheetHandler());
            Register(new Handlers.CreateRevisionHandler());
            Register(new Handlers.AssignRevisionToSheetHandler());
            Register(new Handlers.ListRevisionsHandler());
            Register(new Handlers.RenumberSheetsHandler());

            // Wave 6: Materials
            Register(new Handlers.ListMaterialsHandler());
            Register(new Handlers.GetMaterialPropertiesHandler());
            Register(new Handlers.CreateMaterialHandler());
            Register(new Handlers.DuplicateMaterialHandler());
            Register(new Handlers.SetMaterialAppearanceHandler());
            Register(new Handlers.SetMaterialIdentityHandler());
            Register(new Handlers.SetMaterialStructuralAssetHandler());
            Register(new Handlers.SetMaterialThermalAssetHandler());
            Register(new Handlers.AssignMaterialToElementHandler());
            Register(new Handlers.GetMaterialTakeoffHandler());

            // Wave 7: Geometry Analysis
            Register(new Handlers.GetElementBoundingBoxHandler());
            Register(new Handlers.GetElementGeometryHandler());
            Register(new Handlers.MeasureDistanceBetweenElementsHandler());
            Register(new Handlers.ClashDetectionHandler());
            Register(new Handlers.RaycastFromPointHandler());
            Register(new Handlers.FindElementsInVolumeHandler());
            Register(new Handlers.ComputeElementVolumeHandler());
            Register(new Handlers.ComputeElementAreaHandler());
            Register(new Handlers.ProjectPointOntoFaceHandler());
            Register(new Handlers.FindOverlappingElementsHandler());
            Register(new Handlers.GetElementCentroidHandler());
            Register(new Handlers.AnalyzeGeometryComplexityHandler());

            // Wave 8: Annotation & Detail
            Register(new Handlers.TagElementsHandler());
            Register(new Handlers.TagAllByCategoryHandler());
            Register(new Handlers.CreateTextNoteHandler());
            Register(new Handlers.CreateDimensionsHandler());
            Register(new Handlers.CreateFilledRegionHandler());
            Register(new Handlers.CreateDetailLineHandler());
            Register(new Handlers.CreateCalloutViewHandler());
            Register(new Handlers.ListKeynotesHandler());
            Register(new Handlers.ApplyKeynoteToElementHandler());
            Register(new Handlers.FindUntaggedElementsHandler());
            Register(new Handlers.FindUndimensionedElementsHandler());
            Register(new Handlers.WipeEmptyTagsHandler());

            // Wave 9: Rooms, Areas & Spaces
            Register(new Handlers.ListRoomsHandler());
            Register(new Handlers.GetRoomBoundariesHandler());
            Register(new Handlers.GetRoomOpeningsHandler());
            Register(new Handlers.CreateRoomSeparatorHandler());
            Register(new Handlers.CreateAreaHandler());
            Register(new Handlers.CreateSpaceHandler());
            Register(new Handlers.ListAreasHandler());
            Register(new Handlers.ComputeRoomFinishesHandler());
            Register(new Handlers.AutoCreateRoomsFromWallsHandler());
            Register(new Handlers.TagAllAreasHandler());

            // Wave 10: Links, CAD & Coordinates
            Register(new Handlers.ListLinkedModelsHandler());
            Register(new Handlers.ListLinkedCadHandler());
            Register(new Handlers.ImportCadToViewHandler());
            Register(new Handlers.LinkRevitModelHandler());
            Register(new Handlers.UnloadLinkHandler());
            Register(new Handlers.ReloadLinkHandler());
            Register(new Handlers.GetLinkElementsHandler());
            Register(new Handlers.GetProjectCoordinateSystemHandler());
            Register(new Handlers.GetLinkCoordinateSystemHandler());
            Register(new Handlers.AcquireCoordinatesFromLinkHandler());
            Register(new Handlers.PublishCoordinatesToLinkHandler());
            Register(new Handlers.SetProjectBasePointHandler());

            // Wave 11: Parameters
            Register(new Handlers.ListSharedParametersHandler());
            Register(new Handlers.CreateSharedParameterHandler());
            Register(new Handlers.BindSharedParameterHandler());
            Register(new Handlers.CreateProjectParameterHandler());
            Register(new Handlers.ListProjectParameterBindingsHandler());
            Register(new Handlers.RemoveParameterBindingHandler());
            Register(new Handlers.ExportSharedParameterFileHandler());
            Register(new Handlers.SetParameterValueByGuidHandler());

            // Wave 12: View Templates & Selections
            Register(new Handlers.ListViewTemplatesHandler());
            Register(new Handlers.CreateViewTemplateFromViewHandler());
            Register(new Handlers.ApplyViewTemplateHandler());
            Register(new Handlers.DuplicateViewTemplateHandler());
            Register(new Handlers.DeleteViewTemplateHandler());
            Register(new Handlers.SaveSelectionHandler());
            Register(new Handlers.LoadSelectionHandler());
            Register(new Handlers.ListSavedSelectionsHandler());
            Register(new Handlers.DeleteSavedSelectionHandler());
            Register(new Handlers.SelectElementsHandler());

            // Wave 13: Workflow Composites
            Register(new Handlers.WorkflowClashReviewHandler());
            Register(new Handlers.WorkflowModelAuditHandler());
            Register(new Handlers.WorkflowRoomDocumentationHandler());
            Register(new Handlers.WorkflowSheetSetHandler());
            Register(new Handlers.WorkflowDataRoundtripHandler());
            Register(new Handlers.WorkflowViewCleanupHandler());
            Register(new Handlers.WorkflowNamingNormalizationHandler());
            Register(new Handlers.WorkflowTakeoffReportHandler());

            // Wave 14: Structural Deep
            Register(new Handlers.CreateStructuralColumnHandler());
            Register(new Handlers.CreateStructuralBeamHandler());
            Register(new Handlers.CreateStructuralWallHandler());
            Register(new Handlers.CreateFoundationIsolatedHandler());
            Register(new Handlers.CreateFoundationWallHandler());
            Register(new Handlers.ListRebarHandler());
            Register(new Handlers.GetStructuralLoadsHandler());
            Register(new Handlers.SetStructuralLoadHandler());
            Register(new Handlers.AnalyzeStructuralConnectionsHandler());
            Register(new Handlers.TagStructuralFramingHandler());

            // Wave 16: Rebar Creation (deferred from Wave 14)
            Register(new Handlers.CreateRebarSetHandler());
            Register(new Handlers.CreateRebarStirrupHandler());

            // Wave 15: Final Fill
            Register(new Handlers.SetProjectInfoHandler());
            Register(new Handlers.GetModelWarningsSummaryHandler());
            Register(new Handlers.PurgeUnusedHandler());
            Register(new Handlers.CaptureViewImageHandler());
            Register(new Handlers.SetViewCropHandler());
            Register(new Handlers.SetViewScaleHandler());
            Register(new Handlers.ActivateViewHandler());
            Register(new Handlers.ShowElementInViewHandler());

            // KEI project SQLite via Revit process (WAL-safe). Agents write
            // project data here so BIM delivery can be AI-assisted without
            // fighting Revit's exclusive SQLite handle from an external process.
            Register(new Handlers.ImportProjectEquipmentHandler());
            Register(new Handlers.QueryKeiDatabaseHandler());
            Register(new Handlers.WriteKeiDatabaseHandler());
            Register(new Handlers.GetActiveProjectDbHandler());

            // A6 batch execution — needs dispatcher ref to look up sub-commands
            Register(new Handlers.BatchExecuteHandler(this));
            // ToolBaker runtime access.
            Register(new Handlers.ApplyBakeSuggestionHandler(runtimeCache));
            Register(new Handlers.ListBakedToolsHandler());
            Register(new Handlers.RunBakedToolHandler());
        }

        public void Register(IRevitCommand command)
        {
            _commands[command.Name] = command;
        }

        public bool RegisterBaked(IRevitCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.Name))
                return false;

            if (_commands.ContainsKey(command.Name))
                return false;
            if (_bakedCommands.ContainsKey(command.Name))
                return false;

            _bakedCommands[command.Name] = command;
            return true;
        }

        public IRevitCommand GetCommand(string name)
        {
            _commands.TryGetValue(name, out var command);
            return command;
        }

        public IRevitCommand GetBakedCommand(string name)
        {
            _bakedCommands.TryGetValue(name, out var command);
            return command;
        }

        public bool IsBakedCommand(string name)
        {
            return !string.IsNullOrEmpty(name) && _bakedCommands.ContainsKey(name);
        }

        /// <summary>Load all baked tools from registry and register them.</summary>
        public void LoadBakedTools(BakedToolRegistry registry)
        {
            foreach (var meta in registry.GetAll())
            {
                if (string.Equals(meta.LifecycleState, "archived", System.StringComparison.Ordinal))
                    continue;

                var source = registry.GetSource(meta.Name);
                if (source == null) continue;
                try
                {
                    IRevitCommand command;
                    string error = null;
                    if (BakedToolRuntimeSource.HasMarker(source))
                    {
                        if (!BakedToolRuntimeSource.IsAllowedForSuggestionSource(meta.Source))
                        {
                            System.Diagnostics.Debug.WriteLine($"[RvtMcp] Skipped baked tool '{meta.Name}': runtime source marker is not allowed for source '{meta.Source}'.");
                            continue;
                        }

                        if (!BakedToolRuntimeCommandFactory.TryCreate(
                            meta.Name,
                            meta.Description,
                            meta.ParametersSchema,
                            source,
                            this,
                            out command,
                            out error))
                        {
                            System.Diagnostics.Debug.WriteLine($"[RvtMcp] Failed to load baked tool '{meta.Name}': {error}");
                            continue;
                        }
                    }
                    else
                    {
                        command = ToolCompiler.CompileAndLoad(source, out error);
                    }

                    if (command != null)
                    {
                        if (!string.Equals(command.Name, meta.Name, System.StringComparison.Ordinal))
                        {
                            System.Diagnostics.Debug.WriteLine($"[RvtMcp] Skipped baked tool '{meta.Name}': compiled command name '{command.Name}' did not match registry metadata.");
                            continue;
                        }

                        if (!RegisterBaked(command))
                        {
                            System.Diagnostics.Debug.WriteLine($"[RvtMcp] Skipped baked tool '{meta.Name}': command name collides with an existing command.");
                        }
                        else
                        {
                            _runtimeCache?.RegisterOrUpdate(new BakedToolRuntimeEntry(
                                meta.Name,
                                meta.DisplayName,
                                meta.Description,
                                meta.ParametersSchema,
                                meta.OutputChoice,
                                command));
                        }
                    }
                    else
                        System.Diagnostics.Debug.WriteLine($"[RvtMcp] Failed to load baked tool '{meta.Name}': {error}");
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RvtMcp] Error loading baked tool '{meta.Name}': {ex.Message}");
                }
            }
        }
    }
}
