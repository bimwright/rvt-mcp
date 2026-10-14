using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListSchedulesHandler : IRevitCommand
    {
        public string Name => "list_schedules";
        public string Description => "List all schedules in the project. Optional filters: categoryFilter (case-insensitive substring on resolved category name), namePattern (case-insensitive substring on schedule name).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""categoryFilter"":{""type"":""string""},""namePattern"":{""type"":""string""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var categoryFilter = request.Value<string>("categoryFilter");
            var namePattern = request.Value<string>("namePattern");

            // Build BuiltInCategory → name map once
            var categoryNameById = new Dictionary<long, string>();
            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat != null)
                    {
                        var key = RevitCompat.GetId(cat.Id);
                        if (!categoryNameById.ContainsKey(key))
                            categoryNameById[key] = cat.Name;
                    }
                }
                catch { }
            }

            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .ToList();

            var results = new List<object>();
            var skipped = 0;
            foreach (var sched in schedules)
            {
                try
                {
                    var definition = sched.Definition;
                    string categoryName;
                    if (definition == null)
                    {
                        categoryName = "<unknown>";
                    }
                    else if (definition.CategoryId == ElementId.InvalidElementId)
                    {
                        categoryName = "<multi-category>";
                    }
                    else
                    {
                        var catKey = RevitCompat.GetId(definition.CategoryId);
                        if (!categoryNameById.TryGetValue(catKey, out categoryName))
                        {
                            try
                            {
                                categoryName = Category.GetCategory(doc, definition.CategoryId)?.Name ?? "<unknown>";
                            }
                            catch
                            {
                                categoryName = "<unknown>";
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(categoryFilter) &&
                        (categoryName == null ||
                         categoryName.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    if (!string.IsNullOrEmpty(namePattern) &&
                        (sched.Name == null ||
                         sched.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    int fieldCount = 0;
                    int filterCount = 0;
                    int sortGroupCount = 0;
                    bool isKey = false;
                    bool isItemized = false;
                    if (definition != null)
                    {
                        try { fieldCount = definition.GetFieldCount(); } catch { }
                        try { filterCount = definition.GetFilterCount(); } catch { }
                        try { sortGroupCount = definition.GetSortGroupFieldCount(); } catch { }
                        try { isKey = definition.IsKeySchedule; } catch { }
                        try { isItemized = definition.IsItemized; } catch { }
                    }

                    results.Add(new
                    {
                        id = RevitCompat.GetId(sched.Id),
                        name = sched.Name,
                        categoryName = categoryName,
                        isKey = isKey,
                        isTitleblockRevisionSchedule = sched.IsTitleblockRevisionSchedule,
                        isInternalKeynoteSchedule = sched.IsInternalKeynoteSchedule,
                        isTemplate = sched.IsTemplate,
                        fieldCount = fieldCount,
                        filterCount = filterCount,
                        sortGroupCount = sortGroupCount,
                        isItemized = isItemized
                    });
                }
                catch
                {
                    // Skip schedules that fail introspection
                    skipped++;
                }
            }

            return CommandResult.Ok(new
            {
                total = results.Count,
                skipped = skipped,
                truncated = false,
                schedules = results.ToArray()
            });
        }
    }
}
