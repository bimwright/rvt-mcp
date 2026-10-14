using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListExportSettingsHandler : IRevitCommand
    {
        public string Name => "list_export_settings";

        public string Description =>
            "List saved export/print configurations in the active document: DWG export setups " +
            "(ExportDWGSettings), named print settings (PrintSetting), and view/sheet sets " +
            "(ViewSheetSet, with the number of views in each set). Read-only.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""kind_filter"":{""type"":""string"",""enum"":[""all"",""dwg"",""print"",""view_sheet_set""],""default"":""all""},""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""max_results"":{""type"":""integer"",""default"":50,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }
            var kindFilter = request.Value<string>("kind_filter") ?? "all";
            var allowedKinds = new[] { "all", "dwg", "print", "view_sheet_set" };
            if (!allowedKinds.Contains(kindFilter, StringComparer.OrdinalIgnoreCase))
                return CommandResult.Fail("kind_filter must be one of: all, dwg, print, view_sheet_set.");
            if (!ResponsePaging.TryParse(request, "start_index", "max_results", 50, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);

            // ----- DWG export settings -----
            var dwgExportSettings = new List<object>();
            try
            {
                var dwgSetups = new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>()
                    .ToList();

                foreach (var setup in dwgSetups)
                {
                    try
                    {
                        dwgExportSettings.Add(new
                        {
                            id = RevitCompat.GetId(setup.Id).ToString(),
                            name = setup.Name
                        });
                    }
                    catch
                    {
                        // Skip a setup that fails introspection.
                    }
                }
            }
            catch
            {
                // ExportDWGSettings unavailable; leave list empty.
            }

            // ----- Named print settings -----
            var printSettings = new List<object>();
            try
            {
                var prints = new FilteredElementCollector(doc)
                    .OfClass(typeof(PrintSetting))
                    .Cast<PrintSetting>()
                    .ToList();

                foreach (var print in prints)
                {
                    try
                    {
                        printSettings.Add(new
                        {
                            id = RevitCompat.GetId(print.Id).ToString(),
                            name = print.Name
                        });
                    }
                    catch
                    {
                        // Skip a print setting that fails introspection.
                    }
                }
            }
            catch
            {
                // PrintSetting unavailable; leave list empty.
            }

            // ----- View/sheet sets -----
            var viewSheetSets = new List<object>();
            try
            {
                var sets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheetSet))
                    .Cast<ViewSheetSet>()
                    .ToList();

                foreach (var set in sets)
                {
                    try
                    {
                        int viewCount = 0;
                        try
                        {
                            var views = set.Views;
                            if (views != null)
                                viewCount = views.Size;
                        }
                        catch
                        {
                            viewCount = 0;
                        }

                        viewSheetSets.Add(new
                        {
                            id = RevitCompat.GetId(set.Id).ToString(),
                            name = set.Name,
                            view_count = viewCount
                        });
                    }
                    catch
                    {
                        // Skip a view/sheet set that fails introspection.
                    }
                }
            }
            catch
            {
                // ViewSheetSet unavailable; leave list empty.
            }

            var includeDwg = kindFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || kindFilter.Equals("dwg", StringComparison.OrdinalIgnoreCase);
            var includePrint = kindFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || kindFilter.Equals("print", StringComparison.OrdinalIgnoreCase);
            var includeSets = kindFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || kindFilter.Equals("view_sheet_set", StringComparison.OrdinalIgnoreCase);
            var dwgPage = ResponsePaging.Slice(includeDwg ? dwgExportSettings : new List<object>(), paging.StartIndex, paging.MaxResults);
            var printPage = ResponsePaging.Slice(includePrint ? printSettings : new List<object>(), paging.StartIndex, paging.MaxResults);
            var setPage = ResponsePaging.Slice(includeSets ? viewSheetSets : new List<object>(), paging.StartIndex, paging.MaxResults);
            var nextIndices = new[] { dwgPage.NextIndex, printPage.NextIndex, setPage.NextIndex }.Where(i => i.HasValue).Select(i => i.Value).ToArray();

            return CommandResult.Ok(new
            {
                doc_title = doc.Title,
                kind_filter = kindFilter,
                start_index = paging.StartIndex,
                max_results = paging.MaxResults,
                counts = new { dwg = dwgPage.TotalCount, print = printPage.TotalCount, view_sheet_set = setPage.TotalCount },
                truncated = dwgPage.Truncated || printPage.Truncated || setPage.Truncated,
                next_index = nextIndices.Length > 0 ? (int?)nextIndices.Max() : null,
                dwg_export_settings = dwgPage.Items,
                print_settings = printPage.Items,
                view_sheet_sets = setPage.Items
            });
        }
    }
}
