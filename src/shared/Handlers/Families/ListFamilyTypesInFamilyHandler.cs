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
    /// <summary>
    /// Deep listing of FamilySymbols (types) for one Family, including per-type parameter
    /// values (with unit conversion). This is the per-family scoped counterpart to the
    /// cross-category <c>get_available_family_types</c>.
    /// </summary>
    public class ListFamilyTypesInFamilyHandler : IRevitCommand
    {
        public string Name => "list_family_types_in_family";

        public string Description =>
            "List all types (FamilySymbols) within a single Family, including each type's parameter values "
            + "(with feet->mm / sq_ft->sq_m conversion). More detailed/scoped than get_available_family_types. "
            + "A system family id from list_loaded_families (system:<typeId>) lists every type of that system family, such as each wall type.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""family_id"": {""type"": ""string"", ""description"": ""Family ElementId, or system:<typeId> from list_loaded_families for a system family. Either family_id or family_name required.""},
    ""family_name"": {""type"": ""string"", ""description"": ""Family name. Used if family_id not provided.""},
    ""include_parameter_values"": {""type"": ""boolean"", ""default"": true, ""description"": ""If true, include bounded type parameter values.""},
    ""include_built_in_only"": {""type"": ""boolean"", ""default"": false, ""description"": ""If true, only built-in parameters; else include shared/project parameters too.""},
    ""start_index"": {""type"": ""integer"", ""default"": 0, ""minimum"": 0},
    ""max_types"": {""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 500},
    ""parameter_names"": {""type"": ""array"", ""items"": {""type"": ""string""}, ""description"": ""Optional exact parameter-name allowlist.""}
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

            var familyIdStr = request.Value<string>("family_id");
            var familyName = request.Value<string>("family_name");
            var includeParameterValues = request["include_parameter_values"] != null
                ? request.Value<bool>("include_parameter_values")
                : true;
            var includeBuiltInOnly = request["include_built_in_only"] != null
                ? request.Value<bool>("include_built_in_only")
                : false;
            if (!ResponsePaging.TryParse(request, "start_index", "max_types", 100, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);
            var parameterNames = new HashSet<string>(
                request["parameter_names"]?.Values<string>() ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(familyIdStr) && string.IsNullOrWhiteSpace(familyName))
                return CommandResult.Fail("Either family_id or family_name is required.");

            // A system family (walls, floors, pipes...) has no Family element: list_loaded_families
            // publishes it as system:<id of one of its types>.
            if (!string.IsNullOrWhiteSpace(familyIdStr) && familyIdStr.Trim().StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase))
                return ListSystemFamilyTypes(doc, familyIdStr.Trim(), includeParameterValues, includeBuiltInOnly, parameterNames, paging);

            // Resolve family (id first, then name)
            Family family = null;
            if (!string.IsNullOrWhiteSpace(familyIdStr))
            {
                if (!long.TryParse(familyIdStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId))
                    return CommandResult.Fail("family_id must be a numeric element id (got '" + familyIdStr + "').");

                if (!RevitCompat.CanRepresentElementId(rawId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(rawId));

                family = doc.GetElement(RevitCompat.ToElementId(rawId)) as Family;
                if (family == null)
                    return CommandResult.Fail("No Family element found with id " + rawId.ToString(CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => string.Equals(SafeName(f), familyName, StringComparison.Ordinal))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail("No Family found with name '" + familyName + "'.");
                if (matches.Count > 1)
                    return CommandResult.Fail("Multiple Families found with name '" + familyName + "' ("
                        + matches.Count.ToString(CultureInfo.InvariantCulture)
                        + "). Disambiguate by passing family_id.");
                family = matches[0];
            }

            var resolvedFamilyId = RevitCompat.GetId(family.Id);
            var resolvedFamilyName = SafeName(family) ?? string.Empty;
            var categoryName = SafeCategoryName(family);
            var kind = ClassifyFamilyKind(family);
            var isEditable = SafeIsEditable(family);

            // Enumerate symbols
            var symbolIds = GetFamilySymbolIdsCompat(family)
                .Where(id => id != null)
                .OrderBy(RevitCompat.GetId)
                .ToArray();
            var symbolPage = ResponsePaging.Slice(symbolIds, paging.StartIndex, paging.MaxResults);
            var types = new List<object>();

            foreach (var sid in symbolPage.Items)
            {
                if (sid == null) continue;
                var sym = doc.GetElement(sid) as FamilySymbol;
                if (sym == null) continue;

                object parametersDto = null;
                if (includeParameterValues)
                {
                    parametersDto = BuildParametersDto(doc, sym, includeBuiltInOnly, parameterNames);
                }

                types.Add(new
                {
                    id = RevitCompat.GetId(sym.Id).ToString(CultureInfo.InvariantCulture),
                    name = SafeName(sym) ?? string.Empty,
                    is_active = SafeIsActive(sym),
                    parameters = parametersDto
                });
            }

            return CommandResult.Ok(new
            {
                family_id = resolvedFamilyId.ToString(CultureInfo.InvariantCulture),
                family_name = resolvedFamilyName,
                category = categoryName,
                kind = kind,
                is_editable = isEditable,
                total_type_count = symbolPage.TotalCount,
                start_index = symbolPage.StartIndex,
                returned_count = symbolPage.ReturnedCount,
                truncated = symbolPage.Truncated,
                next_index = symbolPage.NextIndex,
                types = types.ToArray()
            });
        }

        private const string SystemPrefix = "system:";

        /// <summary>
        /// Every type of one system family: the types of the same class, family name and category
        /// as the given type, the grouping list_loaded_families uses.
        /// </summary>
        private static CommandResult ListSystemFamilyTypes(Document doc, string familyId, bool includeParameterValues,
            bool includeBuiltInOnly, HashSet<string> parameterNames, ResponsePaging.Options paging)
        {
            var idText = familyId.Substring(SystemPrefix.Length).Trim();
            if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId))
                return CommandResult.Fail("family_id '" + familyId + "' must be system:<typeId>, as revit_list_loaded_families returns it.");
            if (!RevitCompat.CanRepresentElementId(rawId))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(rawId));

            var seed = doc.GetElement(RevitCompat.ToElementId(rawId)) as ElementType;
            if (seed == null || seed is FamilySymbol)
                return CommandResult.Fail("family_id '" + familyId + "' is not a system family type. Use the system:<typeId> id revit_list_loaded_families returns, or a numeric Family id for a loadable family.");

            var familyName = SafeFamilyName(seed);
            var categoryId = seed.Category == null ? (long?)null : RevitCompat.GetId(seed.Category.Id);
            var typesInFamily = new FilteredElementCollector(doc)
                .OfClass(seed.GetType())
                .Cast<ElementType>()
                .Where(t => string.Equals(SafeFamilyName(t), familyName, StringComparison.Ordinal)
                    && (t.Category == null ? (long?)null : RevitCompat.GetId(t.Category.Id)) == categoryId)
                .OrderBy(t => RevitCompat.GetId(t.Id))
                .ToArray();
            var page = ResponsePaging.Slice(typesInFamily, paging.StartIndex, paging.MaxResults);

            var types = page.Items.Select(t => new
            {
                id = RevitCompat.GetId(t.Id).ToString(CultureInfo.InvariantCulture),
                name = SafeName(t) ?? string.Empty,
                parameters = includeParameterValues ? BuildParametersDto(doc, t, includeBuiltInOnly, parameterNames) : null
            }).ToArray();

            return CommandResult.Ok(new
            {
                family_id = familyId,
                family_name = familyName,
                category = seed.Category?.Name,
                kind = "system",
                is_editable = false,
                total_type_count = page.TotalCount,
                start_index = page.StartIndex,
                returned_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_index = page.NextIndex,
                types
            });
        }

        private static string SafeFamilyName(ElementType type)
        {
            try { return type.FamilyName ?? "<unknown>"; }
            catch { return "<unknown>"; }
        }

        // --- parameter extraction ---------------------------------------------------

        private static JObject BuildParametersDto(Document doc, ElementType sym, bool includeBuiltInOnly, HashSet<string> parameterNames)
        {
            var paramsObj = new JObject();
            ParameterSet paramSet;
            try { paramSet = sym.Parameters; }
            catch { return paramsObj; }
            if (paramSet == null) return paramsObj;

            foreach (Parameter p in paramSet)
            {
                if (p == null) continue;

                bool isBuiltIn = IsBuiltInParameter(p);
                if (includeBuiltInOnly && !isBuiltIn) continue;

                string name;
                try { name = p.Definition?.Name; }
                catch { name = null; }
                if (string.IsNullOrEmpty(name)) continue;
                if (parameterNames != null && parameterNames.Count > 0 && !parameterNames.Contains(name)) continue;

                // Avoid clobbering on duplicate names (e.g. shared params with same display name);
                // first one wins.
                if (paramsObj[name] != null) continue;

                var paramDto = new JObject();
                StorageType storage;
                try { storage = p.StorageType; }
                catch { storage = StorageType.None; }
                paramDto["storage_type"] = storage.ToString();

                JToken value;
                string unit;
                ExtractValueAndUnit(doc, p, storage, out value, out unit);
                paramDto["value"] = value ?? JValue.CreateNull();
                if (!string.IsNullOrEmpty(unit))
                    paramDto["unit"] = unit;

                paramsObj[name] = paramDto;
            }

            return paramsObj;
        }

        private static void ExtractValueAndUnit(
            Document doc,
            Parameter parameter,
            StorageType storage,
            out JToken value,
            out string unit)
        {
            value = JValue.CreateNull();
            unit = null;

            try
            {
                switch (storage)
                {
                    case StorageType.String:
                        var s = parameter.AsString();
                        if (s != null) value = new JValue(s);
                        break;

                    case StorageType.Integer:
                        value = new JValue(parameter.AsInteger());
                        break;

                    case StorageType.Double:
                        var raw = parameter.AsDouble();
                        var spec = TryGetSpecTypeId(parameter);
                        value = new JValue(ConvertFromInternalUnits(raw, spec));
                        unit = UnitLabel(spec);
                        break;

                    case StorageType.ElementId:
                        var eid = parameter.AsElementId();
                        if (eid == null)
                        {
                            value = JValue.CreateNull();
                        }
                        else
                        {
                            // Prefer the referenced element's name for human readability;
                            // fall back to the raw id.
                            string displayName = null;
                            try
                            {
                                var refEl = doc.GetElement(eid);
                                if (refEl != null) displayName = refEl.Name;
                            }
                            catch { }

                            if (!string.IsNullOrEmpty(displayName))
                                value = new JValue(displayName);
                            else
                                value = new JValue(RevitCompat.GetId(eid));
                        }
                        break;

                    default:
                        // StorageType.None — try AsValueString as a final fallback.
                        try
                        {
                            var vs = parameter.AsValueString();
                            if (!string.IsNullOrEmpty(vs)) value = new JValue(vs);
                        }
                        catch { }
                        break;
                }
            }
            catch
            {
                // ignored — leave value as null
            }
        }

        /// <summary>
        /// Cross-version SpecTypeId resolution.
        /// R22+ supports Parameter.Definition.GetDataType() returning a ForgeTypeId spec.
        /// Older paths used Definition.ParameterType / UnitType; we probe via reflection if
        /// GetDataType is missing.
        /// </summary>
        private static ForgeTypeId TryGetSpecTypeId(Parameter parameter)
        {
            try
            {
                var def = parameter.Definition;
                if (def == null) return null;

                // Direct path (available in all supported R22..R27 SDKs)
                try
                {
                    var spec = def.GetDataType();
                    if (spec != null) return spec;
                }
                catch { }

                // Reflection fallback for older / unusual builds
                try
                {
                    var mi = def.GetType().GetMethod(
                        "GetDataType",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (mi != null)
                    {
                        var result = mi.Invoke(def, null) as ForgeTypeId;
                        if (result != null) return result;
                    }
                }
                catch { }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Convert Revit internal units (feet for length, sq-ft for area, cu-ft for volume,
        /// radians for angle) into user-friendly metric (mm / m^2 / m^3 / degrees).
        /// Mirrors the conversion table used by GetScheduleDefinitionHandler so values stay
        /// consistent across the toolset.
        /// </summary>
        private static double ConvertFromInternalUnits(double value, ForgeTypeId spec)
        {
            if (spec == null) return value;
            try
            {
                if (spec == SpecTypeId.Length) return value * 304.8;          // feet -> mm
                if (spec == SpecTypeId.Area) return value * 0.09290304;       // sq-ft -> m^2
                if (spec == SpecTypeId.Volume) return value * 0.028316846592; // cu-ft -> m^3
                if (spec == SpecTypeId.Angle) return value * (180.0 / Math.PI); // radians -> degrees
            }
            catch { }
            return value;
        }

        private static string UnitLabel(ForgeTypeId spec)
        {
            if (spec == null) return null;
            try
            {
                if (spec == SpecTypeId.Length) return "mm";
                if (spec == SpecTypeId.Area) return "m2";
                if (spec == SpecTypeId.Volume) return "m3";
                if (spec == SpecTypeId.Angle) return "degrees";
            }
            catch { }
            return null;
        }

        private static bool IsBuiltInParameter(Parameter parameter)
        {
            try
            {
                var def = parameter.Definition;
                var internalDef = def as InternalDefinition;
                if (internalDef == null) return false;
                return internalDef.BuiltInParameter != BuiltInParameter.INVALID;
            }
            catch
            {
                return false;
            }
        }

        // --- family classification --------------------------------------------------

        /// <summary>
        /// Heuristic kind classification:
        ///  - "inplace"   : Family.IsInPlace == true
        ///  - "system"    : Family.IsEditable == false (system families surfaced as Family)
        ///  - "loadable"  : default (.rfa-loaded family)
        /// </summary>
        private static string ClassifyFamilyKind(Family family)
        {
            try
            {
                if (family.IsInPlace) return "inplace";
            }
            catch { }

            try
            {
                if (!family.IsEditable) return "system";
            }
            catch { }

            return "loadable";
        }

        private static bool SafeIsEditable(Family family)
        {
            try { return family.IsEditable; }
            catch { return false; }
        }

        private static bool SafeIsActive(FamilySymbol sym)
        {
            try { return sym.IsActive; }
            catch { return false; }
        }

        private static string SafeName(Element element)
        {
            if (element == null) return null;
            try { return element.Name; }
            catch { return null; }
        }

        private static string SafeCategoryName(Family family)
        {
            if (family == null) return null;
            try
            {
                var cat = family.FamilyCategory;
                return cat?.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Family.GetFamilySymbolIds() is available R22+, wrapped with reflection fallback for safety
        /// across builds where the binding might be intercepted.
        /// </summary>
        private static ICollection<ElementId> GetFamilySymbolIdsCompat(Family family)
        {
            if (family == null) return new List<ElementId>();

            try
            {
                var ids = family.GetFamilySymbolIds();
                if (ids != null) return ids;
            }
            catch (MissingMethodException) { }
            catch (Exception) { }

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
            catch { }

            return new List<ElementId>();
        }
    }
}
