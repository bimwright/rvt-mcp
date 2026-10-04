using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// create_view_filter — Creates a parameter-based view filter (ParameterFilterElement)
    /// targeting one or more categories, with optional filter rules.
    ///
    /// CROSS-VERSION NOTE: ParameterFilterRuleFactory rule-factory method signatures shifted
    /// across Revit 2022-2027 (a case-sensitivity bool parameter was added then removed; some
    /// string overloads were retired). Rule construction is therefore done entirely through
    /// reflection: for each requested rule we probe every matching factory overload and invoke
    /// the first that accepts our argument shape. Any rule that cannot be built is skipped and
    /// reported in the DTO's skipped_rules note — a filter with zero rules is still valid.
    /// </summary>
    public class CreateViewFilterHandler : IRevitCommand
    {
        public string Name => "create_view_filter";

        public string Description =>
            "Create a parameter-based view filter (ParameterFilterElement) targeting one or more " +
            "categories. Optionally supply filter rules; each rule is {parameter_name, evaluator, value} " +
            "with evaluator one of equals, not_equals, greater, less, contains, begins_with, ends_with. " +
            "If rules are omitted the filter matches all elements of the given categories.";

        public string ParametersSchema =>
            @"{""type"":""object"",""required"":[""name"",""categories""],""properties"":{" +
            @"""name"":{""type"":""string"",""description"":""Filter name (must be unique).""}," +
            @"""categories"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Category names to target, e.g. ['Walls','Doors'].""}," +
            @"""rules"":{""type"":""array"",""description"":""Optional filter rules. Each: {parameter_name, evaluator, value}. evaluator one of: equals,not_equals,greater,less,contains,begins_with,ends_with. If omitted, the filter matches all elements of the categories.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var filterName = request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(filterName))
                return CommandResult.Fail("name is required.");

            var categoriesToken = request["categories"] as JArray;
            if (categoriesToken == null || categoriesToken.Count == 0)
                return CommandResult.Fail("categories is required and must be a non-empty array.");

            // ---- Resolve category names → ElementId -----------------------------------------
            var categoryIds = new List<ElementId>();
            var resolvedCategoryNames = new List<string>();
            var unknownCategories = new List<string>();

            for (int i = 0; i < categoriesToken.Count; i++)
            {
                var rawName = categoriesToken[i]?.ToString();
                if (string.IsNullOrWhiteSpace(rawName))
                    continue;

                var category = ResolveCategory(doc, rawName.Trim());
                if (category == null)
                {
                    unknownCategories.Add(rawName.Trim());
                    continue;
                }

                if (!categoryIds.Any(id => id == category.Id))
                {
                    categoryIds.Add(category.Id);
                    resolvedCategoryNames.Add(category.Name);
                }
            }

            if (categoryIds.Count == 0)
            {
                return CommandResult.Fail(
                    "None of the requested categories could be resolved: " +
                    string.Join(", ", unknownCategories) +
                    ". Suggestion: use exact Revit category names such as 'Walls', 'Doors', 'Rooms'.");
            }

            var rulesToken = request["rules"] as JArray;

            using (var tx = new Transaction(doc, "RvtMcp: create view filter"))
            {
                tx.Start();
                try
                {
                    // ---- Build rules (best-effort, defensive) -------------------------------
                    var filterRules = new List<FilterRule>();
                    var skippedRules = new List<string>();
                    var ruleResults = new List<object>();
                    var rulesRequested = rulesToken != null && rulesToken.Count > 0;

                    if (rulesRequested)
                    {
                        // A sample element per category helps resolve shared/project params to an
                        // ElementId when the parameter is not a BuiltInParameter.
                        var sampleElements = CollectSampleElements(doc, categoryIds);

                        for (int i = 0; i < rulesToken.Count; i++)
                        {
                            var ruleSpec = rulesToken[i] as JObject;
                            if (ruleSpec == null)
                            {
                                var error = $"rules[{i}]: not an object.";
                                skippedRules.Add(error);
                                ruleResults.Add(new
                                {
                                    index = i,
                                    parameter_name = (string)null,
                                    evaluator = (string)null,
                                    applied = false,
                                    unit_note = (string)null,
                                    error
                                });
                                continue;
                            }

                            var paramName = ruleSpec.Value<string>("parameter_name");
                            var evaluator = ruleSpec.Value<string>("evaluator");
                            var valueToken = ruleSpec["value"];

                            if (string.IsNullOrWhiteSpace(paramName))
                            {
                                var error = $"rules[{i}]: parameter_name is missing.";
                                skippedRules.Add(error);
                                ruleResults.Add(new
                                {
                                    index = i,
                                    parameter_name = paramName,
                                    evaluator,
                                    applied = false,
                                    unit_note = (string)null,
                                    error
                                });
                                continue;
                            }
                            if (string.IsNullOrWhiteSpace(evaluator))
                            {
                                var error = $"rules[{i}] ('{paramName}'): evaluator is missing.";
                                skippedRules.Add(error);
                                ruleResults.Add(new
                                {
                                    index = i,
                                    parameter_name = paramName,
                                    evaluator,
                                    applied = false,
                                    unit_note = (string)null,
                                    error
                                });
                                continue;
                            }

                            var trimmedParamName = paramName.Trim();
                            var trimmedEvaluator = evaluator.Trim();
                            var paramId = ResolveParameterId(doc, trimmedParamName, sampleElements);
                            if (paramId == null)
                            {
                                var error =
                                    $"rules[{i}]: parameter '{paramName}' not found among the target " +
                                    "categories' built-in or shared/project parameters.";
                                skippedRules.Add(error);
                                ruleResults.Add(new
                                {
                                    index = i,
                                    parameter_name = trimmedParamName,
                                    evaluator = trimmedEvaluator,
                                    applied = false,
                                    unit_note = (string)null,
                                    error
                                });
                                continue;
                            }

                            var preparedValueToken = PrepareRuleValue(doc, paramId, trimmedParamName,
                                sampleElements, valueToken, out var unitNote);

                            try
                            {
                                var rule = BuildFilterRule(paramId, trimmedEvaluator, preparedValueToken);
                                if (rule != null)
                                {
                                    filterRules.Add(rule);
                                    ruleResults.Add(new
                                    {
                                        index = i,
                                        parameter_name = trimmedParamName,
                                        evaluator = trimmedEvaluator,
                                        applied = true,
                                        unit_note = unitNote,
                                        error = (string)null
                                    });
                                }
                                else
                                {
                                    var error =
                                        $"rules[{i}] ('{paramName}', '{evaluator}'): no compatible " +
                                        "ParameterFilterRuleFactory overload in this Revit version.";
                                    skippedRules.Add(error);
                                    ruleResults.Add(new
                                    {
                                        index = i,
                                        parameter_name = trimmedParamName,
                                        evaluator = trimmedEvaluator,
                                        applied = false,
                                        unit_note = unitNote,
                                        error
                                    });
                                }
                            }
                            catch (Exception ruleEx)
                            {
                                var error = $"rules[{i}] ('{paramName}', '{evaluator}'): {ruleEx.Message}";
                                skippedRules.Add(error);
                                ruleResults.Add(new
                                {
                                    index = i,
                                    parameter_name = trimmedParamName,
                                    evaluator = trimmedEvaluator,
                                    applied = false,
                                    unit_note = unitNote,
                                    error
                                });
                            }
                        }
                    }

                    if (rulesRequested && filterRules.Count == 0)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return RuleError(filterName, categoryIds.Count, resolvedCategoryNames,
                            unknownCategories, skippedRules, ruleResults, "no requested rules could be built.");
                    }

                    // ---- Create the ParameterFilterElement ----------------------------------
                    ParameterFilterElement filterElement;
                    var appliedRuleCount = 0;
                    try
                    {
                        if (filterRules.Count == 0)
                        {
                            filterElement = ParameterFilterElement.Create(doc, filterName, categoryIds);
                        }
                        else
                        {
                            var elementFilter = BuildElementFilter(filterRules);
                            filterElement = CreateWithRules(doc, filterName, categoryIds, elementFilter,
                                filterRules, out appliedRuleCount);
                        }
                    }
                    catch (Exception createEx)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        if (rulesRequested)
                        {
                            return RuleError(filterName, categoryIds.Count, resolvedCategoryNames,
                                unknownCategories, skippedRules, ruleResults, createEx.Message);
                        }

                        return CommandResult.Fail(
                            $"Failed to create view filter '{filterName}': {createEx.Message}. " +
                            "Suggestion: ensure the filter name is unique (Revit disallows duplicate filter names).");
                    }

                    if (filterElement == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        if (rulesRequested)
                        {
                            return RuleError(filterName, categoryIds.Count, resolvedCategoryNames,
                                unknownCategories, skippedRules, ruleResults, "the created filter was null.");
                        }

                        return CommandResult.Fail($"Failed to create view filter '{filterName}': null result.");
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        created = true,
                        filter_id = RevitCompat.GetId(filterElement.Id),
                        name = filterElement.Name,
                        category_count = categoryIds.Count,
                        rule_count = appliedRuleCount,
                        categories = resolvedCategoryNames.ToArray(),
                        unknown_categories = unknownCategories.Count > 0 ? unknownCategories.ToArray() : null,
                        skipped_rules = skippedRules.Count > 0 ? skippedRules.ToArray() : null,
                        rules = ruleResults.Count > 0 ? ruleResults.ToArray() : null,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create view filter: " + ex.Message);
                }
            }
        }

        // ---- Category resolution ------------------------------------------------------------

        private static Category ResolveCategory(Document doc, string name)
        {
            // First pass: the document's category collection (covers subcategories too).
            try
            {
                var categories = doc.Settings?.Categories;
                if (categories != null)
                {
                    foreach (Category c in categories)
                    {
                        if (c != null && c.Name != null &&
                            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            return c;
                    }
                }
            }
            catch { }

            // Second pass: BuiltInCategory enum (catches categories not surfaced in Settings).
            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var c = Category.GetCategory(doc, bic);
                    if (c != null && c.Name != null &&
                        c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                catch { }
            }

            return null;
        }

        // ---- Sample-element collection (for shared/project parameter id resolution) ----------

        private static List<Element> CollectSampleElements(Document doc, IList<ElementId> categoryIds)
        {
            var samples = new List<Element>();
            foreach (var catId in categoryIds)
            {
                try
                {
                    var bic = (BuiltInCategory)(int)RevitCompat.GetId(catId);
                    var element = new FilteredElementCollector(doc)
                        .OfCategoryId(catId)
                        .WhereElementIsNotElementType()
                        .FirstElement();
                    if (element != null)
                        samples.Add(element);

                    var type = new FilteredElementCollector(doc)
                        .OfCategoryId(catId)
                        .WhereElementIsElementType()
                        .FirstElement();
                    if (type != null)
                        samples.Add(type);
                }
                catch { }
            }
            return samples;
        }

        // ---- Parameter resolution -----------------------------------------------------------

        private static ElementId ResolveParameterId(Document doc, string paramName, List<Element> sampleElements)
        {
            // 1. BuiltInParameter whose label matches the requested name.
            foreach (BuiltInParameter bip in Enum.GetValues(typeof(BuiltInParameter)))
            {
                string label = null;
                try { label = LabelUtils.GetLabelFor(bip); }
                catch { }
                if (!string.IsNullOrEmpty(label) &&
                    label.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                {
                    try { return RevitCompat.ToElementId((int)bip); }
                    catch { }
                }
            }

            // 2. Probe sample elements for a parameter (built-in / shared / project) by name.
            foreach (var element in sampleElements)
            {
                if (element == null) continue;
                try
                {
                    foreach (Parameter p in element.Parameters)
                    {
                        if (p?.Definition == null) continue;
                        if (!p.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var idDef = p.Definition as InternalDefinition;
                        if (idDef != null && idDef.Id != ElementId.InvalidElementId)
                            return idDef.Id;
                    }
                }
                catch { }

                // LookupParameter also resolves type parameters not in the instance set.
                try
                {
                    var lookedUp = element.LookupParameter(paramName);
                    var idDef = lookedUp?.Definition as InternalDefinition;
                    if (idDef != null && idDef.Id != ElementId.InvalidElementId)
                        return idDef.Id;
                }
                catch { }
            }

            // 3. Project parameter bindings (shared/project parameters bound document-wide).
            try
            {
                var iterator = doc.ParameterBindings.ForwardIterator();
                iterator.Reset();
                while (iterator.MoveNext())
                {
                    var definition = iterator.Key as InternalDefinition;
                    if (definition != null &&
                        definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase) &&
                        definition.Id != ElementId.InvalidElementId)
                        return definition.Id;
                }
            }
            catch { }

            return null;
        }

        // ---- Unit-aware rule value handling -------------------------------------------------

        private static JToken PrepareRuleValue(Document doc, ElementId paramId, string paramName,
            List<Element> sampleElements, JToken valueToken, out string unitNote)
        {
            unitNote = null;

            if (!TryReadNumericRuleValue(valueToken, out var numericValue))
                return valueToken;

            if (!TryResolveParameterDataType(doc, paramId, paramName, sampleElements, out var dataType))
            {
                unitNote = "Value was treated as Revit internal units because the parameter unit spec could not be determined.";
                return valueToken;
            }

            if (TryConvertToInternalUnits(dataType, numericValue, out var internalValue))
                return new JValue(internalValue);

            return valueToken;
        }

        private static bool TryReadNumericRuleValue(JToken valueToken, out double value)
        {
            value = 0;
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return false;

            try
            {
                if (valueToken.Type == JTokenType.Integer || valueToken.Type == JTokenType.Float)
                {
                    value = valueToken.Value<double>();
                    return true;
                }

                if (valueToken.Type == JTokenType.String)
                {
                    return double.TryParse(valueToken.Value<string>(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out value);
                }
            }
            catch { }

            return false;
        }

        private static bool TryConvertToInternalUnits(ForgeTypeId dataType, double value, out double internalValue)
        {
            internalValue = value;
            if (dataType == null)
                return false;

            if (dataType.Equals(SpecTypeId.Length))
            {
                internalValue = value / 304.8;
                return true;
            }
            if (dataType.Equals(SpecTypeId.Area))
            {
                internalValue = value / 0.09290304;
                return true;
            }
            if (dataType.Equals(SpecTypeId.Volume))
            {
                internalValue = value / 0.02831685;
                return true;
            }
            if (dataType.Equals(SpecTypeId.Angle))
            {
                internalValue = value * Math.PI / 180.0;
                return true;
            }

            return false;
        }

        private static bool TryResolveParameterDataType(Document doc, ElementId paramId, string paramName,
            List<Element> sampleElements, out ForgeTypeId dataType)
        {
            dataType = null;

            try
            {
                var parameterElement = doc.GetElement(paramId) as ParameterElement;
                if (TryGetDefinitionDataType(parameterElement?.GetDefinition(), out dataType))
                    return true;
            }
            catch { }

            foreach (var element in sampleElements)
            {
                if (element == null) continue;

                try
                {
                    foreach (Parameter parameter in element.Parameters)
                    {
                        if (parameter?.Definition == null) continue;
                        if (!DefinitionMatchesParameter(parameter.Definition, paramId, paramName))
                            continue;
                        if (TryGetDefinitionDataType(parameter.Definition, out dataType))
                            return true;
                    }
                }
                catch { }

                try
                {
                    var lookedUp = element.LookupParameter(paramName);
                    if (lookedUp?.Definition != null &&
                        DefinitionMatchesParameter(lookedUp.Definition, paramId, paramName) &&
                        TryGetDefinitionDataType(lookedUp.Definition, out dataType))
                        return true;
                }
                catch { }
            }

            return false;
        }

        private static bool DefinitionMatchesParameter(Definition definition, ElementId paramId, string paramName)
        {
            if (definition == null)
                return false;

            try
            {
                var idDef = definition as InternalDefinition;
                if (idDef != null && idDef.Id != ElementId.InvalidElementId && idDef.Id == paramId)
                    return true;
            }
            catch { }

            return !string.IsNullOrEmpty(definition.Name) &&
                definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetDefinitionDataType(Definition definition, out ForgeTypeId dataType)
        {
            dataType = null;
            try
            {
                if (definition == null)
                    return false;

                dataType = definition.GetDataType();
                return dataType != null;
            }
            catch
            {
                dataType = null;
                return false;
            }
        }

        private static CommandResult RuleError(string filterName, int categoryCount,
            List<string> resolvedCategoryNames, List<string> unknownCategories,
            List<string> skippedRules, List<object> ruleResults, string reason)
        {
            return CommandResult.Ok(new
            {
                created = false,
                filter_id = (long?)null,
                name = filterName,
                category_count = categoryCount,
                rule_count = 0,
                categories = resolvedCategoryNames.ToArray(),
                unknown_categories = unknownCategories.Count > 0 ? unknownCategories.ToArray() : null,
                skipped_rules = skippedRules.Count > 0 ? skippedRules.ToArray() : null,
                rules = ruleResults.Count > 0 ? ruleResults.ToArray() : null,
                error = "Filter rules were requested but could not be applied: " + reason
            });
        }

        // ---- Rule construction (fully reflective for cross-version safety) ------------------

        private static FilterRule BuildFilterRule(ElementId paramId, string evaluator, JToken valueToken)
        {
            var factoryNames = MapEvaluator(evaluator);
            if (factoryNames.Length == 0)
                throw new ArgumentException(
                    $"Unsupported evaluator '{evaluator}'. Allowed: equals, not_equals, greater, " +
                    "less, contains, begins_with, ends_with.");

            // Candidate typed values, in preference order, derived from the JSON token.
            var candidateValues = BuildCandidateValues(valueToken);

            foreach (var factoryName in factoryNames)
            {
                var methods = typeof(ParameterFilterRuleFactory)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == factoryName)
                    .ToArray();

                foreach (var method in methods)
                {
                    var ps = method.GetParameters();
                    if (ps.Length < 1 || ps[0].ParameterType != typeof(ElementId))
                        continue;

                    foreach (var value in candidateValues)
                    {
                        var args = TryBuildArgs(ps, paramId, value);
                        if (args == null)
                            continue;
                        try
                        {
                            var result = method.Invoke(null, args) as FilterRule;
                            if (result != null)
                                return result;
                        }
                        catch (TargetInvocationException) { }
                        catch (ArgumentException) { }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to fill a factory method's argument array. Argument 0 is always the parameter
        /// ElementId. Argument 1 is the comparison value (coerced to the parameter type). Any
        /// trailing args — most commonly the case-sensitivity bool or an epsilon double that some
        /// Revit versions add — are filled from defaults / sensible fallbacks. Returns null when
        /// the signature cannot be satisfied.
        /// </summary>
        private static object[] TryBuildArgs(ParameterInfo[] ps, ElementId paramId, object value)
        {
            var args = new object[ps.Length];
            args[0] = paramId;

            if (ps.Length >= 2)
            {
                var valueType = ps[1].ParameterType;
                object coerced;
                if (!TryCoerce(value, valueType, out coerced))
                    return null;
                args[1] = coerced;
            }

            for (int j = 2; j < ps.Length; j++)
            {
                var pt = ps[j].ParameterType;
                if (ps[j].HasDefaultValue)
                    args[j] = ps[j].DefaultValue;
                else if (pt == typeof(bool))
                    args[j] = false;               // case-sensitivity flag (older signatures)
                else if (pt == typeof(double))
                    args[j] = 1e-6;                // numeric tolerance / epsilon
                else if (pt == typeof(string))
                    args[j] = string.Empty;
                else if (pt.IsValueType)
                    args[j] = Activator.CreateInstance(pt);
                else
                    args[j] = null;
            }

            return args;
        }

        /// <summary>Produces the comparison values worth trying, ordered by likelihood of acceptance.</summary>
        private static List<object> BuildCandidateValues(JToken valueToken)
        {
            var values = new List<object>();
            if (valueToken == null || valueToken.Type == JTokenType.Null)
            {
                values.Add(string.Empty);
                return values;
            }

            switch (valueToken.Type)
            {
                case JTokenType.Integer:
                    long lv;
                    try { lv = valueToken.Value<long>(); } catch { lv = 0; }
                    values.Add((int)lv);
                    values.Add((double)lv);
                    values.Add(lv.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case JTokenType.Float:
                    double dv;
                    try { dv = valueToken.Value<double>(); } catch { dv = 0; }
                    values.Add(dv);
                    values.Add(dv.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case JTokenType.Boolean:
                    bool bv;
                    try { bv = valueToken.Value<bool>(); } catch { bv = false; }
                    values.Add(bv ? 1 : 0);
                    values.Add(bv.ToString());
                    break;
                default:
                    var sv = valueToken.ToString();
                    values.Add(sv);
                    int parsedInt;
                    if (int.TryParse(sv, out parsedInt))
                        values.Add(parsedInt);
                    double parsedDouble;
                    if (double.TryParse(sv, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out parsedDouble))
                        values.Add(parsedDouble);
                    break;
            }
            return values;
        }

        private static bool TryCoerce(object value, Type targetType, out object result)
        {
            result = null;
            if (value == null)
            {
                if (!targetType.IsValueType) { result = null; return true; }
                return false;
            }

            if (targetType.IsInstanceOfType(value))
            {
                result = value;
                return true;
            }

            try
            {
                if (targetType == typeof(string))
                {
                    result = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                if (targetType == typeof(int))
                {
                    result = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                if (targetType == typeof(double))
                {
                    result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                if (targetType == typeof(long))
                {
                    result = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                if (targetType == typeof(bool))
                {
                    result = Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                if (targetType == typeof(ElementId) && value is int)
                {
                    result = RevitCompat.ToElementId((int)value);
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>Maps an evaluator keyword to candidate ParameterFilterRuleFactory method names.</summary>
        private static string[] MapEvaluator(string evaluator)
        {
            switch (evaluator.ToLowerInvariant())
            {
                case "equals":
                case "equal":
                case "=":
                    return new[] { "CreateEqualsRule" };
                case "not_equals":
                case "notequals":
                case "not_equal":
                case "!=":
                case "<>":
                    return new[] { "CreateNotEqualsRule" };
                case "greater":
                case "greater_than":
                case "greaterthan":
                case ">":
                    return new[] { "CreateGreaterRule", "CreateGreaterThanRule" };
                case "greater_or_equal":
                case ">=":
                    return new[] { "CreateGreaterOrEqualRule" };
                case "less":
                case "less_than":
                case "lessthan":
                case "<":
                    return new[] { "CreateLessRule", "CreateLessThanRule" };
                case "less_or_equal":
                case "<=":
                    return new[] { "CreateLessOrEqualRule" };
                case "contains":
                    return new[] { "CreateContainsRule" };
                case "not_contains":
                case "notcontains":
                    return new[] { "CreateNotContainsRule" };
                case "begins_with":
                case "beginswith":
                case "starts_with":
                    return new[] { "CreateBeginsWithRule" };
                case "not_begins_with":
                    return new[] { "CreateNotBeginsWithRule" };
                case "ends_with":
                case "endswith":
                    return new[] { "CreateEndsWithRule" };
                case "not_ends_with":
                    return new[] { "CreateNotEndsWithRule" };
                default:
                    return new string[0];
            }
        }

        // ---- ElementFilter assembly ---------------------------------------------------------

        private static ElementFilter BuildElementFilter(List<FilterRule> rules)
        {
            if (rules.Count == 1)
                return new ElementParameterFilter(rules[0]);
            return new ElementParameterFilter(rules);
        }

        /// <summary>
        /// Creates the ParameterFilterElement with rules. Prefers the
        /// Create(doc, name, categoryIds, ElementFilter) overload; falls back to creating an
        /// empty filter then assigning rules through SetRules / SetElementFilter via reflection.
        /// </summary>
        private static ParameterFilterElement CreateWithRules(
            Document doc, string name, IList<ElementId> categoryIds,
            ElementFilter elementFilter, List<FilterRule> rules, out int appliedRuleCount)
        {
            appliedRuleCount = 0;

            // Preferred: 4-arg Create with an ElementFilter.
            var createWithFilter = typeof(ParameterFilterElement)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "Create") return false;
                    var ps = m.GetParameters();
                    return ps.Length == 4
                        && typeof(Document).IsAssignableFrom(ps[0].ParameterType)
                        && ps[1].ParameterType == typeof(string)
                        && typeof(System.Collections.IEnumerable).IsAssignableFrom(ps[2].ParameterType)
                        && typeof(ElementFilter).IsAssignableFrom(ps[3].ParameterType);
                });

            if (createWithFilter != null)
            {
                try
                {
                    var result = createWithFilter.Invoke(
                        null, new object[] { doc, name, categoryIds, elementFilter }) as ParameterFilterElement;
                    if (result != null)
                    {
                        appliedRuleCount = rules.Count;
                        return result;
                    }
                }
                catch (TargetInvocationException tie)
                {
                    throw tie.InnerException ?? tie;
                }
            }

            // Fallback: create empty, then apply the rules.
            var filterElement = ParameterFilterElement.Create(doc, name, categoryIds);
            var assignmentErrors = new List<string>();

            var setElementFilter = typeof(ParameterFilterElement).GetMethod(
                "SetElementFilter", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(ElementFilter) }, null);
            if (setElementFilter != null)
            {
                try
                {
                    setElementFilter.Invoke(filterElement, new object[] { elementFilter });
                    appliedRuleCount = rules.Count;
                    return filterElement;
                }
                catch (TargetInvocationException tie)
                {
                    assignmentErrors.Add("SetElementFilter failed: " +
                        ((tie.InnerException ?? tie).Message));
                }
                catch (Exception ex)
                {
                    assignmentErrors.Add("SetElementFilter failed: " + ex.Message);
                }
            }
            else
            {
                assignmentErrors.Add("SetElementFilter is not available in this Revit version.");
            }

            var setRules = typeof(ParameterFilterElement)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "SetRules" && m.GetParameters().Length == 1);
            if (setRules != null)
            {
                try
                {
                    var paramType = setRules.GetParameters()[0].ParameterType;
                    object rulesArg = rules;
                    if (paramType.IsGenericType &&
                        paramType.GetGenericArguments().Length == 1 &&
                        paramType.GetGenericArguments()[0] != typeof(FilterRule))
                    {
                        // Build the exact IList<T> the signature expects.
                        var listType = typeof(List<>).MakeGenericType(paramType.GetGenericArguments()[0]);
                        var typedList = (System.Collections.IList)Activator.CreateInstance(listType);
                        foreach (var r in rules) typedList.Add(r);
                        rulesArg = typedList;
                    }
                    setRules.Invoke(filterElement, new object[] { rulesArg });
                    appliedRuleCount = rules.Count;
                    return filterElement;
                }
                catch (TargetInvocationException tie)
                {
                    assignmentErrors.Add("SetRules failed: " + ((tie.InnerException ?? tie).Message));
                }
                catch (Exception ex)
                {
                    assignmentErrors.Add("SetRules failed: " + ex.Message);
                }
            }
            else
            {
                assignmentErrors.Add("SetRules is not available in this Revit version.");
            }

            throw new InvalidOperationException(string.Join("; ", assignmentErrors));
        }
    }
}
