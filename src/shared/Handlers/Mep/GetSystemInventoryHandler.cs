using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// get_system_inventory — Returns the full element inventory of a single MEP system:
    /// every member element with category/type plus a category breakdown. Read-only.
    /// </summary>
    public class GetSystemInventoryHandler : IRevitCommand
    {
        private const int MaxParametersPerElement = 20;

        public string Name => "get_system_inventory";

        public string Description =>
            "Return the full element inventory of one MEP system (mechanical / piping / electrical): " +
            "network members, terminals, and base equipment, deduplicated by element ID, " +
            "with category and type, a category breakdown count, and the system " +
            "domain/type. Resolve the system by system_id or system_name. Optional include_parameters " +
            "returns each element's key MEP parameters with unit-corrected values.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""system_id"":{""type"":""integer"",""description"":""MEP system ElementId. Either system_id or system_name required.""},""system_name"":{""type"":""string""},""include_parameters"":{""type"":""boolean"",""default"":false,""description"":""If true, include each element's key MEP parameters.""},""limit"":{""type"":""integer"",""default"":2000,""minimum"":1,""maximum"":20000}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = JObject.Parse(paramsJson ?? "{}");
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var systemId = request.Value<long?>("system_id");
            var systemName = request.Value<string>("system_name");
            var includeParameters = request.Value<bool?>("include_parameters") ?? false;
            var limit = request.Value<int?>("limit") ?? 2000;
            if (limit < 1) limit = 1;
            if (limit > 20000) limit = 20000;

            if (systemId == null && string.IsNullOrWhiteSpace(systemName))
                return CommandResult.Fail("Either system_id or system_name is required.");

            MEPSystem system = ResolveSystem(doc, systemId, systemName, out string resolveError);
            if (system == null)
                return CommandResult.Fail(resolveError);

            string domain = GetDomain(system);

            // Resolve the system type name (element type of the MEPSystem element).
            string systemTypeName = "";
            try
            {
                var typeEl = doc.GetElement(system.GetTypeId());
                systemTypeName = typeEl?.Name ?? "";
            }
            catch { }

            // Include flow members, terminals, and source equipment without duplicate IDs.
            var memberIds = new List<long>();
            var seen = new HashSet<long>();
            try
            {
                var members = MepSystemMembership.Read(system).AllElements;
                if (members != null)
                {
                    foreach (Element member in members)
                    {
                        if (member == null) continue;
                        long mid = RevitCompat.GetId(member.Id);
                        if (mid <= 0) continue;
                        if (seen.Add(mid))
                            memberIds.Add(mid);
                    }
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to read system elements: " + ex.Message);
            }

            var totalElements = memberIds.Count;
            var categoryBreakdown = new Dictionary<string, int>(StringComparer.Ordinal);
            var elementsOut = new List<object>();
            var limitHit = totalElements > limit;

            for (int i = 0; i < totalElements; i++)
            {
                long id = memberIds[i];
                Element el;
                try
                {
                    el = doc.GetElement(RevitCompat.ToElementId(id));
                }
                catch
                {
                    el = null;
                }
                if (el == null) continue;

                var catName = el.Category?.Name ?? "<none>";
                if (categoryBreakdown.ContainsKey(catName))
                    categoryBreakdown[catName]++;
                else
                    categoryBreakdown[catName] = 1;

                if (elementsOut.Count >= limit) continue;

                string typeName = "";
                try
                {
                    var typeEl = doc.GetElement(el.GetTypeId());
                    typeName = typeEl?.Name ?? "";
                }
                catch { }

                if (includeParameters)
                {
                    var paramMap = new Dictionary<string, string>(StringComparer.Ordinal);
                    var paramCount = 0;
                    try
                    {
                        foreach (Parameter p in el.Parameters)
                        {
                            if (paramCount >= MaxParametersPerElement) break;
                            if (p == null || p.Definition == null || !p.HasValue) continue;
                            try
                            {
                                var pName = p.Definition.Name;
                                if (string.IsNullOrEmpty(pName)) continue;
                                if (paramMap.ContainsKey(pName)) continue;
                                var pVal = GetParamValueAsString(doc, p);
                                if (pVal == null) continue;
                                paramMap[pName] = pVal;
                                paramCount++;
                            }
                            catch { }
                        }
                    }
                    catch { }

                    elementsOut.Add(new
                    {
                        id = id.ToString(),
                        category = catName,
                        type_name = typeName,
                        name = el.Name ?? "",
                        parameters = paramMap
                    });
                }
                else
                {
                    elementsOut.Add(new
                    {
                        id = id.ToString(),
                        category = catName,
                        type_name = typeName,
                        name = el.Name ?? ""
                    });
                }
            }

            return CommandResult.Ok(new
            {
                system_id = RevitCompat.GetId(system.Id).ToString(),
                system_name = system.Name ?? "",
                domain = domain,
                system_type = systemTypeName,
                total_elements = totalElements,
                returned = elementsOut.Count,
                limit_hit = limitHit,
                category_breakdown = categoryBreakdown,
                elements = elementsOut
            });
        }

        /// <summary>
        /// Resolve an MEPSystem by id (any of MechanicalSystem / PipingSystem / ElectricalSystem)
        /// or by name (case-insensitive scan across all three domains).
        /// </summary>
        private static MEPSystem ResolveSystem(Document doc, long? systemId, string systemName, out string error)
        {
            error = null;

            if (systemId != null)
            {
                ElementId eid;
                try
                {
                    eid = RevitCompat.ToElementId(systemId.Value);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    error = ex.Message;
                    return null;
                }

                var el = doc.GetElement(eid);
                if (el == null)
                {
                    error = $"No element found with id {systemId.Value}.";
                    return null;
                }
                if (el is MEPSystem sys)
                    return sys;

                error = $"Element {systemId.Value} is not an MEP system (found {el.GetType().Name}).";
                return null;
            }

            // Resolve by name — scan mechanical, piping, electrical collectors.
            var matches = CollectAllSystems(doc)
                .Where(s => string.Equals(s.Name, systemName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                error = $"No MEP system found with name '{systemName}'.";
                return null;
            }
            if (matches.Count > 1)
            {
                var ids = string.Join(", ", matches.Select(m => RevitCompat.GetId(m.Id)));
                error = $"Ambiguous system name '{systemName}': {matches.Count} matches (ids: {ids}). Use system_id.";
                return null;
            }
            return matches[0];
        }

        private static IEnumerable<MEPSystem> CollectAllSystems(Document doc)
        {
            var result = new List<MEPSystem>();

            try
            {
                result.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(MechanicalSystem))
                    .Cast<MEPSystem>());
            }
            catch { }

            try
            {
                result.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystem))
                    .Cast<MEPSystem>());
            }
            catch { }

            try
            {
                result.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<MEPSystem>());
            }
            catch { }

            return result;
        }

        private static string GetDomain(MEPSystem system)
        {
            if (system is MechanicalSystem) return "mechanical";
            if (system is PipingSystem) return "piping";
            if (system is ElectricalSystem) return "electrical";
            return "unknown";
        }

        private static double ConvertToDisplayUnits(Document doc, Parameter param, double internalValue)
        {
            try
            {
                var specId = param.Definition.GetDataType();

                if (specId == SpecTypeId.Length || specId == SpecTypeId.PipeSize ||
                    specId == SpecTypeId.DuctSize || specId == SpecTypeId.SectionDimension)
                    return internalValue * 304.8; // feet → mm
                if (specId == SpecTypeId.Area)
                    return internalValue * 0.092903; // sq feet → m²
                if (specId == SpecTypeId.Volume)
                    return internalValue * 0.0283168; // cu feet → m³
                if (specId == SpecTypeId.Angle)
                    return internalValue * (180.0 / Math.PI); // radians → degrees
            }
            catch { }

            return internalValue;
        }

        private static string GetParamValueAsString(Document doc, Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    var raw = param.AsDouble();
                    var display = ConvertToDisplayUnits(doc, param, raw);
                    return Math.Round(display, 2).ToString();
                case StorageType.ElementId:
                    var refEl = doc.GetElement(param.AsElementId());
                    return refEl?.Name ?? RevitCompat.GetId(param.AsElementId()).ToString();
                default:
                    return null;
            }
        }
    }
}
