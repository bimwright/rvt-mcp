using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Replace all instances of FamilySymbol (or system ElementType) A with FamilySymbol B
    /// across the project, the active view, or the current selection. Supports dry-run preview.
    /// </summary>
    public class ReplaceFamilyTypeHandler : IRevitCommand
    {
        public string Name => "replace_family_type";

        public string Description =>
            "Replace all instances of FamilySymbol A with FamilySymbol B across the project " +
            "(or within a scope: all, active_view, selection). Supports dry-run preview. " +
            "Both types must be the same category and host-compatible.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""from_type_id"", ""to_type_id""],
  ""properties"": {
    ""from_type_id"": {""type"": ""string"", ""description"": ""Source FamilySymbol (or system type) ElementId.""},
    ""to_type_id"": {""type"": ""string"", ""description"": ""Target FamilySymbol (or system type) ElementId. Must be same category & host-compatible.""},
    ""scope"": {""type"": ""string"", ""enum"": [""all"", ""active_view"", ""selection""], ""default"": ""all""},
    ""view_id"": {""type"": ""string"", ""description"": ""View id if scope='active_view' override; defaults to active view""},
    ""dry_run"": {""type"": ""boolean"", ""default"": false}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // --- Parse required type ids ---
            if (!TryReadIdString(request, "from_type_id", out var fromIdLong, out var fromErr))
                return CommandResult.Fail(fromErr);
            if (!TryReadIdString(request, "to_type_id", out var toIdLong, out var toErr))
                return CommandResult.Fail(toErr);

            var fromTypeId = RevitCompat.ToElementId(fromIdLong);
            var toTypeId = RevitCompat.ToElementId(toIdLong);

            var fromType = doc.GetElement(fromTypeId) as ElementType;
            if (fromType == null)
                return CommandResult.Fail("from_type_id " + fromIdLong.ToString(CultureInfo.InvariantCulture) + " is not an ElementType (FamilySymbol or system type).");
            var toType = doc.GetElement(toTypeId) as ElementType;
            if (toType == null)
                return CommandResult.Fail("to_type_id " + toIdLong.ToString(CultureInfo.InvariantCulture) + " is not an ElementType (FamilySymbol or system type).");

            // --- Verify same category ---
            var fromCat = fromType.Category;
            var toCat = toType.Category;
            if (fromCat == null || toCat == null)
                return CommandResult.Fail("Cannot resolve category for one or both types.");

            var fromCatId = RevitCompat.GetId(fromCat.Id);
            var toCatId = RevitCompat.GetId(toCat.Id);
            if (fromCatId != toCatId)
                return CommandResult.Fail(
                    "Categories do not match: from='" + (fromCat.Name ?? "?") + "' to='" + (toCat.Name ?? "?") + "'. " +
                    "Replacement requires same category.");

            // --- Parse scope ---
            var scope = (request.Value<string>("scope") ?? "all").ToLowerInvariant();
            if (scope != "all" && scope != "active_view" && scope != "selection")
                return CommandResult.Fail("scope must be one of: all, active_view, selection.");

            var dryRun = request.Value<bool?>("dry_run") ?? false;

            // --- Collect candidate instances scoped per branch ---
            List<Element> instances;
            string collectError;
            try
            {
                instances = CollectInstances(app, doc, uidoc, scope, request, fromCat, fromTypeId, out collectError);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to collect instances: " + ex.Message);
            }
            if (collectError != null)
                return CommandResult.Fail(collectError);

            var fromTypeName = SafeName(fromType);
            var toTypeName = SafeName(toType);
            var categoryName = fromCat.Name;
            var instanceCount = instances.Count;

            // --- Dry run: count only, no transaction ---
            if (dryRun)
            {
                return CommandResult.Ok(new
                {
                    replaced = false,
                    from_type_id = fromIdLong.ToString(CultureInfo.InvariantCulture),
                    from_type_name = fromTypeName,
                    to_type_id = toIdLong.ToString(CultureInfo.InvariantCulture),
                    to_type_name = toTypeName,
                    category = categoryName,
                    instance_count = instanceCount,
                    successfully_changed = 0,
                    errors = new object[0],
                    dry_run = true,
                    scope = scope
                });
            }

            if (instanceCount == 0)
            {
                return CommandResult.Ok(new
                {
                    replaced = true,
                    from_type_id = fromIdLong.ToString(CultureInfo.InvariantCulture),
                    from_type_name = fromTypeName,
                    to_type_id = toIdLong.ToString(CultureInfo.InvariantCulture),
                    to_type_name = toTypeName,
                    category = categoryName,
                    instance_count = 0,
                    successfully_changed = 0,
                    errors = new object[0],
                    dry_run = false,
                    scope = scope,
                    note = "no instances matched scope"
                });
            }

            // --- Apply replacement in a single transaction ---
            var errors = new List<object>();
            var successfullyChanged = 0;

            using (var tx = new Transaction(doc, "RvtMcp: replace type"))
            {
                try
                {
                    tx.Start();

                    // Activate target symbol if it's a FamilySymbol (no-op for system types).
                    var toSymbol = toType as FamilySymbol;
                    if (toSymbol != null && !toSymbol.IsActive)
                    {
                        try
                        {
                            toSymbol.Activate();
                            doc.Regenerate();
                        }
                        catch (Exception ex)
                        {
                            if (tx.HasStarted()) tx.RollBack();
                            return CommandResult.Fail("Failed to activate target FamilySymbol: " + ex.Message);
                        }
                    }

                    foreach (var inst in instances)
                    {
                        if (inst == null) continue;
                        long instIdLong = 0;
                        try { instIdLong = RevitCompat.GetId(inst.Id); } catch { }

                        try
                        {
                            // Pre-check validity for nicer error messages.
                            bool isValidType;
                            try
                            {
                                isValidType = inst.IsValidType(toTypeId);
                            }
                            catch (Exception ex)
                            {
                                errors.Add(new
                                {
                                    instance_id = instIdLong.ToString(CultureInfo.InvariantCulture),
                                    error = "IsValidType threw: " + ex.Message
                                });
                                continue;
                            }

                            if (!isValidType)
                            {
                                errors.Add(new
                                {
                                    instance_id = instIdLong.ToString(CultureInfo.InvariantCulture),
                                    error = "Target type is not valid for this instance (host-incompatible)."
                                });
                                continue;
                            }

                            inst.ChangeTypeId(toTypeId);
                            successfullyChanged++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new
                            {
                                instance_id = instIdLong.ToString(CultureInfo.InvariantCulture),
                                error = ex.Message
                            });
                        }
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit type replacement. Transaction status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Replacement transaction failed: " + ex.Message);
                }
            }

            return CommandResult.Ok(new
            {
                replaced = successfullyChanged > 0,
                from_type_id = fromIdLong.ToString(CultureInfo.InvariantCulture),
                from_type_name = fromTypeName,
                to_type_id = toIdLong.ToString(CultureInfo.InvariantCulture),
                to_type_name = toTypeName,
                category = categoryName,
                instance_count = instanceCount,
                successfully_changed = successfullyChanged,
                errors = errors.ToArray(),
                dry_run = false,
                scope = scope
            });
        }

        /// <summary>
        /// Collect instances of <paramref name="fromTypeId"/> in the requested scope.
        /// Returns an empty list on no match. Sets <paramref name="error"/> on configuration problems
        /// (e.g. invalid view_id, scope='active_view' but no active graphical view).
        /// </summary>
        private static List<Element> CollectInstances(
            UIApplication app,
            Document doc,
            UIDocument uidoc,
            string scope,
            JObject request,
            Category fromCat,
            ElementId fromTypeId,
            out string error)
        {
            error = null;
            var result = new List<Element>();
            var fromTypeIdLong = RevitCompat.GetId(fromTypeId);

            // Pre-resolve a category filter for performance.
            BuiltInCategory? bic = null;
            try
            {
                if (fromCat != null && Enum.IsDefined(typeof(BuiltInCategory), (int)RevitCompat.GetId(fromCat.Id)))
                {
                    // Category.Id maps directly to a BuiltInCategory int for built-in categories.
                    var rawId = (int)RevitCompat.GetId(fromCat.Id);
                    bic = (BuiltInCategory)rawId;
                }
            }
            catch { bic = null; }

            if (scope == "all")
            {
                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                if (bic.HasValue) collector = collector.OfCategory(bic.Value);
                foreach (var e in collector)
                {
                    if (e == null) continue;
                    try
                    {
                        if (RevitCompat.GetId(e.GetTypeId()) == fromTypeIdLong)
                            result.Add(e);
                    }
                    catch { }
                }
                return result;
            }

            if (scope == "active_view")
            {
                View view = null;
                var viewIdToken = request["view_id"];
                if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
                {
                    var raw = viewIdToken.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vidLong))
                        {
                            error = "view_id must be an integer-valued string.";
                            return result;
                        }
                        if (!RevitCompat.CanRepresentElementId(vidLong))
                        {
                            error = "view_id " + RevitCompat.ElementIdRangeError(vidLong);
                            return result;
                        }
                        view = doc.GetElement(RevitCompat.ToElementId(vidLong)) as View;
                        if (view == null)
                        {
                            error = "view_id " + vidLong.ToString(CultureInfo.InvariantCulture) + " is not a View.";
                            return result;
                        }
                    }
                }

                if (view == null)
                    view = doc.ActiveView;

                if (view == null)
                {
                    error = "scope='active_view' but no active view is available.";
                    return result;
                }

                FilteredElementCollector collector;
                try
                {
                    collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                }
                catch (Exception ex)
                {
                    error = "Cannot iterate view '" + (view.Name ?? "?") + "': " + ex.Message;
                    return result;
                }

                if (bic.HasValue) collector = collector.OfCategory(bic.Value);
                foreach (var e in collector)
                {
                    if (e == null) continue;
                    try
                    {
                        if (RevitCompat.GetId(e.GetTypeId()) == fromTypeIdLong)
                            result.Add(e);
                    }
                    catch { }
                }
                return result;
            }

            if (scope == "selection")
            {
                if (uidoc == null)
                {
                    error = "scope='selection' but no active UIDocument is available.";
                    return result;
                }

                ICollection<ElementId> selIds = null;
                try { selIds = uidoc.Selection.GetElementIds(); } catch { }
                if (selIds == null || selIds.Count == 0)
                    return result;

                foreach (var id in selIds)
                {
                    var e = doc.GetElement(id);
                    if (e == null) continue;
                    try
                    {
                        if (RevitCompat.GetId(e.GetTypeId()) == fromTypeIdLong)
                            result.Add(e);
                    }
                    catch { }
                }
                return result;
            }

            error = "Unknown scope '" + scope + "'.";
            return result;
        }

        /// <summary>
        /// Reads a required ElementId parameter that arrives as a string (per schema).
        /// Also tolerates an integer JSON token for convenience.
        /// </summary>
        private static bool TryReadIdString(JObject request, string propertyName, out long id, out string error)
        {
            id = 0;
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = propertyName + " is required.";
                return false;
            }

            string raw;
            if (token.Type == JTokenType.Integer)
            {
                id = token.Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    error = propertyName + " " + RevitCompat.ElementIdRangeError(id);
                    return false;
                }
                return true;
            }

            raw = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = propertyName + " is required.";
                return false;
            }

            if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                error = propertyName + " must be an integer-valued string.";
                return false;
            }

            if (!RevitCompat.CanRepresentElementId(id))
            {
                error = propertyName + " " + RevitCompat.ElementIdRangeError(id);
                return false;
            }

            return true;
        }

        private static string SafeName(Element element)
        {
            if (element == null) return null;
            try { return element.Name; } catch { return null; }
        }
    }
}
