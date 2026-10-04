using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewPhaseHandler : IRevitCommand
    {
        public string Name => "set_view_phase";
        public string Description => "Set a view's Phase and/or Phase Filter. At least one of phase or phase filter must be supplied.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer"",""description"":""Target view ElementId. If omitted, the active view is used.""},""phase_id"":{""type"":""integer"",""description"":""Phase ElementId. Optional.""},""phase_name"":{""type"":""string"",""description"":""Phase name (used if phase_id omitted). Optional.""},""phase_filter_id"":{""type"":""integer"",""description"":""PhaseFilter ElementId. Optional.""},""phase_filter_name"":{""type"":""string"",""description"":""PhaseFilter name (used if phase_filter_id omitted). Optional.""}}}";

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
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            var viewIdRaw = request.Value<long?>("view_id");
            var phaseIdRaw = request.Value<long?>("phase_id");
            var phaseName = request.Value<string>("phase_name");
            var phaseFilterIdRaw = request.Value<long?>("phase_filter_id");
            var phaseFilterName = request.Value<string>("phase_filter_name");

            bool phaseRequested = phaseIdRaw.HasValue || !string.IsNullOrWhiteSpace(phaseName);
            bool phaseFilterRequested = phaseFilterIdRaw.HasValue || !string.IsNullOrWhiteSpace(phaseFilterName);

            if (!phaseRequested && !phaseFilterRequested)
                return Error("At least one of phase or phase filter must be supplied.", null, null, null, null);

            // Resolve the target view.
            View view;
            if (viewIdRaw.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdRaw.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdRaw.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewIdRaw.Value)) as View;
                if (view == null)
                    return Error($"View with ID {viewIdRaw.Value} not found.", null, null, null, null);
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return Error("No active view is available.", null, null, null, null);
            }

            var resolvedViewId = RevitCompat.GetId(view.Id);

            // Resolve the Phase, if requested.
            Phase phase = null;
            if (phaseRequested)
            {
                phase = ResolvePhase(doc, phaseIdRaw, phaseName, out var phaseError);
                if (phase == null)
                    return Error(phaseError, resolvedViewId, view.Name, null, null);
            }

            // Resolve the PhaseFilter, if requested.
            PhaseFilter phaseFilter = null;
            if (phaseFilterRequested)
            {
                phaseFilter = ResolvePhaseFilter(doc, phaseFilterIdRaw, phaseFilterName, out var filterError);
                if (phaseFilter == null)
                    return Error(filterError, resolvedViewId, view.Name, null, null);
            }

            using (var tx = new Transaction(doc, "RvtMcp: set view phase"))
            {
                tx.Start();
                try
                {
                    if (phase != null)
                    {
                        var phaseParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                        if (phaseParam == null)
                        {
                            tx.RollBack();
                            return Error($"View '{view.Name}' ({view.ViewType}) does not expose a Phase parameter.",
                                resolvedViewId, view.Name, null, null);
                        }
                        if (phaseParam.IsReadOnly)
                        {
                            tx.RollBack();
                            return Error($"View '{view.Name}' ({view.ViewType}) has a read-only Phase parameter.",
                                resolvedViewId, view.Name, null, null);
                        }
                        if (!phaseParam.Set(phase.Id))
                        {
                            tx.RollBack();
                            return Error("Revit rejected the Phase value.", resolvedViewId, view.Name, null, null);
                        }
                    }

                    if (phaseFilter != null)
                    {
                        var phaseFilterParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                        if (phaseFilterParam == null)
                        {
                            tx.RollBack();
                            return Error($"View '{view.Name}' ({view.ViewType}) does not expose a Phase Filter parameter.",
                                resolvedViewId, view.Name, null, null);
                        }
                        if (phaseFilterParam.IsReadOnly)
                        {
                            tx.RollBack();
                            return Error($"View '{view.Name}' ({view.ViewType}) has a read-only Phase Filter parameter.",
                                resolvedViewId, view.Name, null, null);
                        }
                        if (!phaseFilterParam.Set(phaseFilter.Id))
                        {
                            tx.RollBack();
                            return Error("Revit rejected the Phase Filter value.", resolvedViewId, view.Name, null, null);
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        updated = true,
                        view_id = resolvedViewId,
                        view_name = view.Name,
                        phase = ReadPhaseName(doc, view),
                        phase_filter = ReadPhaseFilterName(doc, view),
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return Error($"Failed to set view phase: {ex.Message}", resolvedViewId, view.Name, null, null);
                }
            }
        }

        private static Phase ResolvePhase(Document doc, long? phaseId, string phaseName, out string error)
        {
            error = null;

            if (phaseId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(phaseId.Value))
                {
                    error = RevitCompat.ElementIdRangeError(phaseId.Value);
                    return null;
                }

                var byId = doc.GetElement(RevitCompat.ToElementId(phaseId.Value)) as Phase;
                if (byId == null)
                {
                    error = $"Phase with ID {phaseId.Value} not found.";
                    return null;
                }
                return byId;
            }

            var phaseArray = doc.Phases;
            if (phaseArray != null)
            {
                foreach (Phase phase in phaseArray)
                {
                    if (phase != null && string.Equals(phase.Name, phaseName, StringComparison.OrdinalIgnoreCase))
                        return phase;
                }
            }

            error = $"Phase named '{phaseName}' not found.";
            return null;
        }

        private static PhaseFilter ResolvePhaseFilter(Document doc, long? filterId, string filterName, out string error)
        {
            error = null;

            if (filterId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(filterId.Value))
                {
                    error = RevitCompat.ElementIdRangeError(filterId.Value);
                    return null;
                }

                var byId = doc.GetElement(RevitCompat.ToElementId(filterId.Value)) as PhaseFilter;
                if (byId == null)
                {
                    error = $"PhaseFilter with ID {filterId.Value} not found.";
                    return null;
                }
                return byId;
            }

            var byName = new FilteredElementCollector(doc)
                .OfClass(typeof(PhaseFilter))
                .Cast<PhaseFilter>()
                .FirstOrDefault(pf => pf != null && string.Equals(pf.Name, filterName, StringComparison.OrdinalIgnoreCase));

            if (byName == null)
                error = $"PhaseFilter named '{filterName}' not found.";

            return byName;
        }

        private static string ReadPhaseName(Document doc, View view)
        {
            try
            {
                var param = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (param == null)
                    return null;

                var id = param.AsElementId();
                if (id == null || RevitCompat.GetId(id) <= 0)
                    return null;

                return doc.GetElement(id)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadPhaseFilterName(Document doc, View view)
        {
            try
            {
                var param = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                if (param == null)
                    return null;

                var id = param.AsElementId();
                if (id == null || RevitCompat.GetId(id) <= 0)
                    return null;

                return doc.GetElement(id)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static CommandResult Error(string message, long? viewId, string viewName, string phase, string phaseFilter)
        {
            return CommandResult.Ok(new
            {
                updated = false,
                view_id = viewId,
                view_name = viewName,
                phase,
                phase_filter = phaseFilter,
                error = message
            });
        }
    }
}
