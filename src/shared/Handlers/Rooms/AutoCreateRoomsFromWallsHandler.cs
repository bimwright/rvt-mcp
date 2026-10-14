using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AutoCreateRoomsFromWallsHandler : IRevitCommand
    {
        public string Name => "auto_create_rooms_from_walls";
        public string Description => "Auto-create rooms for available enclosed plan circuits on a level";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""required"": [""level_name""],
            ""properties"": {
                ""level_name"": { ""type"": ""string"" },
                ""phase_name"": { ""type"": ""string"" },
                ""name_prefix"": { ""type"": ""string"" },
                ""number_prefix"": { ""type"": ""string"" },
                ""start_number"": { ""type"": ""integer"", ""minimum"": 1 },
                ""dry_run"": { ""type"": ""boolean"" },
                ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 2000 }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            string levelName = "";
            string phaseName = "";
            string namePrefix = "";
            string numberPrefix = "";
            int startNumber = 1;
            bool dryRun = true;
            int limit = 500;

            try
            {
                var request = JObject.Parse(paramsJson);
                if (!request.TryGetValue("level_name", out var lvVal))
                    return CommandResult.Fail("level_name is required.");

                levelName = lvVal.Value<string>() ?? "";
                if (request.TryGetValue("phase_name", out var phVal))
                    phaseName = phVal.Value<string>() ?? "";
                if (request.TryGetValue("name_prefix", out var npVal))
                    namePrefix = npVal.Value<string>() ?? "";
                if (request.TryGetValue("number_prefix", out var numpVal))
                    numberPrefix = numpVal.Value<string>() ?? "";
                if (request.TryGetValue("start_number", out var snVal))
                    startNumber = snVal.Value<int>();
                if (request.TryGetValue("dry_run", out var drVal))
                    dryRun = drVal.Value<bool>();
                if (request.TryGetValue("limit", out var limVal))
                    limit = limVal.Value<int>();
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            if (limit < 1 || limit > 2000)
                return CommandResult.Fail("Limit must be between 1 and 2000.");

            // Resolve Level
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            if (level == null)
                return CommandResult.Fail($"Level '{levelName}' was not found.");

            // Resolve Phase
            Phase phase = null;
            if (!string.IsNullOrEmpty(phaseName))
            {
                phase = new FilteredElementCollector(doc).OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .FirstOrDefault(p => p.Name.Equals(phaseName, StringComparison.OrdinalIgnoreCase));
                if (phase == null)
                    return CommandResult.Fail($"Phase '{phaseName}' was not found.");
            }
            else
            {
                var activeView = app.ActiveUIDocument?.ActiveView;
                if (activeView != null)
                {
                    var phaseParam = activeView.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    if (phaseParam != null && phaseParam.HasValue)
                    {
                        phase = doc.GetElement(phaseParam.AsElementId()) as Phase;
                    }
                }
                if (phase == null)
                {
                    phase = new FilteredElementCollector(doc).OfClass(typeof(Phase))
                        .Cast<Phase>()
                        .LastOrDefault();
                }
            }

            if (phase == null)
                return CommandResult.Fail("A valid Phase could not be determined.");

            // Resolve Plan Topology
            PlanTopology planTopology = null;
            try
            {
                planTopology = doc.get_PlanTopology(level, phase);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to get plan topology for level '{level.Name}': {ex.Message}");
            }

            if (planTopology == null)
                return CommandResult.Fail($"Plan topology could not be resolved for level '{level.Name}' and phase '{phase.Name}'.");

            // Temporary collection of circuits
            var circuits = new List<PlanCircuit>();
            foreach (PlanCircuit circuit in planTopology.Circuits)
            {
                if (circuit != null && !circuit.IsRoomLocated)
                {
                    circuits.Add(circuit);
                }
            }

            if (circuits.Count > limit)
                return CommandResult.Fail($"Enclosed plan circuit count ({circuits.Count}) exceeds the specified limit ({limit}). Raise the limit parameter to proceed.");

            var candidatesList = new List<CandidateInfo>();
            int candidateIndex = 0;
            foreach (var circuit in circuits)
            {
                candidatesList.Add(new CandidateInfo
                {
                    Circuit = circuit,
                    Index = candidateIndex++
                });
            }

            var candidatesDto = new List<object>();
            var createdRoomIds = new List<long>();
            var warnings = new List<string>();

            if (dryRun)
            {
                int index = 0;
                foreach (var cand in candidatesList)
                {
                    candidatesDto.Add(new
                    {
                        index = index++,
                        area_m2 = (double?)null,
                        centroid = (object)null,
                        would_create = true,
                        room_id = (long?)null,
                        warnings = new[] { "Dry-run does not create temporary rooms; area and centroid are unavailable until execution." }
                    });
                }
                warnings.Add("Dry-run is read-only; candidate order follows Revit plan topology enumeration.");

                return CommandResult.Ok(new
                {
                    dry_run = true,
                    level = new { element_id = RevitCompat.GetId(level.Id), name = level.Name },
                    phase = new { element_id = RevitCompat.GetId(phase.Id), name = phase.Name },
                    candidate_count = candidatesList.Count,
                    created_count = 0,
                    candidates = candidatesDto,
                    created_room_ids = createdRoomIds,
                    warnings = warnings
                });
            }

            // Execute placement
            var tg = new TransactionGroup(doc, "RvtMcp: Auto Create Rooms From Walls");
            tg.Start();

            try
            {
                using (var tx = new Transaction(doc, "RvtMcp: Create Rooms"))
                {
                    tx.Start();

                    int index = 0;
                    foreach (var cand in candidatesList)
                    {
                        var itemWarnings = new List<string>();
                        Room room = null;
                        double? areaM2 = null;
                        object centroid = null;
                        try
                        {
                            var unplacedRoom = doc.Create.NewRoom(phase);
                            room = doc.Create.NewRoom(unplacedRoom, cand.Circuit);

                            if (room != null)
                            {
                                string number = $"{numberPrefix}{startNumber + index}";
                                string name = string.IsNullOrEmpty(namePrefix) ? "Room" : namePrefix;

                                var nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME) ?? room.LookupParameter("Name");
                                if (nameParam != null && !nameParam.IsReadOnly) nameParam.Set(name);
                                else room.Name = name;

                                var numParam = room.get_Parameter(BuiltInParameter.ROOM_NUMBER) ?? room.LookupParameter("Number");
                                if (numParam != null && !numParam.IsReadOnly) numParam.Set(number);
                                else room.Number = number;

                                areaM2 = Math.Round(room.Area * 0.09290304, 4);
                                if (room.Location is LocationPoint lp)
                                {
                                    centroid = new
                                    {
                                        x_mm = Math.Round(lp.Point.X * 304.8, 2),
                                        y_mm = Math.Round(lp.Point.Y * 304.8, 2),
                                        z_mm = Math.Round(lp.Point.Z * 304.8, 2)
                                    };
                                }

                                createdRoomIds.Add(RevitCompat.GetId(room.Id));
                            }
                        }
                        catch (Exception ex)
                        {
                            itemWarnings.Add($"Failed to place room: {ex.Message}");
                        }

                        candidatesDto.Add(new
                        {
                            index = index++,
                            area_m2 = areaM2,
                            centroid = centroid,
                            would_create = true,
                            room_id = room != null ? (long?)RevitCompat.GetId(room.Id) : null,
                            warnings = itemWarnings
                        });
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        if (tg.HasStarted())
                            tg.RollBack();
                        return CommandResult.Fail("Create rooms transaction did not commit. Status: " + status);
                    }
                }

                var groupStatus = tg.Assimilate();
                if (groupStatus != TransactionStatus.Committed)
                    return CommandResult.Fail("Auto-create rooms transaction group did not commit. Status: " + groupStatus);

                return CommandResult.Ok(new
                {
                    dry_run = false,
                    level = new { element_id = RevitCompat.GetId(level.Id), name = level.Name },
                    phase = new { element_id = RevitCompat.GetId(phase.Id), name = phase.Name },
                    candidate_count = candidatesList.Count,
                    created_count = createdRoomIds.Count,
                    candidates = candidatesDto,
                    created_room_ids = createdRoomIds,
                    warnings = warnings
                });
            }
            catch (Exception ex)
            {
                if (tg.HasStarted())
                {
                    tg.RollBack();
                }
                return CommandResult.Fail($"Failed to auto-create rooms: {ex.Message}");
            }
        }

        private class CandidateInfo
        {
            public PlanCircuit Circuit { get; set; }
            public int Index { get; set; }
        }
    }
}
