using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetFamilyInstancesHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "get_family_instances";
        public string Description => "List placed instances of a Family (or a specific type). Returns location (point|line) in mm, level, host, and Mark for each instance. Resolve family by family_id or family_name; optionally filter by type_name and/or active-view scope.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""family_id"":{""type"":""string"",""description"":""Family ElementId. Either family_id or family_name required.""},""family_name"":{""type"":""string""},""type_name"":{""type"":""string"",""description"":""Optional: only instances of this type.""},""view_only"":{""type"":""boolean"",""default"":false,""description"":""If true, only instances visible in active view.""},""limit"":{""type"":""integer"",""default"":1000,""minimum"":1,""maximum"":10000}}}";

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

            var familyIdStr = request.Value<string>("family_id");
            var familyName = request.Value<string>("family_name");
            var typeName = request.Value<string>("type_name");
            var viewOnly = request.Value<bool?>("view_only") ?? false;
            var limit = request.Value<int?>("limit") ?? 1000;
            if (limit < 1) limit = 1;
            if (limit > 10000) limit = 10000;

            if (string.IsNullOrWhiteSpace(familyIdStr) && string.IsNullOrWhiteSpace(familyName))
                return CommandResult.Fail("Either family_id or family_name is required.");

            // ---- Resolve Family ----
            Family family = null;

            if (!string.IsNullOrWhiteSpace(familyIdStr))
            {
                if (!long.TryParse(familyIdStr.Trim(), out var famIdNum))
                    return CommandResult.Fail($"family_id '{familyIdStr}' is not a valid integer id.");
                if (!RevitCompat.CanRepresentElementId(famIdNum))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(famIdNum));

                Element famEl;
                try
                {
                    famEl = doc.GetElement(RevitCompat.ToElementId(famIdNum));
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail($"Failed to fetch element id {famIdNum}: {ex.Message}");
                }

                if (famEl == null)
                    return CommandResult.Fail($"No element found with id {famIdNum}.");
                family = famEl as Family;
                if (family == null)
                    return CommandResult.Fail($"Element id {famIdNum} is not a Family (got {famEl.GetType().Name}).");
            }
            else
            {
                var families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (families.Count == 0)
                    return CommandResult.Fail($"Family '{familyName}' not found.");
                if (families.Count > 1)
                    return CommandResult.Fail($"Ambiguous family name '{familyName}': {families.Count} matches found. Use family_id.");
                family = families[0];
            }

            // ---- Resolve symbol-id set (optionally filtered by type_name) ----
            ICollection<ElementId> symbolIds;
            try
            {
                symbolIds = family.GetFamilySymbolIds();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to enumerate family types: {ex.Message}");
            }

            if (symbolIds == null || symbolIds.Count == 0)
                return CommandResult.Ok(new
                {
                    family_id = RevitCompat.GetId(family.Id).ToString(),
                    family_name = family.Name ?? string.Empty,
                    category = SafeCategoryName(family),
                    type_filter = typeName,
                    view_only = viewOnly,
                    total_instances = 0,
                    returned = 0,
                    limit_hit = false,
                    instances = new object[0]
                });

            // Filter symbol ids if type_name is provided
            var symbolIdSet = new HashSet<long>();
            var matchedTypeNames = new List<string>();
            foreach (var sid in symbolIds)
            {
                try
                {
                    if (string.IsNullOrEmpty(typeName))
                    {
                        symbolIdSet.Add(RevitCompat.GetId(sid));
                    }
                    else
                    {
                        var symEl = doc.GetElement(sid) as FamilySymbol;
                        if (symEl == null) continue;
                        if (string.Equals(symEl.Name, typeName, StringComparison.OrdinalIgnoreCase))
                        {
                            symbolIdSet.Add(RevitCompat.GetId(sid));
                            matchedTypeNames.Add(symEl.Name);
                        }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(typeName) && symbolIdSet.Count == 0)
                return CommandResult.Fail($"Type '{typeName}' not found in family '{family.Name}'.");

            // ---- Collect instances ----
            FilteredElementCollector collector;
            try
            {
                if (viewOnly)
                {
                    var activeView = doc.ActiveView;
                    if (activeView == null)
                        return CommandResult.Fail("view_only=true but the document has no active view.");
                    collector = new FilteredElementCollector(doc, activeView.Id).OfClass(typeof(FamilyInstance));
                }
                else
                {
                    collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance))
                        .WhereElementIsNotElementType();
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to build collector: {ex.Message}");
            }

            var matching = new List<FamilyInstance>();
            foreach (var el in collector)
            {
                try
                {
                    var fi = el as FamilyInstance;
                    if (fi == null) continue;
                    if (fi.Symbol == null) continue;
                    if (!symbolIdSet.Contains(RevitCompat.GetId(fi.Symbol.Id))) continue;
                    matching.Add(fi);
                }
                catch { }
            }

            var total = matching.Count;
            var limitHit = total > limit;
            var take = limitHit ? limit : total;

            var instances = new List<object>(take);
            for (var i = 0; i < take; i++)
            {
                var fi = matching[i];
                try
                {
                    instances.Add(BuildInstanceDto(doc, fi));
                }
                catch (Exception ex)
                {
                    instances.Add(new
                    {
                        id = RevitCompat.GetId(fi.Id).ToString(),
                        _error = ex.Message
                    });
                }
            }

            return CommandResult.Ok(new
            {
                family_id = RevitCompat.GetId(family.Id).ToString(),
                family_name = family.Name ?? string.Empty,
                category = SafeCategoryName(family),
                type_filter = string.IsNullOrEmpty(typeName) ? null : typeName,
                view_only = viewOnly,
                total_instances = total,
                returned = instances.Count,
                limit_hit = limitHit,
                instances = instances.ToArray()
            });
        }

        private static object BuildInstanceDto(Document doc, FamilyInstance fi)
        {
            // type
            string typeIdStr = null;
            string typeNameStr = null;
            try
            {
                var sym = fi.Symbol;
                if (sym != null)
                {
                    typeIdStr = RevitCompat.GetId(sym.Id).ToString();
                    typeNameStr = sym.Name;
                }
            }
            catch { }

            // level: prefer instance.LevelId, fallback to host.LevelId
            ElementId levelId = null;
            try { levelId = fi.LevelId; } catch { levelId = null; }
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                try
                {
                    var host = fi.Host;
                    if (host != null)
                        levelId = host.LevelId;
                }
                catch { }
            }

            string levelIdStr = null;
            string levelNameStr = null;
            if (levelId != null && levelId != ElementId.InvalidElementId)
            {
                try
                {
                    levelIdStr = RevitCompat.GetId(levelId).ToString();
                    var lvlEl = doc.GetElement(levelId);
                    if (lvlEl != null) levelNameStr = lvlEl.Name;
                }
                catch { }
            }

            // host
            string hostIdStr = null;
            string hostNameStr = null;
            try
            {
                var host = fi.Host;
                if (host != null)
                {
                    hostIdStr = RevitCompat.GetId(host.Id).ToString();
                    hostNameStr = host.Name;
                }
            }
            catch { }

            // mark
            string markStr = null;
            try
            {
                var markParam = fi.LookupParameter("Mark");
                if (markParam != null && markParam.HasValue)
                    markStr = markParam.AsString();
            }
            catch { }

            // location — may be null for some nested / view-specific / hosted instances
            string locationKind = null;
            double[] locPoint = null;
            double[] locLineStart = null;
            double[] locLineEnd = null;

            try
            {
                var loc = fi.Location;
                if (loc is LocationPoint lp)
                {
                    locationKind = "point";
                    try
                    {
                        var p = lp.Point;
                        if (p != null) locPoint = ToMmArray(p);
                    }
                    catch { }
                }
                else if (loc is LocationCurve lc)
                {
                    locationKind = "line";
                    try
                    {
                        var curve = lc.Curve;
                        if (curve != null && curve.IsBound)
                        {
                            locLineStart = ToMmArray(curve.GetEndPoint(0));
                            locLineEnd = ToMmArray(curve.GetEndPoint(1));
                        }
                    }
                    catch { }
                }
                // else: locationKind stays null (no Location, common for nested families)
            }
            catch { }

            return new
            {
                id = RevitCompat.GetId(fi.Id).ToString(),
                type_id = typeIdStr,
                type_name = typeNameStr,
                level_id = levelIdStr,
                level_name = levelNameStr,
                location_kind = locationKind,
                location_point_mm = locPoint,
                location_line_start_mm = locLineStart,
                location_line_end_mm = locLineEnd,
                host_id = hostIdStr,
                host_name = hostNameStr,
                mark = markStr
            };
        }

        private static double[] ToMmArray(XYZ p)
        {
            if (p == null) return null;
            return new[]
            {
                Math.Round(p.X * FeetToMm, 3),
                Math.Round(p.Y * FeetToMm, 3),
                Math.Round(p.Z * FeetToMm, 3)
            };
        }

        private static string SafeCategoryName(Family family)
        {
            try
            {
                var cat = family.FamilyCategory;
                if (cat != null && !string.IsNullOrEmpty(cat.Name))
                    return cat.Name;
            }
            catch { }

            try
            {
                if (family.Category != null && !string.IsNullOrEmpty(family.Category.Name))
                    return family.Category.Name;
            }
            catch { }

            return "Uncategorized";
        }
    }
}
