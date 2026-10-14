using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AssignElementsToWorksetHandler : IRevitCommand
    {
        public string Name => "assign_elements_to_workset";
        public string Description => "Assign one or more elements to a user workset by workset ID or name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""worksetId"":{""type"":""integer""},""worksetName"":{""type"":""string""}},""required"":[""elementIds""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            if (!doc.IsWorkshared)
                return CommandResult.Fail("Document is not workshared; user worksets are not available.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            if (!TryReadIdArray(request, "elementIds", out var elementIds, out var elementIdsError))
                return CommandResult.Fail(elementIdsError);

            if (elementIds.Count == 0)
                return CommandResult.Fail("elementIds array is required.");

            if (!TryReadOptionalIntId(request, "worksetId", out var requestedWorksetId, out var worksetIdError))
                return CommandResult.Fail(worksetIdError);

            var requestedWorksetName = request.Value<string>("worksetName");

            if (!requestedWorksetId.HasValue && string.IsNullOrWhiteSpace(requestedWorksetName))
                return CommandResult.Fail("Either worksetId or worksetName is required.");

            var targetWorkset = ResolveUserWorkset(doc, requestedWorksetId, requestedWorksetName, out var worksetError);
            if (targetWorkset == null)
                return CommandResult.Fail(worksetError);

            var targetWorksetId = targetWorkset.Id.IntegerValue;
            var targetWorksetName = targetWorkset.Name;
            var updated = new List<object>();
            var failed = new List<object>();
            var errors = new List<string>();

            using (var tx = new Transaction(doc, "MCP: Assign elements to workset"))
            {
                tx.Start();

                foreach (var requestedElementId in elementIds)
                {
                    WorksetInfo oldWorkset = null;
                    try
                    {
                        var element = doc.GetElement(RevitCompat.ToElementId(requestedElementId));
                        if (element == null)
                        {
                            AddFailure(failed, errors, requestedElementId, null, targetWorksetId, targetWorksetName, "Element not found.");
                            continue;
                        }

                        oldWorkset = GetWorksetInfo(doc, element);
                        var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (parameter == null)
                        {
                            AddFailure(failed, errors, requestedElementId, oldWorkset, targetWorksetId, targetWorksetName, "Element does not expose a writable workset parameter.");
                            continue;
                        }

                        if (parameter.IsReadOnly)
                        {
                            AddFailure(failed, errors, requestedElementId, oldWorkset, targetWorksetId, targetWorksetName, "Element workset parameter is read-only.");
                            continue;
                        }

                        var changed = oldWorkset == null || oldWorkset.Id != targetWorksetId;
                        if (changed)
                        {
                            var setAccepted = SetWorksetParameter(parameter, targetWorksetId, out var setError);
                            if (!setAccepted)
                            {
                                var currentParameterWorksetId = ReadWorksetId(parameter);
                                if (currentParameterWorksetId != targetWorksetId)
                                {
                                    AddFailure(failed, errors, requestedElementId, oldWorkset, targetWorksetId, targetWorksetName, setError);
                                    continue;
                                }
                            }
                        }

                        updated.Add(new
                        {
                            elementId = requestedElementId,
                            oldWorksetId = oldWorkset?.Id,
                            oldWorksetName = oldWorkset?.Name,
                            newWorksetId = targetWorksetId,
                            newWorksetName = targetWorksetName,
                            changed
                        });
                    }
                    catch (Exception ex)
                    {
                        AddFailure(failed, errors, requestedElementId, oldWorkset, targetWorksetId, targetWorksetName, ex.Message);
                    }
                }

                var status = tx.Commit();
                if (status != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit workset assignments. Transaction status: " + status);
            }

            return CommandResult.Ok(new
            {
                requested = elementIds.Count,
                updated,
                failed,
                errors,
                updatedCount = updated.Count,
                failedCount = failed.Count,
                newWorksetId = targetWorksetId,
                newWorksetName = targetWorksetName
            });
        }

        private static bool TryReadIdArray(JObject request, string propertyName, out List<long> ids, out string error)
        {
            ids = new List<long>();
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = propertyName + " array is required.";
                return false;
            }

            if (token.Type != JTokenType.Array)
            {
                error = propertyName + " must be an array of integers.";
                return false;
            }

            var array = (JArray)token;
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i].Type != JTokenType.Integer)
                {
                    error = propertyName + "[" + i.ToString(CultureInfo.InvariantCulture) + "] must be an integer.";
                    return false;
                }

                var id = array[i].Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    error = propertyName + "[" + i.ToString(CultureInfo.InvariantCulture) + "] " + RevitCompat.ElementIdRangeError(id);
                    return false;
                }

                ids.Add(id);
            }

            return true;
        }

        private static bool TryReadOptionalIntId(JObject request, string propertyName, out int? id, out string error)
        {
            id = null;
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Integer)
            {
                error = propertyName + " must be an integer.";
                return false;
            }

            var value = token.Value<long>();
            if (value < int.MinValue || value > int.MaxValue)
            {
                error = propertyName + " " + RevitCompat.ElementIdRangeError(value);
                return false;
            }

            id = (int)value;
            return true;
        }

        private static Workset ResolveUserWorkset(Document doc, int? requestedId, string requestedName, out string error)
        {
            error = null;
            var userWorksets = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .ToList();

            Workset byId = null;
            if (requestedId.HasValue)
            {
                byId = userWorksets.FirstOrDefault(w => w.Id.IntegerValue == requestedId.Value);
                if (byId == null)
                {
                    error = $"User workset with ID {requestedId.Value} was not found.";
                    return null;
                }
            }

            Workset byName = null;
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                byName = userWorksets.FirstOrDefault(w => string.Equals(w.Name, requestedName, StringComparison.OrdinalIgnoreCase));
                if (byName == null)
                {
                    error = $"User workset named '{requestedName}' was not found.";
                    return null;
                }
            }

            if (byId != null && byName != null && byId.Id.IntegerValue != byName.Id.IntegerValue)
            {
                error = $"worksetId {requestedId.Value} and worksetName '{requestedName}' refer to different user worksets.";
                return null;
            }

            return byId ?? byName;
        }

        private static bool SetWorksetParameter(Parameter parameter, int worksetId, out string error)
        {
            error = null;
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.Integer:
                        if (parameter.Set(worksetId))
                            return true;
                        error = "Revit rejected the workset parameter update.";
                        return false;

                    case StorageType.ElementId:
                        if (parameter.Set(RevitCompat.ToElementId(worksetId)))
                            return true;
                        error = "Revit rejected the workset parameter update.";
                        return false;

                    default:
                        error = $"Workset parameter storage type '{parameter.StorageType}' is not supported.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static int? ReadWorksetId(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.Integer:
                        return parameter.AsInteger();
                    case StorageType.ElementId:
                        var elementId = parameter.AsElementId();
                        var id = RevitCompat.GetIdOrNull(elementId);
                        if (!id.HasValue || id.Value < int.MinValue || id.Value > int.MaxValue)
                            return null;
                        return (int)id.Value;
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static WorksetInfo GetWorksetInfo(Document doc, Element element)
        {
            try
            {
                var worksetId = element.WorksetId;
                if (worksetId == null || worksetId.IntegerValue <= 0)
                    return null;

                string worksetName = null;
                try
                {
                    worksetName = doc.GetWorksetTable()?.GetWorkset(worksetId)?.Name;
                }
                catch
                {
                    worksetName = null;
                }

                return new WorksetInfo
                {
                    Id = worksetId.IntegerValue,
                    Name = worksetName
                };
            }
            catch
            {
                return null;
            }
        }

        private static void AddFailure(
            List<object> failed,
            List<string> errors,
            long elementId,
            WorksetInfo oldWorkset,
            int targetWorksetId,
            string targetWorksetName,
            string error)
        {
            failed.Add(new
            {
                elementId,
                oldWorksetId = oldWorkset?.Id,
                oldWorksetName = oldWorkset?.Name,
                newWorksetId = targetWorksetId,
                newWorksetName = targetWorksetName,
                error
            });
            errors.Add($"{elementId}: {error}");
        }

        private class WorksetInfo
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
    }
}
