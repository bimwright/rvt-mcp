using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Builds human-readable toast copy from MCP call data (richer than session-log Summary).
    /// </summary>
    public static class ToastContentBuilder
    {
        public static McpToastViewModel BuildCompleted(
            string toolName,
            string paramsJson,
            string resultJson,
            bool success,
            string errorMessage,
            long durationMs,
            string toolDescription)
        {
            if (string.IsNullOrEmpty(toolName))
                toolName = "unknown";

            var kind = ToolActivityClassifier.Classify(toolName);
            var content = success
                ? BuildSuccessContent(toolName, paramsJson, resultJson, kind)
                : BuildFailureContent(toolName, errorMessage, toolDescription);

            return new McpToastViewModel
            {
                CommandName = toolName,
                Title = ToolNameFormatter.Format(toolName),
                CategoryLabel = content.CategoryLabel,
                Summary = content.Summary,
                Detail = content.Detail,
                ThumbnailPath = content.ThumbnailPath,
                Kind = kind,
                Success = success,
                DurationMs = durationMs
            };
        }

        private static ToastContent BuildSuccessContent(string toolName, string paramsJson, string resultJson, ToolActivityKind kind)
        {
            JObject result = null;
            JObject parms = null;
            try { if (!string.IsNullOrEmpty(resultJson)) result = JObject.Parse(resultJson); } catch { }
            try { if (!string.IsNullOrEmpty(paramsJson)) parms = JObject.Parse(paramsJson); } catch { }

            var category = CategoryFor(kind, toolName);

            switch (toolName)
            {
                case "capture_view_image":
                    return BuildCaptureImageSuccess(category, result);
                case "get_current_view_info":
                    return BuildCurrentViewSuccess(category, result);
                case "list_rooms":
                    return BuildListRoomsSuccess(category, result, parms);
                case "list_sheets":
                    return BuildListSheetsSuccess(category, result);
                case "list_worksets":
                    return BuildListWorksetsSuccess(category, result);
                case "ai_element_filter":
                    return BuildAiFilterSuccess(category, result);
                case "get_selected_elements":
                    return BuildSelectedSuccess(category, result);
                case "send_code_to_revit":
                    return BuildSendCodeSuccess(category, result);
                default:
                    return BuildGenericSuccess(category, toolName, result);
            }
        }

        private static ToastContent BuildFailureContent(string toolName, string errorMessage, string toolDescription)
        {
            var category = L.T("toast.category.failed");
            var summary = Truncate(errorMessage ?? L.T("toast.failed.default"), 120);
            var detail = !string.IsNullOrWhiteSpace(toolDescription)
                ? Truncate(toolDescription, 100)
                : ToolNameFormatter.Format(toolName);
            return new ToastContent(category, summary, detail);
        }

        private static ToastContent BuildCaptureImageSuccess(string category, JObject result)
        {
            var savedPath = result?.Value<string>("saved_path");
            var pixelSize = result?.Value<int?>("pixel_size");
            var format = (result?.Value<string>("image_format") ?? "png").ToUpperInvariant();
            var viewId = result?.Value<long?>("view_id");

            var fileName = GetFileNameSafe(savedPath, L.T("toast.capture.imageFallback"));
            var summary = pixelSize.HasValue
                ? L.T("toast.capture.savedPx", ("fileName", fileName), ("pixelSize", pixelSize.Value), ("format", format))
                : L.T("toast.capture.saved", ("fileName", fileName));

            var thumb = IsSafeImagePath(savedPath) ? savedPath : null;
            var detailParts = new System.Collections.Generic.List<string>();
            if (viewId.HasValue)
                detailParts.Add(L.T("toast.capture.viewId", ("viewId", viewId.Value)));
            if (thumb != null)
                detailParts.Add(L.T("toast.capture.clickToOpen"));
            var detail = string.Join(" · ", detailParts);
            return new ToastContent(category, summary, detail, thumb);
        }

        private static ToastContent BuildCurrentViewSuccess(string category, JObject result)
        {
            var viewName = result?.Value<string>("viewName") ?? result?.Value<string>("view_name") ?? L.T("toast.currentView.active");
            var viewType = result?.Value<string>("viewType") ?? result?.Value<string>("view_type");
            var scale = result?.Value<int?>("scale");
            var level = result?.Value<string>("levelName") ?? result?.Value<string>("level_name");

            var summary = viewType != null ? $"{viewName} · {viewType}" : viewName;
            var detailParts = new System.Collections.Generic.List<string>();
            if (scale.HasValue && scale.Value > 0)
                detailParts.Add(L.T("toast.currentView.scale", ("scale", scale.Value)));
            if (!string.IsNullOrWhiteSpace(level))
                detailParts.Add(level);
            return new ToastContent(category, summary, string.Join(" · ", detailParts));
        }

        private static ToastContent BuildListRoomsSuccess(string category, JObject result, JObject parms)
        {
            var total = result?.Value<int?>("total");
            var returned = result?.Value<int?>("returned");
            var placed = result?["counts"]?.Value<int?>("placed");
            var unplaced = result?["counts"]?.Value<int?>("unplaced");

            var count = total ?? returned;
            var summary = count.HasValue
                ? (count.Value == 0 ? L.T("toast.rooms.none") : L.T("toast.rooms.found", ("count", count.Value)))
                : L.T("toast.rooms.complete");

            var detail = DescribeRoomFilters(parms);
            if (placed.HasValue || unplaced.HasValue)
            {
                var stats = L.T("toast.rooms.placedStats", ("placed", placed ?? 0), ("unplaced", unplaced ?? 0));
                detail = string.IsNullOrEmpty(detail) ? stats : $"{detail} · {stats}";
            }

            return new ToastContent(category, summary, detail);
        }

        private static ToastContent BuildListSheetsSuccess(string category, JObject result)
        {
            var total = result?.Value<int?>("total") ?? result?.Value<int?>("returned");
            var summary = total.HasValue
                ? (total.Value == 0 ? L.T("toast.sheets.none") : L.T("toast.sheets.found", ("count", total.Value)))
                : L.T("toast.sheets.ready");

            string detail = null;
            var sheets = result?["sheets"] as JArray;
            if (sheets != null && sheets.Count > 0)
            {
                var first = sheets[0];
                var num = first?.Value<string>("sheet_number");
                var name = first?.Value<string>("sheet_name");
                if (!string.IsNullOrWhiteSpace(num) || !string.IsNullOrWhiteSpace(name))
                    detail = L.T("toast.sheets.first", ("number", num), ("name", name)).Trim();
                if (sheets.Count > 1)
                    detail = (detail ?? L.T("toast.sheets.fallback")) + " " + L.T("toast.sheets.more", ("count", sheets.Count - 1));
            }

            return new ToastContent(category, summary, detail);
        }

        private static ToastContent BuildListWorksetsSuccess(string category, JObject result)
        {
            var isShared = result?.Value<bool?>("isWorkshared");
            if (isShared == false)
                return new ToastContent(category, L.T("toast.worksets.notWorkshared"), L.T("toast.worksets.none"));

            var count = result?.Value<int?>("count");
            var active = result?.Value<string>("activeWorksetName") ?? result?.Value<string>("active_workset_name");
            var summary = count.HasValue
                ? L.T("toast.worksets.found", ("count", count.Value))
                : L.T("toast.worksets.complete");
            var detail = !string.IsNullOrWhiteSpace(active) ? L.T("toast.worksets.active", ("name", active)) : null;
            return new ToastContent(category, summary, detail);
        }

        private static ToastContent BuildAiFilterSuccess(string category, JObject result)
        {
            var count = result?.Value<int?>("count");
            var categoryName = result?.Value<string>("category");
            var summary = count.HasValue
                ? L.T("toast.filter.matched", ("count", count.Value), ("category", categoryName ?? L.T("toast.filter.elementsFallback")))
                : L.T("toast.filter.complete");
            return new ToastContent(category, summary, null);
        }

        private static ToastContent BuildSelectedSuccess(string category, JObject result)
        {
            var count = result?.Value<int?>("count");
            var summary = count.HasValue
                ? L.T("toast.selected.count", ("count", count.Value))
                : L.T("toast.selected.done");
            return new ToastContent(category, summary, null);
        }

        private static ToastContent BuildSendCodeSuccess(string category, JObject result)
        {
            var text = result?.Value<string>("result");
            var summary = string.IsNullOrWhiteSpace(text) ? L.T("toast.sendCode.finished") : Truncate(FirstLine(text), 100);
            return new ToastContent(category, summary, L.T("toast.sendCode.detail"));
        }

        private static ToastContent BuildGenericSuccess(string category, string toolName, JObject result)
        {
            if (result == null)
                return new ToastContent(category, L.T("toast.generic.completed"), ToolNameFormatter.Format(toolName));

            var total = result.Value<int?>("total");
            var returned = result.Value<int?>("returned");
            var count = result.Value<int?>("count");
            var rowCount = result.Value<int?>("rowCount");
            var viewName = result.Value<string>("viewName") ?? result.Value<string>("view_name");
            var savedPath = result.Value<string>("saved_path") ?? result.Value<string>("path");

            if (total.HasValue || returned.HasValue)
            {
                var n = total ?? returned;
                return new ToastContent(category, L.T("toast.generic.results", ("count", n.Value)), ToolNameFormatter.Format(toolName));
            }
            if (count.HasValue)
                return new ToastContent(category, L.T("toast.generic.items", ("count", count.Value)), ToolNameFormatter.Format(toolName));
            if (rowCount.HasValue)
                return new ToastContent(category, L.T("toast.generic.rows", ("count", rowCount.Value)), ToolNameFormatter.Format(toolName));
            if (!string.IsNullOrWhiteSpace(viewName))
                return new ToastContent(category, viewName, ToolNameFormatter.Format(toolName));
            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                var thumb = IsSafeImagePath(savedPath) ? savedPath : null;
                return new ToastContent(category, GetFileNameSafe(savedPath, L.T("toast.generic.fileFallback")), ToolNameFormatter.Format(toolName), thumb);
            }

            var message = result.Value<string>("message") ?? result.Value<string>("summary");
            if (!string.IsNullOrWhiteSpace(message))
                return new ToastContent(category, Truncate(message, 100), ToolNameFormatter.Format(toolName));

            return new ToastContent(category, L.T("toast.generic.completed"), ToolNameFormatter.Format(toolName));
        }

        private static string CategoryFor(ToolActivityKind kind, string toolName)
        {
            if (toolName == "send_code_to_revit" || toolName == "batch_execute" || toolName == "run_baked_tool")
                return L.T("toast.category.script");
            if (toolName != null && toolName.StartsWith("export_", StringComparison.OrdinalIgnoreCase))
                return L.T("toast.category.export");
            if (toolName == "capture_view_image")
                return L.T("toast.category.snapshot");

            if (kind == ToolActivityKind.Write)
                return L.T("toast.category.modified");
            return L.T("toast.category.query");
        }

        private static string DescribeRoomFilters(JObject parms)
        {
            if (parms == null)
                return L.T("toast.filters.allLevelsPhases");

            var level = parms.Value<string>("level_name");
            var phase = parms.Value<string>("phase_name");
            var status = parms.Value<string>("status");
            var parts = new System.Collections.Generic.List<string>();
            parts.Add(string.IsNullOrWhiteSpace(level) ? L.T("toast.filters.allLevels") : L.T("toast.filters.level", ("level", level)));
            parts.Add(string.IsNullOrWhiteSpace(phase) ? L.T("toast.filters.allPhases") : L.T("toast.filters.phase", ("phase", phase)));
            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
                parts.Add(L.T("toast.filters.status", ("status", status)));
            return string.Join(" · ", parts);
        }

        internal static bool IsSafeImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                if (!File.Exists(path))
                    return false;

                var full = Path.GetFullPath(path);
                var ext = Path.GetExtension(full);
                if (ext == null)
                    return false;
                ext = ext.ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                    return false;

                return PathAllowlist.IsUnderTempOrCaptures(full);
            }
            catch
            {
                return false;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            var idx = text.IndexOf('\n');
            return idx < 0 ? text : text.Substring(0, idx);
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text ?? string.Empty;
            return text.Substring(0, max - 3) + "...";
        }

        private readonly struct ToastContent
        {
            public string CategoryLabel { get; }
            public string Summary { get; }
            public string Detail { get; }
            public string ThumbnailPath { get; }

            public ToastContent(string categoryLabel, string summary, string detail, string thumbnailPath = null)
            {
                CategoryLabel = categoryLabel ?? string.Empty;
                Summary = summary ?? string.Empty;
                Detail = detail ?? string.Empty;
                ThumbnailPath = thumbnailPath;
            }
        }

        private static string GetFileNameSafe(string path, string fallback)
        {
            if (string.IsNullOrEmpty(path))
                return fallback;
            if (path.Contains("<") || path.Contains(">") || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return fallback;
            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
