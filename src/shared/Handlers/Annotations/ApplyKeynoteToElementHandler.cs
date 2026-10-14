using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ApplyKeynoteToElementHandler : IRevitCommand
    {
        public string Name => "apply_keynote_to_element";
        public string Description => "Apply a keynote value to one or more elements or their types.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids"", ""keynote""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1 },
    ""keynote"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": false }
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
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var elementIdsToken = request["element_ids"] as JArray;
            if (elementIdsToken == null || elementIdsToken.Count == 0)
                return CommandResult.Fail("element_ids array is required and must not be empty.");

            var keynote = request.Value<string>("keynote");
            if (keynote == null) // Can be empty string to clear keynote
                return CommandResult.Fail("keynote parameter is required.");

            bool dryRun = request.Value<bool>("dry_run");

            var elementIds = new List<long>();
            foreach (var tok in elementIdsToken)
            {
                if (tok.Type == JTokenType.Integer)
                {
                    elementIds.Add(tok.Value<long>());
                }
            }

            var changes = new JArray();
            var failed = new JArray();
            int updatedCount = 0;

            if (dryRun)
            {
                foreach (var id in elementIds)
                {
                    if (!RevitCompat.CanRepresentElementId(id))
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = RevitCompat.ElementIdRangeError(id) });
                        continue;
                    }

                    var elem = doc.GetElement(RevitCompat.ToElementId(id));
                    if (elem == null)
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = "Element not found." });
                        continue;
                    }

                    var keynoteParam = FindKeynoteParameter(doc, elem);
                    if (keynoteParam == null)
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = "Keynote parameter not found on element or type." });
                        continue;
                    }

                    if (keynoteParam.IsReadOnly)
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = "Keynote parameter is read-only." });
                        continue;
                    }

                    if (keynoteParam.StorageType != StorageType.String)
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = "Keynote parameter is not a string type." });
                        continue;
                    }

                    updatedCount++;
                    changes.Add(new JObject
                    {
                        ["element_id"] = id,
                        ["old_keynote"] = keynoteParam.AsString() ?? "",
                        ["new_keynote"] = keynote,
                        ["parameter_name"] = keynoteParam.Definition.Name
                    });
                }
            }
            else
            {
                using (var tx = new Transaction(doc, "RvtMcp: apply keynote to element"))
                {
                    tx.Start();
                    try
                    {
                        foreach (var id in elementIds)
                        {
                            if (!RevitCompat.CanRepresentElementId(id))
                            {
                                failed.Add(new JObject { ["element_id"] = id, ["error"] = RevitCompat.ElementIdRangeError(id) });
                                continue;
                            }

                            var elem = doc.GetElement(RevitCompat.ToElementId(id));
                            if (elem == null)
                            {
                                failed.Add(new JObject { ["element_id"] = id, ["error"] = "Element not found." });
                                continue;
                            }

                            var keynoteParam = FindKeynoteParameter(doc, elem);
                            if (keynoteParam == null)
                            {
                                failed.Add(new JObject { ["element_id"] = id, ["error"] = "Keynote parameter not found on element or type." });
                                continue;
                            }

                            if (keynoteParam.IsReadOnly)
                            {
                                failed.Add(new JObject { ["element_id"] = id, ["error"] = "Keynote parameter is read-only." });
                                continue;
                            }

                            if (keynoteParam.StorageType != StorageType.String)
                            {
                                failed.Add(new JObject { ["element_id"] = id, ["error"] = "Keynote parameter is not a string type." });
                                continue;
                            }

                            try
                            {
                                var oldVal = keynoteParam.AsString() ?? "";
                                keynoteParam.Set(keynote);
                                updatedCount++;
                                changes.Add(new JObject
                                {
                                    ["element_id"] = id,
                                    ["old_keynote"] = oldVal,
                                    ["new_keynote"] = keynote,
                                    ["parameter_name"] = keynoteParam.Definition.Name
                                });
                            }
                            catch (Exception ex)
                            {
                                failed.Add(new JObject { ["element_id"] = id, ["error"] = ex.Message });
                            }
                        }

                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                            return CommandResult.Fail("Apply keynote transaction did not commit. Status: " + status);
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Transaction commit failed: " + ex.Message);
                    }
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = dryRun,
                ["requested"] = elementIds.Count,
                ["updated"] = updatedCount,
                ["changes"] = changes,
                ["failed"] = failed,
                ["error"] = null
            });
        }

        private static Parameter FindKeynoteParameter(Document doc, Element elem)
        {
            if (elem == null) return null;

            // 1. Try instance parameter by built-in ID
            var p = elem.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
            if (p != null) return p;

            // 2. Try instance parameter by name
            p = elem.LookupParameter("Keynote");
            if (p != null) return p;

            // 3. Try type parameter
            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var type = doc.GetElement(typeId);
                if (type != null)
                {
                    p = type.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
                    if (p != null) return p;

                    p = type.LookupParameter("Keynote");
                    if (p != null) return p;
                }
            }

            return null;
        }
    }
}
