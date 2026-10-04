using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Creates a named ViewSheetSet (a saved set of views/sheets) for batch printing/exporting.
    /// ViewSheetSets are created via the PrintManager's ViewSheetSetting. The current set's
    /// Views collection is populated with a ViewSet and then persisted with SaveAs(name).
    /// PrintManager operations can throw, so they are wrapped defensively.
    /// </summary>
    public class CreateViewSheetSetHandler : IRevitCommand
    {
        public string Name => "create_view_sheet_set";

        public string Description =>
            "Create a named ViewSheetSet (a saved set of views/sheets) for batch printing/exporting.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""name"",""view_ids""],
  ""properties"":{
    ""name"":{""type"":""string"",""description"":""Name for the new view/sheet set.""},
    ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""ViewSheet/View ElementIds to include.""}
  }
}";

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
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var name = request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("name is required.");

            var viewIdsToken = request["view_ids"] as JArray;
            if (viewIdsToken == null || viewIdsToken.Count == 0)
                return CommandResult.Fail("view_ids is required and must contain at least one element id.");

            // Resolve each id to a View (ViewSheet derives from View); reject the whole request if any id is unusable.
            var resolvedViews = new List<View>();
            var skipped = new List<object>();
            foreach (var token in viewIdsToken)
            {
                long rawId;
                try
                {
                    rawId = token.Value<long>();
                }
                catch (Exception)
                {
                    skipped.Add(new { id = token?.ToString(), reason = "not an integer" });
                    continue;
                }

                if (!RevitCompat.CanRepresentElementId(rawId))
                {
                    skipped.Add(new { id = rawId, reason = RevitCompat.ElementIdRangeError(rawId) });
                    continue;
                }

                var element = doc.GetElement(RevitCompat.ToElementId(rawId));
                if (element == null)
                {
                    skipped.Add(new { id = rawId, reason = "element not found" });
                    continue;
                }

                var view = element as View;
                if (view == null)
                {
                    skipped.Add(new { id = rawId, reason = "element is not a view or sheet" });
                    continue;
                }

                if (view.IsTemplate)
                {
                    skipped.Add(new { id = rawId, reason = "view is a template" });
                    continue;
                }

                resolvedViews.Add(view);
            }

            if (skipped.Count > 0)
            {
                return CommandResult.Ok(new
                {
                    created = false,
                    error = skipped.Count + " of " + viewIdsToken.Count + " view_ids could not be used",
                    skipped
                });
            }

            if (resolvedViews.Count == 0)
                return CommandResult.Fail("None of the supplied view_ids resolved to a usable view or sheet.");

            using (var tx = new Transaction(doc, "RvtMcp: create view sheet set"))
            {
                try
                {
                    tx.Start();

                    var pm = doc.PrintManager;
                    pm.PrintRange = PrintRange.Select;

                    var vss = pm.ViewSheetSetting;

                    var viewSet = new ViewSet();
                    foreach (var view in resolvedViews)
                        viewSet.Insert(view);

                    vss.CurrentViewSheetSet.Views = viewSet;
                    vss.SaveAs(name);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create view sheet set: " + ex.Message);
                }
            }

            // Locate the persisted ViewSheetSet by name to report its id.
            long? setId = null;
            try
            {
                var created = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheetSet))
                    .Cast<ViewSheetSet>()
                    .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

                if (created != null)
                    setId = RevitCompat.GetId(created.Id);
            }
            catch (Exception)
            {
                setId = null;
            }

            return CommandResult.Ok(new
            {
                created = true,
                set_id = setId,
                name = name,
                view_count = resolvedViews.Count,
                error = (string)null
            });
        }
    }
}
