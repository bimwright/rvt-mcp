using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetPanelScheduleHandler : IRevitCommand
    {
        public string Name => "get_panel_schedule";
        public string Description => "Read an electrical panel's circuit schedule: panel metadata, voltage, and the list of circuits with loads. Resolve the panel by panel_id (ElementId of an Electrical Equipment family instance) or by panel_name. Returns panel info plus each circuit's number, rating (amps), apparent load (VA), voltage, pole count, and connected element count.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""panel_id"":{""type"":""integer"",""description"":""ElementId of an electrical panel (Electrical Equipment family instance). Either panel_id or panel_name required.""},""panel_name"":{""type"":""string"",""description"":""Name of an electrical panel. Either panel_id or panel_name required.""},""start_circuit"":{""type"":""integer"",""default"":0,""minimum"":0},""max_circuits"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":1000}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var panelId = request.Value<long?>("panel_id");
            var panelName = request.Value<string>("panel_name");
            if (!ResponsePaging.TryParse(request, "start_circuit", "max_circuits", 100, 1000, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);

            if (panelId == null && string.IsNullOrWhiteSpace(panelName))
                return CommandResult.Fail("Either panel_id or panel_name is required.");

            // Resolve the panel: a FamilyInstance of category OST_ElectricalEquipment.
            FamilyInstance panel = null;

            if (panelId != null)
            {
                if (!RevitCompat.CanRepresentElementId(panelId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(panelId.Value));

                var el = doc.GetElement(RevitCompat.ToElementId(panelId.Value));
                if (el == null)
                    return CommandResult.Fail($"Element {panelId.Value} not found in model.");

                panel = el as FamilyInstance;
                if (panel == null ||
                    RevitCompat.GetIdOrNull(panel.Category?.Id) != (long)BuiltInCategory.OST_ElectricalEquipment)
                    return CommandResult.Fail($"Element {panelId.Value} is not an electrical panel (Electrical Equipment family instance).");
            }
            else
            {
                var candidates = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi =>
                    {
                        try { return string.Equals(GetPanelName(fi), panelName, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    })
                    .ToList();

                if (candidates.Count == 0)
                    return CommandResult.Fail($"No electrical panel named '{panelName}' found in model.");
                if (candidates.Count > 1)
                    return CommandResult.Fail($"Multiple electrical panels named '{panelName}' found ({candidates.Count}). Use panel_id to disambiguate.");

                panel = candidates[0];
            }

            string panelDisplayName = null;
            try { panelDisplayName = GetPanelName(panel); } catch { }

            string panelType = null;
            try { panelType = panel.Symbol?.Name; } catch { }

            // Collect circuits assigned to this panel as BaseEquipment.
            long panelElemId = RevitCompat.GetId(panel.Id);
            var circuits = new List<object>();

            var systems = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .ToList();

            foreach (var sys in systems)
            {
                long? baseId;
                try { baseId = RevitCompat.GetIdOrNull(sys.BaseEquipment?.Id); }
                catch { continue; }

                if (baseId == null || baseId.Value != panelElemId)
                    continue;

                string circuitNumber = null;
                try { circuitNumber = sys.CircuitNumber; } catch { }

                string circuitName = null;
                try { circuitName = sys.Name; } catch { }

                double? ratingAmps = null;
                try { ratingAmps = sys.Rating; } catch { }

                double? loadVa = null;
                try { loadVa = sys.ApparentLoad; } catch { }

                double? voltage = null;
                try { voltage = sys.Voltage; } catch { }

                int? poles = null;
                try { poles = sys.PolesNumber; } catch { }

                int elementCount = 0;
                try { elementCount = sys.Elements != null ? sys.Elements.Size : 0; } catch { }

                circuits.Add(new
                {
                    id = RevitCompat.GetId(sys.Id).ToString(),
                    circuit_number = circuitNumber,
                    name = circuitName,
                    rating_amps = ratingAmps,
                    load_va = loadVa,
                    voltage = voltage,
                    poles = poles,
                    element_count = elementCount
                });
            }

            var circuitPage = ResponsePaging.Slice(circuits, paging.StartIndex, paging.MaxResults);
            return CommandResult.Ok(new
            {
                panel_id = panelElemId.ToString(),
                panel_name = panelDisplayName,
                panel_type = panelType,
                total_circuits = circuitPage.TotalCount,
                start_circuit = circuitPage.StartIndex,
                returned_circuit_count = circuitPage.ReturnedCount,
                truncated = circuitPage.Truncated,
                next_circuit = circuitPage.NextIndex,
                circuits = circuitPage.Items
            });
        }

        private static string GetPanelName(Element panel)
        {
            string panelName = null;
            try { panelName = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString(); }
            catch { }

            if (!string.IsNullOrEmpty(panelName))
                return panelName;

            try { return panel.Name; }
            catch { return null; }
        }
    }
}
