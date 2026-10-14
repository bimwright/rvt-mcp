using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Scans the model for MEP elements (ducts, pipes, cable trays, conduits, fittings,
    /// equipment, terminals) that have open/unconnected physical connectors — potential
    /// gaps in ductwork, piping, or other MEP systems. Read-only — no transaction.
    /// </summary>
    public class FindMepDisconnectsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;
        private const int DefaultLimit = 2000;
        private const int MinLimit = 1;
        private const int MaxLimit = 20000;

        public string Name => "find_mep_disconnects";

        public string Description =>
            "Find MEP elements with open/unconnected connectors (potential gaps in ductwork, " +
            "piping, cable tray, or conduit runs). Inspects ducts, pipes, cable trays, conduits, " +
            "and MEP family instances (fittings, equipment, terminals). Only physical 'End' " +
            "connectors are considered — logical/curve connectors are ignored. Returns each " +
            "element's open connector count, domain, and connector origins in mm. " +
            "Optionally scope to a single domain or the active view.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""domain_filter"": {""type"":""string"",""enum"":[""all"",""mechanical"",""piping"",""electrical""],""default"":""all"",""description"":""Restrict results to one MEP domain.""},
    ""view_only"": {""type"":""boolean"",""default"":false,""description"":""If true, only inspect elements visible in the active view.""},
    ""limit"": {""type"":""integer"",""default"":2000,""minimum"":1,""maximum"":20000,""description"":""Maximum number of disconnected elements to return.""}
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
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            string domainFilter = (request.Value<string>("domain_filter") ?? "all").Trim().ToLowerInvariant();
            if (domainFilter != "all" && domainFilter != "mechanical" &&
                domainFilter != "piping" && domainFilter != "electrical")
            {
                return CommandResult.Fail(
                    "domain_filter must be one of: all, mechanical, piping, electrical.");
            }

            bool viewOnly = request.Value<bool?>("view_only") ?? false;

            int limit = request.Value<int?>("limit") ?? DefaultLimit;
            if (limit < MinLimit) limit = MinLimit;
            if (limit > MaxLimit) limit = MaxLimit;

            // Build candidate element list: MEPCurves cover Duct/Pipe/CableTray/Conduit;
            // MEP family instances cover fittings, equipment, and terminals.
            var candidates = new List<Element>();
            try
            {
                CollectCandidates(doc, viewOnly, candidates);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to collect MEP elements: " + ex.Message);
            }

            int totalOpenConnectors = 0;
            int elementsWithDisconnects = 0;
            bool limitHit = false;
            var disconnects = new List<object>();

            foreach (var element in candidates)
            {
                try
                {
                    var openConnectors = FindOpenConnectors(element, domainFilter);
                    if (openConnectors.Count == 0)
                        continue;

                    elementsWithDisconnects++;
                    totalOpenConnectors += openConnectors.Count;

                    if (disconnects.Count >= limit)
                    {
                        limitHit = true;
                        continue;
                    }

                    string category = null;
                    try { category = element.Category?.Name; } catch { }

                    string typeName = null;
                    try
                    {
                        var typeId = element.GetTypeId();
                        if (typeId != null && RevitCompat.GetId(typeId) > 0)
                        {
                            var typeElem = doc.GetElement(typeId);
                            if (typeElem != null) typeName = typeElem.Name;
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(typeName))
                    {
                        try { typeName = element.Name; } catch { }
                    }

                    disconnects.Add(new
                    {
                        element_id = RevitCompat.GetId(element.Id).ToString(),
                        category = category,
                        type_name = typeName,
                        open_connector_count = openConnectors.Count,
                        open_connectors = openConnectors
                    });
                }
                catch
                {
                    // One broken element must not abort the whole scan.
                }
            }

            return CommandResult.Ok(new
            {
                total_open_connectors = totalOpenConnectors,
                elements_with_disconnects = elementsWithDisconnects,
                returned = disconnects.Count,
                limit_hit = limitHit,
                disconnects = disconnects
            });
        }

        private static void CollectCandidates(Document doc, bool viewOnly, List<Element> candidates)
        {
            // MEPCurves: Duct, Pipe, CableTray, Conduit.
            FilteredElementCollector curveCollector =
                viewOnly && doc.ActiveView != null
                    ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                    : new FilteredElementCollector(doc);

            foreach (var el in curveCollector
                         .OfClass(typeof(MEPCurve))
                         .WhereElementIsNotElementType())
            {
                if (el != null) candidates.Add(el);
            }

            // MEP family instances: fittings, equipment, air terminals, etc.
            FilteredElementCollector instCollector =
                viewOnly && doc.ActiveView != null
                    ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                    : new FilteredElementCollector(doc);

            foreach (var el in instCollector
                         .OfClass(typeof(FamilyInstance))
                         .WhereElementIsNotElementType())
            {
                var fi = el as FamilyInstance;
                if (fi == null) continue;

                bool hasMepModel = false;
                try { hasMepModel = fi.MEPModel != null; } catch { hasMepModel = false; }
                if (hasMepModel) candidates.Add(fi);
            }
        }

        private static List<object> FindOpenConnectors(Element element, string domainFilter)
        {
            var open = new List<object>();

            ConnectorManager connectorManager = GetConnectorManager(element);
            if (connectorManager == null)
                return open;

            ConnectorSet connectorSet = null;
            try { connectorSet = connectorManager.Connectors; }
            catch { connectorSet = null; }
            if (connectorSet == null)
                return open;

            int index = 0;
            foreach (Connector connector in connectorSet)
            {
                int currentIndex = index;
                index++;

                try
                {
                    // Only physical "End" connectors represent real openings in a run.
                    // Logical / Curve connectors are pseudo-connectors and must be skipped.
                    if (connector.ConnectorType != ConnectorType.End)
                        continue;

                    bool isConnected = false;
                    try { isConnected = connector.IsConnected; }
                    catch { isConnected = false; }
                    if (isConnected)
                        continue;

                    Domain domain = Domain.DomainUndefined;
                    try { domain = connector.Domain; }
                    catch { domain = Domain.DomainUndefined; }

                    if (!DomainMatches(domain, domainFilter))
                        continue;

                    object originMm = null;
                    try
                    {
                        var origin = connector.Origin;
                        if (origin != null)
                        {
                            originMm = new[]
                            {
                                Math.Round(origin.X * FeetToMm, 1),
                                Math.Round(origin.Y * FeetToMm, 1),
                                Math.Round(origin.Z * FeetToMm, 1)
                            };
                        }
                    }
                    catch { originMm = null; }

                    open.Add(new
                    {
                        index = currentIndex,
                        domain = domain.ToString().Replace("Domain", ""),
                        origin_mm = originMm
                    });
                }
                catch
                {
                    // One bad connector must not abort inspection of the element.
                }
            }

            return open;
        }

        private static bool DomainMatches(Domain domain, string domainFilter)
        {
            switch (domainFilter)
            {
                case "mechanical":
                    return domain == Domain.DomainHvac;
                case "piping":
                    return domain == Domain.DomainPiping;
                case "electrical":
                    return domain == Domain.DomainElectrical;
                default:
                    return true;
            }
        }

        private static ConnectorManager GetConnectorManager(Element element)
        {
            try
            {
                if (element is MEPCurve curve)
                    return curve.ConnectorManager;

                if (element is FamilyInstance fi)
                    return fi.MEPModel?.ConnectorManager;
            }
            catch { }

            return null;
        }
    }
}
