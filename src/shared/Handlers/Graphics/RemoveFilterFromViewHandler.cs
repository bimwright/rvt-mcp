using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class RemoveFilterFromViewHandler : IRevitCommand
    {
        public string Name => "remove_filter_from_view";
        public string Description => "Remove a ParameterFilterElement from a view's filter list, optionally deleting the filter definition if it is no longer used by any view";
        public string ParametersSchema => @"{""type"":""object"",""required"":[""filter_id""],""properties"":{""filter_id"":{""type"":""integer"",""description"":""ParameterFilterElement ElementId.""},""view_id"":{""type"":""integer"",""description"":""Target view ElementId. If omitted, the active view is used.""},""delete_definition_if_unused"":{""type"":""boolean"",""default"":false,""description"":""If true and the filter is applied to no other views after removal, delete the ParameterFilterElement itself.""}}}";

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
            var deleteIfUnused = request.Value<bool?>("delete_definition_if_unused") ?? false;

            // Resolve the ParameterFilterElement.
            if (!RevitCompat.CanRepresentElementId(filterIdRaw))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(filterIdRaw));

            var filterElementId = RevitCompat.ToElementId(filterIdRaw);
            var filter = doc.GetElement(filterElementId) as ParameterFilterElement;
            if (filter == null)
                return Error($"ParameterFilterElement with ID {filterIdRaw} not found.", filterIdRaw, null, null, null);

            var filterName = filter.Name;

            // Resolve the target view.
            View view;
            if (viewIdRaw.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdRaw.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdRaw.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewIdRaw.Value)) as View;
                if (view == null)
                    return Error($"View with ID {viewIdRaw.Value} not found.", filterIdRaw, filterName, null, null);
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return Error("No active view is available.", filterIdRaw, filterName, null, null);
            }

            var resolvedViewId = RevitCompat.GetId(view.Id);
            var resolvedViewName = view.Name;

            if (!view.AreGraphicsOverridesAllowed())
                return Error(
                    $"View '{view.Name}' ({view.ViewType}) does not allow graphics overrides.",
                    filterIdRaw, filterName, resolvedViewId, resolvedViewName);

            using (var tx = new Transaction(doc, "RvtMcp: remove filter from view"))
            {
                tx.Start();
                try
                {
                    bool applied = view.GetFilters()
                        .Any(id => RevitCompat.GetId(id) == filterIdRaw);

                    if (!applied)
                    {
                        tx.RollBack();
                        return Error("Filter is not applied to this view.",
                            filterIdRaw, filterName, resolvedViewId, resolvedViewName);
                    }

                    view.RemoveFilter(filterElementId);

                    bool definitionDeleted = false;
                    if (deleteIfUnused && !IsFilterUsedByAnyView(doc, filterIdRaw))
                    {
                        doc.Delete(filterElementId);
                        definitionDeleted = true;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        removed = true,
                        filter_id = filterIdRaw,
                        filter_name = filterName,
                        view_id = resolvedViewId,
                        view_name = resolvedViewName,
                        definition_deleted = definitionDeleted,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return Error($"Failed to remove filter from view: {ex.Message}",
                        filterIdRaw, filterName, resolvedViewId, resolvedViewName);
                }
            }
        }

        /// <summary>
        /// Returns true if any view in the document still references the filter.
        /// Skips view templates and views that do not allow filters/graphics overrides.
        /// </summary>
        private static bool IsFilterUsedByAnyView(Document doc, long filterIdRaw)
        {
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>();

            foreach (var v in views)
            {
                ICollection<ElementId> appliedFilters;
                try
                {
                    appliedFilters = v.GetFilters();
                }
                catch (Exception)
                {
                    // Some view types throw when filters are not supported.
                    continue;
                }

                if (appliedFilters != null &&
                    appliedFilters.Any(id => RevitCompat.GetId(id) == filterIdRaw))
                {
                    return true;
                }
            }

            return false;
        }

        private static CommandResult Error(string message, long filterId, string filterName,
            long? viewId, string viewName)
        {
            return CommandResult.Ok(new
            {
                removed = false,
                filter_id = filterId,
                filter_name = filterName,
                view_id = viewId,
                view_name = viewName,
                definition_deleted = false,
                error = message
            });
        }
    }
}
