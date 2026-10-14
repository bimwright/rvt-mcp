using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class UnloadFamilyHandler : IRevitCommand
    {
        public string Name => "unload_family";

        public string Description =>
            "Remove (purge) a loadable family from the document. Family must be identified by id or name. "
            + "Optional cascade delete to also remove placed instances first.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""family_id"":{""type"":""string"",""description"":""Element id of the Family. Either family_id or family_name required.""},""family_name"":{""type"":""string"",""description"":""Name of the Family. Used if family_id not provided.""},""cascade_delete_instances"":{""type"":""boolean"",""default"":false,""description"":""If true, delete all placed instances first (count returned). If false and instances exist, return error without deleting.""},""allow_inplace"":{""type"":""boolean"",""default"":false,""description"":""If true, permit deleting in-place family content. Defaults to false because this is destructive.""},""dry_run"":{""type"":""boolean"",""default"":false,""description"":""If true, report what would happen without deleting.""}}}";

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

            var familyIdStr = request.Value<string>("family_id");
            var familyName = request.Value<string>("family_name");
            var cascadeDeleteInstances = request.Value<bool?>("cascade_delete_instances") ?? false;
            var allowInPlace = request.Value<bool?>("allow_inplace") ?? false;
            var dryRun = request.Value<bool?>("dry_run") ?? false;

            if (string.IsNullOrWhiteSpace(familyIdStr) && string.IsNullOrWhiteSpace(familyName))
                return CommandResult.Fail("Either family_id or family_name is required.");

            // Locate the Family
            Family family = null;
            if (!string.IsNullOrWhiteSpace(familyIdStr))
            {
                if (!long.TryParse(familyIdStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId))
                    return CommandResult.Fail("family_id must be a numeric element id (got '" + familyIdStr + "').");

                if (!RevitCompat.CanRepresentElementId(rawId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(rawId));

                var elId = RevitCompat.ToElementId(rawId);
                family = doc.GetElement(elId) as Family;
                if (family == null)
                    return BuildErrorDto(null, familyName, 0, 0, dryRun,
                        "No Family element found with id " + rawId.ToString(CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => string.Equals(SafeName(f), familyName, StringComparison.Ordinal))
                    .ToList();

                if (matches.Count == 0)
                    return BuildErrorDto(null, familyName, 0, 0, dryRun,
                        "No Family found with name '" + familyName + "'.");

                if (matches.Count > 1)
                    return BuildErrorDto(null, familyName, 0, 0, dryRun,
                        "Multiple Families found with name '" + familyName + "' (" + matches.Count
                        + "). Disambiguate by passing family_id.");

                family = matches[0];
            }

            var resolvedFamilyId = RevitCompat.GetId(family.Id);
            var resolvedFamilyName = SafeName(family) ?? string.Empty;

            // System families (Wall/Floor types) cannot be deleted.
            // Family.IsInPlace + Family.FamilyCategory exist; loadable families are not system.
            // Heuristic: if the family came from a Family element, it is loadable or in-place by definition.
            // But Revit may surface "system" families via the Family class only in unusual cases — guard explicitly.
            if (IsSystemFamily(family))
            {
                return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, 0, 0, dryRun,
                    "Family '" + resolvedFamilyName + "' is a system family (e.g. Wall/Floor/Pipe type) and cannot be unloaded.");
            }

            var isInPlace = SafeIsInPlace(family);
            if (isInPlace && !allowInPlace)
            {
                return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, 0, 0, dryRun,
                    "Family is in-place. Pass allow_inplace=true to permit deleting in-place family content.");
            }

            // Gather symbol ids (FamilySymbol == family types)
            var symbolIds = GetFamilySymbolIdsCompat(family);
            var symbolIdSet = new HashSet<long>(symbolIds.Select(RevitCompat.GetId));

            // Count placed instances of any symbol belonging to this family.
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Where(fi =>
                {
                    var symId = fi.GetTypeId();
                    if (symId == null) return false;
                    return symbolIdSet.Contains(RevitCompat.GetId(symId));
                })
                .ToList();

            var instanceCount = instances.Count;
            var typesRemoved = symbolIdSet.Count;
            var warning = isInPlace ? "Family is in-place; instances are tied to this model only." : null;

            // Refuse if instances exist and no cascade.
            if (instanceCount > 0 && !cascadeDeleteInstances)
            {
                return CommandResult.Ok(new
                {
                    unloaded = false,
                    family_id = resolvedFamilyId.ToString(CultureInfo.InvariantCulture),
                    family_name = resolvedFamilyName,
                    instances_deleted = 0,
                    types_removed = 0,
                    dry_run = dryRun,
                    warning,
                    error = "Family has " + instanceCount.ToString(CultureInfo.InvariantCulture)
                            + " placed instances; pass cascade_delete_instances=true to remove"
                });
            }

            // Dry run: project the outcome without opening a transaction.
            if (dryRun)
            {
                return CommandResult.Ok(new
                {
                    unloaded = true,
                    family_id = resolvedFamilyId.ToString(CultureInfo.InvariantCulture),
                    family_name = resolvedFamilyName,
                    instances_deleted = instanceCount,
                    types_removed = typesRemoved,
                    dry_run = true,
                    warning,
                    error = (string)null
                });
            }

            // Execute deletion inside one transaction.
            int actuallyDeletedInstances = 0;
            int actuallyRemovedTypes = typesRemoved;
            try
            {
                using (var tx = new Transaction(doc, "RvtMcp: unload family"))
                {
                    tx.Start();

                    if (instanceCount > 0)
                    {
                        var instanceIds = instances.Select(i => i.Id).ToList();
                        try
                        {
                            var deletedIds = doc.Delete(instanceIds);
                            actuallyDeletedInstances = deletedIds != null ? deletedIds.Count : instanceIds.Count;
                        }
                        catch (Exception ex)
                        {
                            tx.RollBack();
                            return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, 0, 0, false,
                                "Failed to delete placed instances: " + ex.Message);
                        }
                    }

                    try
                    {
                        doc.Delete(family.Id);
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, actuallyDeletedInstances, 0, false,
                            "Failed to delete family: " + ex.Message);
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, 0, 0, false,
                            "Revit did not commit unload transaction. Status: " + status);
                    }
                }
            }
            catch (Exception ex)
            {
                return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, 0, 0, false,
                    "Unload failed: " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                unloaded = true,
                family_id = resolvedFamilyId.ToString(CultureInfo.InvariantCulture),
                family_name = resolvedFamilyName,
                instances_deleted = actuallyDeletedInstances,
                types_removed = actuallyRemovedTypes,
                dry_run = false,
                warning,
                error = (string)null
            });
        }

        private static CommandResult BuildErrorDto(
            long? familyId,
            string familyName,
            int instancesDeleted,
            int typesRemoved,
            bool dryRun,
            string error)
        {
            return CommandResult.Ok(new
            {
                unloaded = false,
                family_id = familyId.HasValue ? familyId.Value.ToString(CultureInfo.InvariantCulture) : null,
                family_name = familyName ?? string.Empty,
                instances_deleted = instancesDeleted,
                types_removed = typesRemoved,
                dry_run = dryRun,
                warning = (string)null,
                error
            });
        }

        private static string SafeName(Element element)
        {
            if (element == null) return null;
            try { return element.Name; }
            catch { return null; }
        }

        private static bool SafeIsInPlace(Family family)
        {
            if (family == null) return false;
            try { return family.IsInPlace; }
            catch { return false; }
        }

        /// <summary>
        /// True when the Family element actually represents a system family (Wall/Floor/Pipe type).
        /// In practice doc.GetElement returning a Family for a system family is unusual, but we guard
        /// via IsEditable + FamilyCategory checks to be defensive.
        /// </summary>
        private static bool IsSystemFamily(Family family)
        {
            if (family == null) return false;

            // Loadable families are editable; system families are not.
            // In-place families are also editable, but we explicitly do NOT treat them as system.
            try
            {
                if (family.IsInPlace) return false;
            }
            catch
            {
                // ignored — fall through
            }

            try
            {
                // Family.IsEditable is true for loadable families. False generally implies system.
                if (!family.IsEditable) return true;
            }
            catch
            {
                // older Revit may throw — assume not system
            }

            return false;
        }

        /// <summary>
        /// Family.GetFamilySymbolIds() exists from Revit 2015+, so it is available on R22.
        /// Wrapped in reflection fallback just in case the method is removed or renamed in a future build.
        /// </summary>
        private static ICollection<ElementId> GetFamilySymbolIdsCompat(Family family)
        {
            if (family == null) return new List<ElementId>();

            try
            {
                var ids = family.GetFamilySymbolIds();
                if (ids != null) return ids;
            }
            catch (MissingMethodException)
            {
                // fall through to reflection fallback
            }
            catch (Exception)
            {
                // fall through to reflection fallback
            }

            try
            {
                var mi = typeof(Family).GetMethod(
                    "GetFamilySymbolIds",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (mi != null)
                {
                    var raw = mi.Invoke(family, null) as ICollection<ElementId>;
                    if (raw != null) return raw;
                }
            }
            catch
            {
                // last-resort fall through
            }

            return new List<ElementId>();
        }
    }
}
