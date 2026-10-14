using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListLoadedFamiliesHandler : IRevitCommand
    {
        public string Name => "list_loaded_families";
        public string Description => "List all loaded families in the active Revit document, grouped by category. Returns family id, name, category, family-kind (system|loadable|inplace), type count, and instance count.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""category_filter"":{""type"":""string"",""description"":""Optional category name to filter (e.g. 'Doors', 'Windows'). Case-insensitive.""},""kind_filter"":{""type"":""string"",""enum"":[""all"",""system"",""loadable"",""inplace""],""default"":""all""},""include_instance_count"":{""type"":""boolean"",""default"":false,""description"":""If true, count placed instances per family (slower).""},""limit"":{""type"":""integer"",""default"":1000,""minimum"":1,""maximum"":10000}}}";

        // Built-in categories whose types we treat as "system families".
        // System families are not represented by a Family element — their types descend from ElementType subclasses.
        // We group those types by FamilyName to synthesize one family entry per group.
        private static readonly Type[] SystemFamilyTypeClasses = new Type[]
        {
            typeof(WallType),
            typeof(FloorType),
            typeof(CeilingType),
            typeof(RoofType),
            typeof(Autodesk.Revit.DB.Plumbing.PipeType),
            typeof(Autodesk.Revit.DB.Mechanical.DuctType),
            typeof(Autodesk.Revit.DB.Electrical.ConduitType),
            typeof(Autodesk.Revit.DB.Electrical.CableTrayType)
        };

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
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var categoryFilter = request.Value<string>("category_filter");
            var kindFilter = (request.Value<string>("kind_filter") ?? "all").ToLowerInvariant();
            var includeInstanceCount = request.Value<bool?>("include_instance_count") ?? false;
            var limit = request.Value<int?>("limit") ?? 1000;
            if (limit < 1) limit = 1;
            if (limit > 10000) limit = 10000;

            var results = new List<object>();

            // ---- Loadable + In-place families ----
            if (kindFilter == "all" || kindFilter == "loadable" || kindFilter == "inplace")
            {
                var families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .ToList();

                foreach (var fam in families)
                {
                    try
                    {
                        bool isInPlace = false;
                        try { isInPlace = fam.IsInPlace; } catch { isInPlace = false; }
                        var thisKind = isInPlace ? "inplace" : "loadable";

                        if (kindFilter == "loadable" && thisKind != "loadable") continue;
                        if (kindFilter == "inplace" && thisKind != "inplace") continue;

                        string categoryName = "Uncategorized";
                        ElementId categoryId = ElementId.InvalidElementId;
                        try
                        {
                            var famCat = fam.FamilyCategory;
                            if (famCat != null)
                            {
                                categoryName = famCat.Name ?? "Uncategorized";
                                categoryId = famCat.Id;
                            }
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(categoryFilter) &&
                            (categoryName == null ||
                             categoryName.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;

                        int typeCount = 0;
                        try
                        {
                            var typeIds = fam.GetFamilySymbolIds();
                            typeCount = typeIds != null ? typeIds.Count : 0;
                        }
                        catch { }

                        int instanceCount = -1;
                        if (includeInstanceCount)
                        {
                            instanceCount = 0;
                            try
                            {
                                if (categoryId != ElementId.InvalidElementId)
                                {
                                    var symbolIds = new HashSet<long>();
                                    try
                                    {
                                        foreach (var sid in fam.GetFamilySymbolIds())
                                            symbolIds.Add(RevitCompat.GetId(sid));
                                    }
                                    catch { }

                                    var instCollector = new FilteredElementCollector(doc)
                                        .OfCategoryId(categoryId)
                                        .WhereElementIsNotElementType();
                                    foreach (var elem in instCollector)
                                    {
                                        try
                                        {
                                            var fi = elem as FamilyInstance;
                                            if (fi == null) continue;
                                            if (fi.Symbol == null) continue;
                                            if (symbolIds.Contains(RevitCompat.GetId(fi.Symbol.Id)))
                                                instanceCount++;
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch
                            {
                                instanceCount = -1;
                            }
                        }

                        bool isEditable = false;
                        try { isEditable = fam.IsEditable; } catch { isEditable = false; }

                        results.Add(new
                        {
                            id = RevitCompat.GetId(fam.Id).ToString(),
                            name = fam.Name ?? string.Empty,
                            category = categoryName,
                            kind = thisKind,
                            type_count = typeCount,
                            instance_count = instanceCount,
                            is_editable = isEditable
                        });
                    }
                    catch
                    {
                        // Skip families that fail introspection
                    }
                }
            }

            // ---- System families ----
            if (kindFilter == "all" || kindFilter == "system")
            {
                foreach (var typeClass in SystemFamilyTypeClasses)
                {
                    List<ElementType> systemTypes;
                    try
                    {
                        systemTypes = new FilteredElementCollector(doc)
                            .OfClass(typeClass)
                            .Cast<ElementType>()
                            .ToList();
                    }
                    catch
                    {
                        continue;
                    }

                    // Group by (FamilyName, CategoryName)
                    var groups = new Dictionary<string, List<ElementType>>(StringComparer.Ordinal);
                    foreach (var et in systemTypes)
                    {
                        string famName;
                        try { famName = et.FamilyName ?? "<unknown>"; }
                        catch { famName = "<unknown>"; }

                        string catName = "Uncategorized";
                        try
                        {
                            if (et.Category != null)
                                catName = et.Category.Name ?? "Uncategorized";
                        }
                        catch { }

                        var key = catName + "||" + famName;
                        if (!groups.TryGetValue(key, out var list))
                        {
                            list = new List<ElementType>();
                            groups[key] = list;
                        }
                        list.Add(et);
                    }

                    foreach (var kv in groups)
                    {
                        try
                        {
                            var sep = kv.Key.IndexOf("||", StringComparison.Ordinal);
                            var categoryName = sep >= 0 ? kv.Key.Substring(0, sep) : "Uncategorized";
                            var familyName = sep >= 0 ? kv.Key.Substring(sep + 2) : kv.Key;

                            if (!string.IsNullOrEmpty(categoryFilter) &&
                                (categoryName == null ||
                                 categoryName.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0))
                                continue;

                            var firstType = kv.Value.FirstOrDefault();
                            string idStr;
                            ElementId catIdForInstances = ElementId.InvalidElementId;
                            if (firstType != null)
                            {
                                idStr = "system:" + RevitCompat.GetId(firstType.Id).ToString();
                                try
                                {
                                    if (firstType.Category != null)
                                        catIdForInstances = firstType.Category.Id;
                                }
                                catch { }
                            }
                            else
                            {
                                idStr = "system:" + familyName;
                            }

                            int instanceCount = -1;
                            if (includeInstanceCount && catIdForInstances != ElementId.InvalidElementId)
                            {
                                instanceCount = 0;
                                try
                                {
                                    var typeIds = new HashSet<long>();
                                    foreach (var et in kv.Value)
                                        typeIds.Add(RevitCompat.GetId(et.Id));

                                    var instCollector = new FilteredElementCollector(doc)
                                        .OfCategoryId(catIdForInstances)
                                        .WhereElementIsNotElementType();
                                    foreach (var elem in instCollector)
                                    {
                                        try
                                        {
                                            var tid = elem.GetTypeId();
                                            if (tid == null || tid == ElementId.InvalidElementId) continue;
                                            if (typeIds.Contains(RevitCompat.GetId(tid)))
                                                instanceCount++;
                                        }
                                        catch { }
                                    }
                                }
                                catch
                                {
                                    instanceCount = -1;
                                }
                            }

                            results.Add(new
                            {
                                id = idStr,
                                name = familyName,
                                category = categoryName,
                                kind = "system",
                                type_count = kv.Value.Count,
                                instance_count = instanceCount,
                                is_editable = false
                            });
                        }
                        catch
                        {
                            // Skip groups that fail
                        }
                    }
                }
            }

            // Stable ordering: category, then kind, then name
            var ordered = results
                .Select(r => new { Obj = r, J = JObject.FromObject(r) })
                .OrderBy(x => x.J.Value<string>("category") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.J.Value<string>("kind") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.J.Value<string>("name") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Obj)
                .ToList();

            var total = ordered.Count;
            var limitHit = total > limit;
            var returned = limitHit ? ordered.Take(limit).ToList() : ordered;

            return CommandResult.Ok(new
            {
                doc_title = doc.Title ?? string.Empty,
                total_families = total,
                returned_families = returned.Count,
                limit_hit = limitHit,
                families = returned.ToArray()
            });
        }
    }
}
