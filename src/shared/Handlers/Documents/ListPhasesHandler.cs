using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListPhasesHandler : IRevitCommand
    {
        public string Name => "list_phases";
        public string Description => "List all project phases (in sequence order) and all phase filters. No parameters.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            // Parse params defensively — the schema is empty so paramsJson may be empty/blank.
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try
                {
                    JObject.Parse(paramsJson);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // No parameters are used; ignore malformed/empty payloads.
                }
            }

            // Phases — order in the PhaseArray is the sequence order.
            var phases = new List<object>();
            try
            {
                var phaseArray = doc.Phases;
                if (phaseArray != null)
                {
                    int sequenceIndex = 0;
                    foreach (Phase phase in phaseArray)
                    {
                        if (phase == null)
                        {
                            sequenceIndex++;
                            continue;
                        }

                        phases.Add(new
                        {
                            id = RevitCompat.GetId(phase.Id),
                            name = phase.Name,
                            sequence_index = sequenceIndex
                        });
                        sequenceIndex++;
                    }
                }
            }
            catch
            {
                // Leave phases as collected so far.
            }

            // Phase filters.
            var phaseFilters = new List<object>();
            try
            {
                var filters = new FilteredElementCollector(doc)
                    .OfClass(typeof(PhaseFilter))
                    .Cast<PhaseFilter>()
                    .ToList();

                foreach (var filter in filters)
                {
                    if (filter == null)
                        continue;

                    try
                    {
                        phaseFilters.Add(new
                        {
                            id = RevitCompat.GetId(filter.Id),
                            name = filter.Name
                        });
                    }
                    catch
                    {
                        // Skip filters that fail introspection.
                    }
                }
            }
            catch
            {
                // Leave phaseFilters as collected so far.
            }

            return CommandResult.Ok(new
            {
                doc_title = doc.Title,
                total_phases = phases.Count,
                phases = phases.ToArray(),
                total_phase_filters = phaseFilters.Count,
                phase_filters = phaseFilters.ToArray()
            });
        }
    }
}
