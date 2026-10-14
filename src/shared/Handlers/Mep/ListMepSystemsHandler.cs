using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListMepSystemsHandler : IRevitCommand
    {
        public string Name => "list_mep_systems";
        public string Description => "List all MEP systems (mechanical/HVAC, piping/plumbing, electrical) in the document. Optional domain_filter: all (default), mechanical, piping, electrical. limit caps the number of returned systems (default 1000). element_count is pipes and fittings, ducts and fittings, or electrical circuit members. terminal_count excludes base equipment. Membership read failures return an error, never zero counts.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""domain_filter"":{""type"":""string"",""enum"":[""all"",""mechanical"",""piping"",""electrical""],""default"":""all""},""limit"":{""type"":""integer"",""default"":1000,""minimum"":1,""maximum"":10000}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try { return ExecuteCore(app, paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Failed to list MEP systems: " + ex.Message); }
        }

        private CommandResult ExecuteCore(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var domainFilter = (request.Value<string>("domain_filter") ?? "all").Trim().ToLowerInvariant();
            if (domainFilter.Length == 0) domainFilter = "all";
            if (domainFilter != "all" && domainFilter != "mechanical" &&
                domainFilter != "piping" && domainFilter != "electrical")
                return CommandResult.Fail("domain_filter must be one of: all, mechanical, piping, electrical.");

            int limit = request.Value<int?>("limit") ?? 1000;
            if (limit < 1) limit = 1;
            if (limit > 10000) limit = 10000;

            var collected = new List<object>();
            int total = 0;

            // Mechanical / HVAC
            if (domainFilter == "all" || domainFilter == "mechanical")
            {
                var mechSystems = new FilteredElementCollector(doc)
                    .OfClass(typeof(MechanicalSystem))
                    .Cast<MechanicalSystem>()
                    .ToList();
                foreach (var sys in mechSystems)
                {
                    total++;
                    if (collected.Count < limit)
                        collected.Add(BuildSystemDto(doc, sys, "mechanical"));
                }
            }

            // Piping / Plumbing
            if (domainFilter == "all" || domainFilter == "piping")
            {
                var pipeSystems = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystem))
                    .Cast<PipingSystem>()
                    .ToList();
                foreach (var sys in pipeSystems)
                {
                    total++;
                    if (collected.Count < limit)
                        collected.Add(BuildSystemDto(doc, sys, "piping"));
                }
            }

            // Electrical
            if (domainFilter == "all" || domainFilter == "electrical")
            {
                var elecSystems = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .ToList();
                foreach (var sys in elecSystems)
                {
                    total++;
                    if (collected.Count < limit)
                        collected.Add(BuildSystemDto(doc, sys, "electrical"));
                }
            }

            return CommandResult.Ok(new
            {
                doc_title = doc.Title,
                total_systems = total,
                returned = collected.Count,
                systems = collected.ToArray()
            });
        }

        private static object BuildSystemDto(Document doc, MEPSystem system, string domain)
        {
            string name = null;
            try { name = system.Name; } catch { }

            var membership = MepSystemMembership.Read(system).Counts;

            string systemType = null;
            try
            {
                var typeId = system.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var mepType = doc.GetElement(typeId) as MEPSystemType;
                    if (mepType != null) systemType = mepType.Name;
                }
            }
            catch { }

            bool isWellConnected = IsWellConnected(system);

            return new
            {
                id = RevitCompat.GetId(system.Id).ToString(),
                name = name ?? string.Empty,
                domain = domain,
                system_type = systemType ?? string.Empty,
                element_count = membership.ElementCount,
                terminal_count = membership.TerminalCount,
                is_well_connected = isWellConnected
            };
        }

        /// <summary>
        /// MEPSystem.IsWellConnected is not exposed uniformly across all domains/versions.
        /// Try the direct property; fall back to reflection; default to true.
        /// </summary>
        private static bool IsWellConnected(MEPSystem system)
        {
            // IsWellConnected lives on the MechanicalSystem / PipingSystem subclasses,
            // not on the MEPSystem base. ElectricalSystem has no equivalent — default true.
            try
            {
                if (system is MechanicalSystem) return ((MechanicalSystem)system).IsWellConnected;
                if (system is PipingSystem) return ((PipingSystem)system).IsWellConnected;
            }
            catch { }
            return true;
        }
    }
}
