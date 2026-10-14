using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ApplyFilterToViewHandler : IRevitCommand
    {
        public string Name => "apply_filter_to_view";
        public string Description => "Add an existing ParameterFilterElement to a view's filter list, setting its initial visibility";
        public string ParametersSchema => @"{""type"":""object"",""required"":[""filter_id""],""properties"":{""filter_id"":{""type"":""integer"",""description"":""ParameterFilterElement ElementId.""},""view_id"":{""type"":""integer"",""description"":""Target view ElementId. If omitted, the active view is used.""},""visible"":{""type"":""boolean"",""default"":true,""description"":""Initial visibility of elements matching the filter.""}}}";

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

            var filterIdToken = request["filter_id"];
            if (filterIdToken == null || filterIdToken.Type == JTokenType.Null)
                return CommandResult.Fail("'filter_id' is required.");

            var filterIdRaw = filterIdToken.Value<long>();
            var viewIdRaw = request.Value<long?>("view_id");
            var visible = request.Value<bool?>("visible") ?? true;

            // Resolve the ParameterFilterElement.
            if (!RevitCompat.CanRepresentElementId(filterIdRaw))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(filterIdRaw));

            var filter = doc.GetElement(RevitCompat.ToElementId(filterIdRaw)) as ParameterFilterElement;
            if (filter == null)
                return Error($"ParameterFilterElement with ID {filterIdRaw} not found.", filterIdRaw, null, null, null, false);

            // Resolve the target view.
            View view;
            if (viewIdRaw.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdRaw.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdRaw.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewIdRaw.Value)) as View;
                if (view == null)
                    return Error($"View with ID {viewIdRaw.Value} not found.", filterIdRaw, filter.Name, null, null, false);
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return Error("No active view is available.", filterIdRaw, filter.Name, null, null, false);
            }

            var resolvedViewId = RevitCompat.GetId(view.Id);

            if (!view.AreGraphicsOverridesAllowed())
                return Error(
                    $"View '{view.Name}' ({view.ViewType}) does not allow graphics overrides; filters cannot be applied.",
                    filterIdRaw, filter.Name, resolvedViewId, view.Name, false);

            using (var tx = new Transaction(doc, "RvtMcp: apply filter to view"))
            {
                tx.Start();
                try
                {
                    bool alreadyApplied = view.GetFilters()
                        .Any(id => RevitCompat.GetId(id) == filterIdRaw);

                    if (!alreadyApplied)
                        view.AddFilter(filter.Id);

                    view.SetFilterVisibility(filter.Id, visible);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        applied = true,
                        filter_id = filterIdRaw,
                        filter_name = filter.Name,
                        view_id = resolvedViewId,
                        view_name = view.Name,
                        already_applied = alreadyApplied,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return Error($"Failed to apply filter to view: {ex.Message}",
                        filterIdRaw, filter.Name, resolvedViewId, view.Name, false);
                }
            }
        }

        private static CommandResult Error(string message, long filterId, string filterName,
            long? viewId, string viewName, bool alreadyApplied)
        {
            return CommandResult.Ok(new
            {
                applied = false,
                filter_id = filterId,
                filter_name = filterName,
                view_id = viewId,
                view_name = viewName,
                already_applied = alreadyApplied,
                error = message
            });
        }
    }
}
