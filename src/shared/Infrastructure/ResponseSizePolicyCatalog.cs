#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Agent-facing recovery guidance for commands whose inline response can be large.
    /// Parameter names are MCP-facing names from Program.cs, not generic guesses.
    /// </summary>
    public static class ResponseSizePolicyCatalog
    {
        private const string FallbackHint =
            "Retry with a smaller explicit ID list or a more selective filter supported by this tool.";

        private static readonly Dictionary<string, string> NarrowingHints =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["get_available_family_types"] = "Retry with a narrower `category`.",
            ["ai_element_filter"] = "Retry with `parameterName`, `parameterValue`, and a smaller `limit`.",
            ["get_element_details"] = "Retry with fewer `elementIds`.",
            ["get_element_parameters"] = "Retry with fewer `elementIds`; set `includeReadOnly=false` when possible.",
            ["get_type_parameters"] = "Retry with fewer `elementIds` or `typeIds`.",
            ["list_project_parameters"] = "Retry with `includeCategories=false`.",
            ["get_element_relationships"] = "Retry with fewer `elementIds` or `includeDependents=false`.",
            ["survey_change_impact"] = "Use fewer `elementIds`, a smaller `maxIdsPerRelation`, or `depth=1`; omitted evidence remains explicit. View iteration is opt-in through `maxViews`.",
            ["list_groups"] = "Retry with a narrower `groupKind` and `includeMembers=false`.",
            ["list_schedules"] = "Retry with `categoryFilter` and `namePattern`.",
            ["get_schedule_definition"] = "Retry with one exact `scheduleId`.",
            ["get_schedule_data"] = "Retry with `scheduleId`, `startRow`, a smaller `maxRows`, and `includeCellMeta=false`.",
            ["get_schedule_formulas"] = "Retry with one exact `scheduleId`.",
            ["get_schedulable_fields"] = "Retry with `scheduleId` and a narrower `kindFilter`.",
            ["find_schedule_elements"] = "Retry with `scheduleId`, `includeParameters=false`, and a smaller `limit`.",
            ["list_loaded_families"] = "Retry with `categoryFilter`, `kindFilter`, `includeInstanceCount=false`, and a smaller `limit`.",
            ["replace_family_type"] = "Retry with `scope=view`, an exact `viewId`, or `dryRun=true`.",
            ["get_family_instances"] = "Retry with exact `familyId`, `viewOnly=true`, and a smaller `limit`.",
            ["create_group_from_elements"] = "Retry with fewer `elementIds`.",
            ["set_element_parameter_values"] = "Split `elementIds` into smaller batches.",
            ["set_type_parameter_values"] = "Split `typeIds` or `elementIds` into smaller batches.",
            ["change_element_type"] = "Split `elementIds` into smaller batches.",
            ["assign_elements_to_workset"] = "Split `elementIds` into smaller batches.",
            ["delete_element"] = "Split `elementIds` into smaller batches.",
            ["export_pdf"] = "Retry with fewer `viewIds`.",
            ["export_dwg"] = "Retry with fewer `viewIds`.",
            ["export_dgn"] = "Retry with fewer `viewIds`.",
            ["export_dwf"] = "Retry with fewer `viewIds`.",
            ["batch_export_sheets"] = "Retry with fewer `sheetIds` or a narrower `sheetNumberFilter`.",
            ["create_view_sheet_set"] = "Retry with fewer `viewIds`.",
            ["tag_elements"] = "Split `elementIds` into smaller batches and scope with `viewId`.",
            ["tag_all_by_category"] = "Retry with an exact `viewId`, `dryRun=true`, and a smaller `limit`.",
            ["create_dimensions"] = "Retry with fewer `references` and one exact `viewId`.",
            ["list_keynotes"] = "Retry with `keyPrefix`, `search`, and a smaller `limit`.",
            ["apply_keynote_to_element"] = "Split `elementIds` into smaller batches or use `dryRun=true`.",
            ["find_untagged_elements"] = "Retry with `category`, an exact `viewId`, and a smaller `limit`.",
            ["find_undimensioned_elements"] = "Retry with `category`, an exact `viewId`, and a smaller `limit`.",
            ["wipe_empty_tags"] = "Retry with an exact `viewId`, `dryRun=true`, and a smaller `limit`.",
            ["list_mep_systems"] = "Retry with `domainFilter` and a smaller `limit`.",
            ["get_system_inventory"] = "Retry with exact `systemId`, `includeParameters=false`, and a smaller `limit`.",
            ["set_system_classification"] = "Split `elementIds` into smaller batches.",
            ["find_mep_disconnects"] = "Retry with `domainFilter`, `viewOnly=true`, and a smaller `limit`.",
            ["create_view_filter"] = "Retry with fewer `categories` or rules.",
            ["list_view_filters"] = "Retry with exact `viewId` and `includeUsage=false`.",
            ["override_element_graphics"] = "Split `elementIds` into smaller batches and scope with `viewId`.",
            ["clear_element_overrides"] = "Split `elementIds` into smaller batches and scope with `viewId`.",
            ["get_view_visibility"] = "Retry with exact `viewId` and `includeCategoryList=false`.",
            ["set_category_visibility"] = "Split `categories` into smaller batches and scope with `viewId`.",
            ["set_element_phase"] = "Split `elementIds` into smaller batches.",
            ["purge_unused"] = "Retry with narrower `targets` and a smaller `limit`.",
            ["list_rebar"] = "Retry with exact `host_id` or `view_id` and a smaller `limit`.",
            ["get_structural_loads"] = "Retry with exact `element_id`, `load_type`, and a smaller `limit`.",
            ["analyze_structural_connections"] = "Retry with fewer `element_ids` and a smaller `limit`.",
            ["get_model_warnings_summary"] = "Retry with `include_examples=false`, a smaller `max_examples_per_type`, and a smaller `max_warning_types`.",
            ["duplicate_sheet"] = "Retry with `includeSchedules=false` and `includeRevisions=false`.",
            ["list_sheets"] = "Retry with `numberFilter`, `namePattern`, fewer include flags, and a smaller `limit`.",
            ["set_titleblock_parameters"] = "Split `parameters` into smaller calls and target one `sheetId`.",
            ["get_titleblock_parameters"] = "Target one `sheetId`; set `includeReadOnly=false`.",
            ["list_titleblocks"] = "Retry with `namePattern`, `includeInactive=false`, and a smaller `limit`.",
            ["assign_revision_to_sheet"] = "Split `sheetIds` into smaller batches.",
            ["list_revisions"] = "Retry with `includeSheets=false`.",
            ["renumber_sheets"] = "Split `items` into smaller batches or use `dryRun=true`.",
            ["list_materials"] = "Retry with `namePattern`, `classFilter`, fewer include flags, and a smaller `limit`.",
            ["get_material_properties"] = "Target one `materialId`; set `includeAssets=false` or `includeParameters=false`.",
            ["assign_material_to_element"] = "Split `elementIds` into smaller batches.",
            ["get_element_bounding_box"] = "Retry with fewer `elementIds` and an exact `viewId`.",
            ["get_element_geometry"] = "Retry with fewer `elementIds`, lower `detailLevel`, `includeSamples=false`, or a smaller `sampleLimit`.",
            ["clash_detection"] = "Retry with narrower `categoriesA`/`categoriesB`, exact `viewId`, smaller `maxPairs`, and smaller `maxResults`.",
            ["find_elements_in_volume"] = "Retry with exact `roomId` or narrower `categories`/`viewId` and a smaller `limit`.",
            ["compute_element_volume"] = "Retry with fewer `elementIds` and lower `detailLevel`.",
            ["compute_element_area"] = "Retry with fewer `elementIds` and lower `detailLevel`.",
            ["find_overlapping_elements"] = "Retry with exact `viewId`, smaller `maxPairs`, and smaller `maxResults`.",
            ["get_element_centroid"] = "Retry with fewer `elementIds`.",
            ["analyze_geometry_complexity"] = "Retry with fewer `elementIds`, narrower `categories`/`viewId`, lower `detailLevel`, and a smaller `limit`.",
            ["list_rooms"] = "Retry with `levelName`, `phaseName`, `status`, `includeParameters=false`, and a smaller `limit`.",
            ["get_room_boundaries"] = "Target one `roomId` and set `includeBoundaryElements=false`.",
            ["get_room_openings"] = "Target one `roomId` and disable unneeded `includeDoors`/`includeWindows`.",
            ["list_areas"] = "Retry with `areaSchemeName`, `levelName`, `status`, and a smaller `limit`.",
            ["auto_create_rooms_from_walls"] = "Retry with exact `levelName`/`phaseName`, `dryRun=true`, and a smaller `limit`.",
            ["get_link_elements"] = "Retry with exact `linkInstanceId`, `category`, `includeBoundingBox=false`, and a smaller `limit`.",
            ["list_shared_parameters"] = "Retry with `groupName`, `includeBindings=false`, and a smaller `limit`.",
            ["bind_shared_parameter"] = "Split `categories` into smaller batches and target one `guid`.",
            ["list_project_parameter_bindings"] = "Retry with `nameFilter`/`guid`, fewer include flags, and a smaller `limit`.",
            ["remove_parameter_binding"] = "Split `categories` into smaller batches or use `dryRun=true`.",
            ["set_parameter_value_by_guid"] = "Split `elementIds` into smaller batches and target one `guid`.",
            ["list_view_templates"] = "Retry with `viewType`/`viewId`, fewer include flags, and a smaller `limit`.",
            ["apply_view_template"] = "Split `viewIds` into smaller batches and target one `templateId`.",
            ["list_saved_selections"] = "Retry with `nameFilter`, `includeElementIds=false`, `includeElementSummary=false`, and a smaller `limit`.",
            ["select_elements"] = "Retry with fewer `elementIds` or one exact `savedSelectionId`.",
            ["workflow_clash_review"] = "Retry with narrower `category_a`/`category_b`, exact `view_id`, and smaller `max_pairs`.",
            ["workflow_model_audit"] = "Disable unneeded sections and lower `limit_per_section`.",
            ["workflow_room_documentation"] = "Retry with fewer `room_ids`, exact `level_name`/`sheet_id`, and a smaller `limit`.",
            ["workflow_sheet_set"] = "Split `sheets` into smaller batches.",
            ["workflow_view_cleanup"] = "Disable unneeded include flags and lower `limit`.",
            ["workflow_naming_normalization"] = "Retry with narrower `target`/`pattern`, fewer `ids`, and a smaller `limit`.",

            // Survey group 3: scope added in oversized-response hardening step 1.
            ["get_selected_elements"] = "Retry with `startIndex` and a smaller `maxResults` (hard maximum 1000).",
            ["get_material_quantities"] = "Retry with `materialNameFilter`, `startIndex`, and a smaller `maxResults` (hard maximum 1000).",
            ["get_group_members"] = "Retry with `startIndex` and a smaller `maxResults` (hard maximum 1000).",
            ["list_assemblies"] = "Retry with `startIndex`, smaller `maxResults`, and smaller `maxMembersPerAssembly`.",
            ["get_assembly_members"] = "Retry with `startIndex` and a smaller `maxResults` (hard maximum 1000).",
            ["load_family_from_path"] = "Retry with `includeSymbols=false` or a smaller `maxSymbolResults`.",
            ["audit_families"] = "Retry with `startIndex`, a smaller `limitPerSection`, and disable unneeded sections.",
            ["list_family_types_in_family"] = "Retry with `startIndex`, smaller `maxTypes`, `parameterNames`, or `includeParameterValues=false`.",
            ["color_elements"] = "Use a smaller `maxGroups`; the mutation completed and only response detail is truncated.",
            ["analyze_sheet_layout"] = "Retry with `startViewport` and a smaller `maxViewports`.",
            ["list_export_settings"] = "Retry with `kindFilter`, `startIndex`, and a smaller `maxResults`.",
            ["get_print_settings"] = "Retry with `kindFilter`, `startIndex`, and a smaller `maxResults`.",
            ["detect_system_elements"] = "Page returned element IDs with `startElement` and a smaller `maxElements`.",
            ["get_panel_schedule"] = "Retry with `startCircuit` and a smaller `maxCircuits`.",
            ["show_message"] = "Retry with `echoMessage=false` or a smaller `maxEchoChars`; the dialog was already shown.",
            ["list_baked_tools"] = "Retry with `nameFilter`, `startIndex`, and a smaller `limit`.",
            ["analyze_view_naming_patterns"] = "Retry with smaller `maxPatterns`; page outliers with `startOutlier` and smaller `maxOutliers`.",
            ["tag_all_areas"] = "Retry with a smaller `limit`; the response returns a compact mutation summary.",
            ["delete_view_template"] = "Retry with a smaller `maxUsedByViews`; mutation responses remain compact.",
            ["save_selection"] = "Retry with `includeElementIds=false` or a smaller `maxElementIdResults`.",
            ["load_selection"] = "Retry with `startIndex`, a smaller `maxResults`, or `includeElementSummary=false`.",
            ["apply_bake"] = "Bake completed; source and DLL bodies are omitted in favor of hashes and byte counts.",

            // Survey group 4: inline retains step-1 behavior; output=file uses local spill.
            ["export_room_data"] = "Retry with `output=file` for the full local SQLite artifact, or use `list_rooms` with filters and a smaller limit.",
            ["batch_execute"] = "Use `output=file` for NDJSON sub-results or split `commands`; never blindly retry a completed batch mutation.",
            ["run_baked_tool"] = "Use `output=file` to force a local artifact or narrow the baked tool's `params`; oversized inline output auto-spills.",
            ["export_shared_parameter_file"] = "Retry with `output=file` for the full local JSON artifact; do not retry the same unscoped inline request.",
            ["get_material_takeoff"] = "Use `output=file` for local SQLite, or narrow `categoryFilter`/`materialNamePattern` and lower `elementLimit`.",
            ["compute_room_finishes"] = "Use `output=file` for local SQLite, or use fewer `roomIds`, exact `levelName`, and a smaller `limit`.",
            ["workflow_data_roundtrip"] = "Use `output=file` for an NDJSON report, or use fewer `parameter_names`/a smaller input file.",
            ["workflow_takeoff_report"] = "Use `output=file` for local SQLite, or use fewer `categories` and lower `limit_per_category`."
        };

        public static string GetNarrowingHint(string? commandName)
        {
            if (commandName == null || commandName.Trim().Length == 0)
                return FallbackHint;

            return NarrowingHints.TryGetValue(commandName, out var hint)
                ? hint
                : FallbackHint;
        }

        public static bool IsMutationOutcomeIndeterminate(string? commandName)
        {
            return string.Equals(commandName, "send_code_to_revit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "run_baked_tool", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldPreserveSuccessfulMutation(
            string? commandName,
            string? paramsJson,
            bool classifiedAsWrite)
        {
            if (string.Equals(commandName, "export_room_data", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "export_shared_parameter_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "get_material_takeoff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "compute_room_finishes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "workflow_takeoff_report", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var parameters = JObject.Parse(paramsJson ?? "{}");
                    return string.Equals(parameters.Value<string>("output"), "file", StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(commandName, "workflow_takeoff_report", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(parameters.Value<string>("output_path")));
                }
                catch { return false; }
            }
            if (string.Equals(commandName, "workflow_model_audit", StringComparison.OrdinalIgnoreCase)) return false;

            return classifiedAsWrite || HasUiSideEffect(commandName, paramsJson);
        }

        public static string? GetOperationError(string? commandName, JObject? data)
        {
            // Takeoff returns a workflow DTO even when its optional file export fails.
            // Transport success alone must not promote that failed write to success.
            if (!string.Equals(commandName, "workflow_takeoff_report", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(data?.Value<string>("status"), "failed", StringComparison.OrdinalIgnoreCase))
                return null;

            if (data?["steps"] is JArray steps)
                foreach (var step in steps)
                    if (step is JObject item
                        && string.Equals(item.Value<string>("status"), "failed", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(item.Value<string>("error")))
                        return item.Value<string>("error");
            return "Takeoff report export failed.";
        }

        public static bool HasUiSideEffect(string? commandName, string? paramsJson)
        {
            if (string.Equals(commandName, "show_message", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "select_elements", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "activate_view", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandName, "show_element_in_view", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(commandName, "ai_element_filter", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                var json = paramsJson == null || paramsJson.Trim().Length == 0 ? "{}" : paramsJson;
                return JObject.Parse(json).Value<bool?>("select") ?? false;
            }
            catch
            {
                return false;
            }
        }
    }
}
