using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AuditFamiliesHandler : IRevitCommand
    {
        public string Name => "audit_families";

        public string Description =>
            "Read-only audit of loaded families: detect in-place families, unused families (zero placed instances), duplicate names, and unusual size/type-count hints. Returns recommendations.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""include_unused"":{""type"":""boolean"",""default"":true},""include_inplace"":{""type"":""boolean"",""default"":true},""include_duplicate_names"":{""type"":""boolean"",""default"":true},""include_high_type_count"":{""type"":""boolean"",""default"":true},""high_type_count_threshold"":{""type"":""integer"",""default"":20,""minimum"":1},""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""limit_per_section"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":500}}}";

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

            bool includeUnused = request.Value<bool?>("include_unused") ?? true;
            bool includeInplace = request.Value<bool?>("include_inplace") ?? true;
            bool includeDuplicates = request.Value<bool?>("include_duplicate_names") ?? true;
            bool includeHighTypeCount = request.Value<bool?>("include_high_type_count") ?? true;
            int highTypeCountThreshold = request.Value<int?>("high_type_count_threshold") ?? 20;
            if (highTypeCountThreshold < 1) highTypeCountThreshold = 1;
            if (!ResponsePaging.TryParse(request, "start_index", "limit_per_section", 100, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);

            // Collect loadable + in-place families only (system families are excluded:
            // they are not represented by Family elements).
            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToList();

            // Per-family info we precompute once.
            var infos = new List<FamilyInfo>(families.Count);
            // Map symbol-id -> owning family-id for fast O(1) instance attribution.
            var symbolToFamily = new Dictionary<long, long>();
            // Category-id -> list of family ids using that category (drives single-pass collectors).
            var categoryToFamilyIds = new Dictionary<long, HashSet<long>>();

            foreach (var fam in families)
            {
                FamilyInfo info;
                try
                {
                    info = new FamilyInfo
                    {
                        IdLong = RevitCompat.GetId(fam.Id),
                        IdString = RevitCompat.GetId(fam.Id).ToString(),
                        Name = fam.Name ?? string.Empty,
                        IsInPlace = SafeIsInPlace(fam),
                        CategoryName = "Uncategorized",
                        CategoryId = ElementId.InvalidElementId,
                        TypeCount = 0,
                        InstanceCount = 0
                    };
                }
                catch
                {
                    continue;
                }

                try
                {
                    var cat = fam.FamilyCategory;
                    if (cat != null)
                    {
                        info.CategoryName = cat.Name ?? "Uncategorized";
                        info.CategoryId = cat.Id;
                    }
                }
                catch { }

                // Track symbol ids for instance attribution.
                try
                {
                    var symIds = fam.GetFamilySymbolIds();
                    if (symIds != null)
                    {
                        info.TypeCount = symIds.Count;
                        foreach (var sid in symIds)
                        {
                            try
                            {
                                symbolToFamily[RevitCompat.GetId(sid)] = info.IdLong;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (info.CategoryId != ElementId.InvalidElementId)
                {
                    var catKey = RevitCompat.GetId(info.CategoryId);
                    if (!categoryToFamilyIds.TryGetValue(catKey, out var fidSet))
                    {
                        fidSet = new HashSet<long>();
                        categoryToFamilyIds[catKey] = fidSet;
                    }
                    fidSet.Add(info.IdLong);
                }

                infos.Add(info);
            }

            var familyById = infos.ToDictionary(f => f.IdLong);

            // Single-pass instance counting per category (avoids O(N families x M instances) blowup).
            if (includeUnused)
            {
                foreach (var kv in categoryToFamilyIds)
                {
                    ElementId catId;
                    try { catId = RevitCompat.ToElementId(kv.Key); }
                    catch { continue; }

                    FilteredElementCollector instCollector;
                    try
                    {
                        instCollector = new FilteredElementCollector(doc)
                            .OfCategoryId(catId)
                            .WhereElementIsNotElementType();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var elem in instCollector)
                    {
                        try
                        {
                            var fi = elem as FamilyInstance;
                            if (fi == null || fi.Symbol == null) continue;
                            var symLong = RevitCompat.GetId(fi.Symbol.Id);
                            if (symbolToFamily.TryGetValue(symLong, out var owningFamId)
                                && familyById.TryGetValue(owningFamId, out var owningInfo))
                            {
                                owningInfo.InstanceCount++;
                            }
                        }
                        catch { }
                    }
                }
            }

            // Build buckets.
            var unusedOut = new List<object>();
            var inplaceOut = new List<object>();
            var highTypeOut = new List<object>();
            var dupGroupsOut = new List<object>();
            var recommendations = new List<string>();

            if (includeInplace)
            {
                foreach (var f in infos.Where(x => x.IsInPlace)
                                       .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    inplaceOut.Add(new
                    {
                        id = f.IdString,
                        name = f.Name,
                        category = f.CategoryName
                    });
                    recommendations.Add(
                        $"In-place family '{f.Name}' ({f.CategoryName}) — consider converting to a loadable family for reuse and lighter model size.");
                }
            }

            if (includeUnused)
            {
                foreach (var f in infos.Where(x => x.InstanceCount == 0)
                                       .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    unusedOut.Add(new
                    {
                        id = f.IdString,
                        name = f.Name,
                        category = f.CategoryName
                    });
                    recommendations.Add(
                        $"Family '{f.Name}' has 0 placed instances — consider purging.");
                }
            }

            if (includeHighTypeCount)
            {
                foreach (var f in infos.Where(x => x.TypeCount >= highTypeCountThreshold)
                                       .OrderByDescending(x => x.TypeCount)
                                       .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    highTypeOut.Add(new
                    {
                        id = f.IdString,
                        name = f.Name,
                        type_count = f.TypeCount
                    });
                    recommendations.Add(
                        $"Family '{f.Name}' has {f.TypeCount} types (threshold {highTypeCountThreshold}) — review for unused/redundant types.");
                }
            }

            if (includeDuplicates)
            {
                var byName = infos
                    .Where(x => !string.IsNullOrEmpty(x.Name))
                    .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var grp in byName)
                {
                    var members = grp.ToList();
                    dupGroupsOut.Add(new
                    {
                        name = grp.Key,
                        ids = members.Select(m => m.IdString).ToArray(),
                        categories = members.Select(m => m.CategoryName).ToArray()
                    });
                    recommendations.Add(
                        $"Duplicate family name '{grp.Key}' appears {members.Count} times across categories [{string.Join(", ", members.Select(m => m.CategoryName).Distinct())}] — rename for clarity.");
                }
            }

            if (recommendations.Count == 0)
            {
                recommendations.Add("No family-level issues detected with current options.");
            }

            var unusedPage = ResponsePaging.Slice(unusedOut, paging.StartIndex, paging.MaxResults);
            var inplacePage = ResponsePaging.Slice(inplaceOut, paging.StartIndex, paging.MaxResults);
            var duplicatePage = ResponsePaging.Slice(dupGroupsOut, paging.StartIndex, paging.MaxResults);
            var highTypePage = ResponsePaging.Slice(highTypeOut, paging.StartIndex, paging.MaxResults);
            var recommendationPage = ResponsePaging.Slice(recommendations, paging.StartIndex, paging.MaxResults);
            var nextIndices = new[] { unusedPage.NextIndex, inplacePage.NextIndex, duplicatePage.NextIndex, highTypePage.NextIndex, recommendationPage.NextIndex }
                .Where(i => i.HasValue).Select(i => i.Value).ToArray();

            return CommandResult.Ok(new
            {
                doc_title = doc.Title ?? string.Empty,
                total_families_scanned = infos.Count,
                start_index = paging.StartIndex,
                limit_per_section = paging.MaxResults,
                section_counts = new
                {
                    unused = unusedPage.TotalCount,
                    inplace = inplacePage.TotalCount,
                    duplicate_name_groups = duplicatePage.TotalCount,
                    high_type_count = highTypePage.TotalCount,
                    recommendations = recommendationPage.TotalCount
                },
                truncated = unusedPage.Truncated || inplacePage.Truncated || duplicatePage.Truncated
                    || highTypePage.Truncated || recommendationPage.Truncated,
                next_index = nextIndices.Length > 0 ? (int?)nextIndices.Max() : null,
                unused_families = unusedPage.Items,
                inplace_families = inplacePage.Items,
                duplicate_name_groups = duplicatePage.Items,
                high_type_count_families = highTypePage.Items,
                recommendations = recommendationPage.Items
            });
        }

        private static bool SafeIsInPlace(Family fam)
        {
            try { return fam.IsInPlace; }
            catch { return false; }
        }

        private class FamilyInfo
        {
            public long IdLong;
            public string IdString;
            public string Name;
            public bool IsInPlace;
            public string CategoryName;
            public ElementId CategoryId;
            public int TypeCount;
            public int InstanceCount;
        }
    }
}
