using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetPrintSettingsHandler : IRevitCommand
    {
        public string Name => "get_print_settings";

        public string Description =>
            "Report the active document's PrintManager state (target printer, print-to-file flag, " +
            "print range) plus all named print settings (PrintSetting, with paper size and page " +
            "orientation) and view/sheet sets (ViewSheetSet). Read-only.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""kind_filter"":{""type"":""string"",""enum"":[""all"",""print"",""view_sheet_set""],""default"":""all""},""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""max_results"":{""type"":""integer"",""default"":50,""minimum"":1,""maximum"":500}}}";

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
            var allowedKinds = new[] { "all", "print", "view_sheet_set" };
            if (!allowedKinds.Contains(kindFilter, StringComparer.OrdinalIgnoreCase))
                return CommandResult.Fail("kind_filter must be one of: all, print, view_sheet_set.");
            if (!ResponsePaging.TryParse(request, "start_index", "max_results", 50, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);

            // ----- PrintManager state -----
            // PrintManager properties can throw when no printer is configured;
            // each read is guarded independently so a partial state is still reported.
            bool? printToFile = null;
            string selectedPrinter = null;
            string printRange = null;

            try
            {
                var pm = doc.PrintManager;

                try
                {
                    printToFile = pm.PrintToFile;
                }
                catch
                {
                    printToFile = null;
                }

                try
                {
                    selectedPrinter = pm.PrinterName;
                }
                catch
                {
                    selectedPrinter = null;
                }

                try
                {
                    printRange = pm.PrintRange.ToString();
                }
                catch
                {
                    printRange = null;
                }
            }
            catch
            {
                // PrintManager itself unavailable; leave all reads null.
            }

            // ----- Named print settings -----
            var namedPrintSettings = new List<object>();
            try
            {
                var settings = new FilteredElementCollector(doc)
                    .OfClass(typeof(PrintSetting))
                    .Cast<PrintSetting>()
                    .ToList();

                foreach (var setting in settings)
                {
                    try
                    {
                        string paperSize = null;
                        string orientation = null;

                        try
                        {
                            var prm = setting.PrintParameters;
                            if (prm != null)
                            {
                                try
                                {
                                    var paper = prm.PaperSize;
                                    if (paper != null)
                                        paperSize = paper.Name;
                                }
                                catch
                                {
                                    paperSize = null;
                                }

                                try
                                {
                                    orientation = prm.PageOrientation.ToString();
                                }
                                catch
                                {
                                    orientation = null;
                                }
                            }
                        }
                        catch
                        {
                            // PrintParameters unreadable for this setting.
                        }

                        namedPrintSettings.Add(new
                        {
                            id = RevitCompat.GetId(setting.Id).ToString(),
                            name = setting.Name,
                            paper_size = paperSize,
                            orientation = orientation
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
                        viewSheetSets.Add(new
                        {
                            id = RevitCompat.GetId(set.Id).ToString(),
                            name = set.Name
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

            var includePrint = kindFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || kindFilter.Equals("print", StringComparison.OrdinalIgnoreCase);
            var includeSets = kindFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || kindFilter.Equals("view_sheet_set", StringComparison.OrdinalIgnoreCase);
            var printPage = ResponsePaging.Slice(includePrint ? namedPrintSettings : new List<object>(), paging.StartIndex, paging.MaxResults);
            var setPage = ResponsePaging.Slice(includeSets ? viewSheetSets : new List<object>(), paging.StartIndex, paging.MaxResults);
            var nextIndices = new[] { printPage.NextIndex, setPage.NextIndex }.Where(i => i.HasValue).Select(i => i.Value).ToArray();

            return CommandResult.Ok(new
            {
                doc_title = doc.Title,
                print_to_file = printToFile,
                selected_printer = selectedPrinter,
                print_range = printRange,
                kind_filter = kindFilter,
                start_index = paging.StartIndex,
                max_results = paging.MaxResults,
                counts = new { print = printPage.TotalCount, view_sheet_set = setPage.TotalCount },
                truncated = printPage.Truncated || setPage.Truncated,
                next_index = nextIndices.Length > 0 ? (int?)nextIndices.Max() : null,
                named_print_settings = printPage.Items,
                view_sheet_sets = setPage.Items
            });
        }
    }
}
