using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetSchedulableFieldsHandler : IRevitCommand
    {
        public string Name => "get_schedulable_fields";
        public string Description => "List parameters that CAN be added as fields to a schedule but have not been added yet. Pre-step for add_schedule_field — call this to discover valid parameter names.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""kindFilter"":{""type"":""array"",""items"":{""type"":""string""}}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var scheduleIdToken = request["scheduleId"];
            var scheduleName = request.Value<string>("scheduleName");

            // kindFilter — case-insensitive set
            HashSet<string> kindFilter = null;
            var kindFilterToken = request["kindFilter"];
            if (kindFilterToken != null && kindFilterToken.Type == JTokenType.Array)
            {
                kindFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in kindFilterToken)
                {
                    var s = t?.Value<string>();
                    if (!string.IsNullOrEmpty(s)) kindFilter.Add(s);
                }
                if (kindFilter.Count == 0) kindFilter = null;
            }

            // Resolve ViewSchedule by id or name
            ViewSchedule schedule = null;
            if (scheduleIdToken != null && scheduleIdToken.Type != JTokenType.Null)
            {
                long idValue;
                try { idValue = scheduleIdToken.Value<long>(); }
                catch { return CommandResult.Fail("scheduleId must be an integer."); }

                if (!RevitCompat.CanRepresentElementId(idValue))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(idValue));

                var el = doc.GetElement(RevitCompat.ToElementId(idValue));
                schedule = el as ViewSchedule;
                if (schedule == null)
                    return CommandResult.Fail($"Element {idValue} is not a ViewSchedule or not found.");
            }
            else if (!string.IsNullOrEmpty(scheduleName))
            {
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => s.Name != null &&
                                s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail($"Schedule '{scheduleName}' not found.");
                if (matches.Count > 1)
                    return CommandResult.Fail($"Ambiguous schedule name '{scheduleName}': {matches.Count} matches found. Use scheduleId.");
                schedule = matches[0];
            }
            else
            {
                return CommandResult.Fail("Either scheduleId or scheduleName is required.");
            }

            var definition = schedule.Definition;
            if (definition == null)
                return CommandResult.Fail("Schedule has no definition.");

            // Build set of already-added composite keys: parameterId|fieldType
            var addedKeys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var fieldOrder = definition.GetFieldOrder();
                if (fieldOrder != null)
                {
                    foreach (var fieldId in fieldOrder)
                    {
                        try
                        {
                            var field = definition.GetField(fieldId);
                            if (field == null) continue;
                            var sf = field.GetSchedulableField();
                            if (sf == null) continue;
                            var pid = RevitCompat.GetIdOrNull(sf.ParameterId);
                            var ft = sf.FieldType.ToString();
                            var key = (pid.HasValue ? pid.Value.ToString() : "null") + "|" + ft;
                            addedKeys.Add(key);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            int alreadyAdded = addedKeys.Count;
            int skipped = 0;
            int total = 0;

            // Iterate SchedulableFields
            IList<SchedulableField> all;
            try { all = definition.GetSchedulableFields(); }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to get schedulable fields: {ex.Message}");
            }
            if (all == null) all = new List<SchedulableField>();
            total = all.Count;

            var fields = new List<object>();
            foreach (var sf in all)
            {
                try
                {
                    var pidNullable = RevitCompat.GetIdOrNull(sf.ParameterId);
                    var ftStr = sf.FieldType.ToString();
                    var key = (pidNullable.HasValue ? pidNullable.Value.ToString() : "null") + "|" + ftStr;

                    if (addedKeys.Contains(key)) continue;

                    string paramName = null;
                    try { paramName = sf.GetName(doc); }
                    catch { paramName = null; }

                    // specTypeId via reflection (may not exist on SchedulableField in some versions)
                    string specTypeId = null;
                    try
                    {
                        // TODO: GetSpecTypeId may not be available on SchedulableField across all Revit versions.
                        var mi = sf.GetType().GetMethod("GetSpecTypeId", BindingFlags.Public | BindingFlags.Instance);
                        if (mi != null)
                        {
                            var ftid = mi.Invoke(sf, null);
                            if (ftid != null)
                            {
                                var typeIdProp = ftid.GetType().GetProperty("TypeId", BindingFlags.Public | BindingFlags.Instance);
                                if (typeIdProp != null)
                                {
                                    specTypeId = typeIdProp.GetValue(ftid) as string;
                                }
                            }
                        }
                    }
                    catch
                    {
                        specTypeId = null;
                    }

                    // Source detection
                    string source;
                    bool isShared = false;
                    if (sf.ParameterId == ElementId.InvalidElementId)
                    {
                        source = "BuiltIn";
                    }
                    else
                    {
                        Element pEl = null;
                        try { pEl = doc.GetElement(sf.ParameterId); }
                        catch { pEl = null; }

                        if (pEl is SharedParameterElement)
                        {
                            source = "Shared";
                            isShared = true;
                        }
                        else if (pEl is ParameterElement)
                        {
                            source = "Project";
                        }
                        else
                        {
                            source = "BuiltIn";
                        }
                    }

                    // Apply kindFilter
                    if (kindFilter != null && !kindFilter.Contains(ftStr))
                        continue;

                    fields.Add(new
                    {
                        parameterName = paramName,
                        parameterId = pidNullable,
                        fieldType = ftStr,
                        specTypeId = specTypeId,
                        source = source,
                        isShared = isShared
                    });
                }
                catch
                {
                    skipped++;
                }
            }

            // Sort by parameterName (case-insensitive). Anonymous types require dynamic access — re-project.
            var sorted = fields
                .Select(o => new
                {
                    Obj = o,
                    Name = (string)o.GetType().GetProperty("parameterName")?.GetValue(o)
                })
                .OrderBy(x => x.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Obj)
                .ToArray();

            return CommandResult.Ok(new
            {
                scheduleId = RevitCompat.GetId(schedule.Id),
                scheduleName = schedule.Name,
                total = total,
                alreadyAdded = alreadyAdded,
                available = sorted.Length,
                fields = sorted,
                _skipped = skipped
            });
        }
    }
}
