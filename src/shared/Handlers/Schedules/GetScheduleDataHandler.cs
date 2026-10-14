using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetScheduleDataHandler : IRevitCommand
    {
        public string Name => "get_schedule_data";
        public string Description => "Get the rendered tabular content of a schedule (header row + body rows) with pagination. Optional cell metadata (cell type + merged cells).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""startRow"":{""type"":""integer"",""default"":0,""minimum"":0},""maxRows"":{""type"":""integer"",""default"":200,""minimum"":1,""maximum"":500},""includeCellMeta"":{""type"":""boolean"",""default"":false}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var scheduleIdToken = request["scheduleId"];
            var scheduleName = request.Value<string>("scheduleName");
            var startRow = request.Value<int?>("startRow") ?? 0;
            var maxRows = request.Value<int?>("maxRows") ?? 200;
            var includeCellMeta = request.Value<bool?>("includeCellMeta") ?? false;

            if (startRow < 0)
                return CommandResult.Fail("startRow must be at least 0.");
            if (maxRows < 1 || maxRows > 500)
                return CommandResult.Fail("maxRows must be between 1 and the hard maximum of 500.");

            // Resolve ViewSchedule by id or name
            ViewSchedule schedule = null;
            if (scheduleIdToken != null && scheduleIdToken.Type != JTokenType.Null)
            {
                long idValue;
                try { idValue = scheduleIdToken.Value<long>(); }
                catch { return CommandResult.Fail("scheduleId must be an integer."); }

                if (!RevitCompat.CanRepresentElementId(idValue))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(idValue));

                var el = doc.GetElement(RevitCompat.ToElementId(idValue));
                schedule = el as ViewSchedule;
                if (schedule == null)
                    return CommandResult.Fail($"Element {idValue} is not a ViewSchedule or not found.");
            }
            else if (!string.IsNullOrEmpty(scheduleName))
            {
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => s.Name != null &&
                                s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail($"Schedule '{scheduleName}' not found.");
                if (matches.Count > 1)
                    return CommandResult.Fail($"Ambiguous schedule name '{scheduleName}': {matches.Count} matches found. Use scheduleId.");
                schedule = matches[0];
            }
            else
            {
                return CommandResult.Fail("Either scheduleId or scheduleName is required.");
            }

            string warning = null;

            TableData tableData;
            try { tableData = schedule.GetTableData(); }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to get table data: {ex.Message}");
            }
            if (tableData == null)
                return CommandResult.Fail("Schedule has no table data.");

            TableSectionData headerSection = null;
            TableSectionData bodySection = null;
            try { headerSection = tableData.GetSectionData(SectionType.Header); } catch { }
            try { bodySection = tableData.GetSectionData(SectionType.Body); } catch { }

            if (bodySection == null)
                return CommandResult.Fail("Schedule body section is unavailable.");

            int nCols = 0;
            int nRows = 0;
            try { nCols = bodySection.NumberOfColumns; } catch { }
            try { nRows = bodySection.NumberOfRows; } catch { }

            bool headerHasRows = false;
            if (headerSection != null)
            {
                try { headerHasRows = headerSection.NumberOfRows > 0; } catch { headerHasRows = false; }
            }

            // Build columns list from the schedule fields, then fall back to the final header row.
            var columns = new string[nCols];
            var definition = schedule.Definition;
            IList<ScheduleFieldId> fieldOrder = null;
            try { fieldOrder = definition?.GetFieldOrder(); } catch { fieldOrder = null; }
            for (int c = 0; c < nCols; c++)
            {
                string colText = string.Empty;
                try
                {
                    if (fieldOrder != null && c < fieldOrder.Count)
                    {
                        var field = definition.GetField(fieldOrder[c]);
                        colText = field.ColumnHeading;
                        if (string.IsNullOrEmpty(colText)) colText = field.GetName();
                    }
                    if (string.IsNullOrEmpty(colText) && headerHasRows)
                    {
                        var headerRow = headerSection.NumberOfRows - 1;
                        colText = headerSection.GetCellText(headerRow, c) ?? string.Empty;
                    }
                }
                catch
                {
                    colText = string.Empty;
                    if (warning == null)
                        warning = "Some cells failed to render and were returned as empty strings.";
                }
                columns[c] = colText;
            }

            // Body section rows are schedule body rows; do not guess/drop row 0 as a header.
            int dataStartOffset = 0;

            int totalDataRows = nRows - dataStartOffset;
            if (totalDataRows < 0) totalDataRows = 0;

            // Compute row window (in body-section absolute indices)
            int absStart = startRow + dataStartOffset;
            int absEndExclusive = absStart + maxRows;
            if (absEndExclusive > nRows) absEndExclusive = nRows;
            if (absStart > nRows) absStart = nRows;

            var rows = new List<string[]>();
            var rowKinds = new List<string>();
            var cellMeta = includeCellMeta ? new List<object[]>() : null;

            for (int r = absStart; r < absEndExclusive; r++)
            {
                var rowArr = new string[nCols];
                bool textOnly = true;
                object[] metaArr = includeCellMeta ? new object[nCols] : null;

                for (int c = 0; c < nCols; c++)
                {
                    string text = string.Empty;
                    try { text = schedule.GetCellText(SectionType.Body, r, c) ?? string.Empty; }
                    catch
                    {
                        text = string.Empty;
                        if (warning == null)
                            warning = "Some cells failed to render and were returned as empty strings.";
                    }
                    rowArr[c] = text;
                    if (bodySection.GetCellType(r, c) != CellType.Text) textOnly = false;

                    if (includeCellMeta)
                    {
                        string cellType = null;
                        bool isMerged = false;
                        int? anchorRow = null;
                        int? anchorCol = null;
                        bool isAnchor = false;

                        try { cellType = bodySection.GetCellType(r, c).ToString(); }
                        catch
                        {
                            // TODO: Cell type API may differ across Revit versions.
                            cellType = null;
                        }

                        try
                        {
                            var merged = bodySection.GetMergedCell(r, c);
                            if (merged != null)
                            {
                                int top = merged.Top;
                                int bottom = merged.Bottom;
                                int left = merged.Left;
                                int right = merged.Right;
                                int rowSpan = bottom - top + 1;
                                int colSpan = right - left + 1;
                                if (rowSpan > 1 || colSpan > 1)
                                {
                                    isMerged = true;
                                    if (r == top && c == left)
                                    {
                                        isAnchor = true;
                                        anchorRow = top - dataStartOffset;
                                        anchorCol = left;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // TODO: GetMergedCell API may differ across Revit versions.
                        }

                        if (isAnchor)
                        {
                            metaArr[c] = new
                            {
                                cellType = cellType,
                                isMerged = isMerged,
                                mergeAnchor = new { row = anchorRow, col = anchorCol }
                            };
                        }
                        else
                        {
                            metaArr[c] = new
                            {
                                cellType = cellType,
                                isMerged = isMerged
                            };
                        }
                    }
                }

                rows.Add(rowArr);
                rowKinds.Add(rowArr.All(string.IsNullOrWhiteSpace) ? "blank"
                    : textOnly && rowArr.SequenceEqual(columns) ? "header"
                    : textOnly ? "text" : "data");
                if (includeCellMeta) cellMeta.Add(metaArr);
            }

            int returnedRows = rows.Count;
            bool truncated = (absStart + returnedRows) < nRows;

            // Edge case: nRows == 0 (no data at all) → truncated must be false
            if (nRows == 0) truncated = false;

            if (includeCellMeta)
            {
                return CommandResult.Ok(new
                {
                    scheduleId = RevitCompat.GetId(schedule.Id),
                    scheduleName = schedule.Name,
                    columns = columns,
                    totalRows = totalDataRows,
                    startRow = startRow,
                    returnedRows = returnedRows,
                    truncated = truncated,
                    rows = rows.ToArray(),
                    rowKinds = rowKinds.ToArray(),
                    cellMeta = cellMeta.ToArray(),
                    warning = warning
                });
            }

            return CommandResult.Ok(new
            {
                scheduleId = RevitCompat.GetId(schedule.Id),
                scheduleName = schedule.Name,
                columns = columns,
                totalRows = totalDataRows,
                startRow = startRow,
                returnedRows = returnedRows,
                truncated = truncated,
                rows = rows.ToArray(),
                rowKinds = rowKinds.ToArray(),
                warning = warning
            });
        }
    }
}
