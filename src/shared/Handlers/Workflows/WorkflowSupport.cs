using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    internal static class WorkflowSupport
    {
        public const double FeetToMm = 304.8;
        public const double SquareFeetToSquareMeters = 0.092903043596;
        public const double CubicFeetToCubicMeters = 0.028316846592;

        public static JObject ParseParams(string paramsJson)
        {
            return string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
        }

        public static JObject Step(string name, string tool, string status, string inputsSummary, object result, string error = null)
        {
            return new JObject
            {
                ["name"] = name,
                ["tool"] = tool,
                ["status"] = status,
                ["inputs_summary"] = inputsSummary,
                ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result),
                ["error"] = error == null ? JValue.CreateNull() : new JValue(error)
            };
        }

        public static JObject Rollback(string strategy, bool rolledBack, string reason)
        {
            return new JObject
            {
                ["strategy"] = strategy,
                ["rolled_back"] = rolledBack,
                ["reason"] = reason ?? string.Empty
            };
        }

        public static JObject Envelope(
            string workflow,
            bool dryRun,
            string status,
            JArray steps,
            IEnumerable<long> createdIds,
            IEnumerable<long> modifiedIds,
            IEnumerable<string> warnings,
            JObject rollback)
        {
            return new JObject
            {
                ["workflow"] = workflow,
                ["dry_run"] = dryRun,
                ["status"] = status,
                ["steps"] = steps ?? new JArray(),
                ["created_ids"] = ToJArray(createdIds),
                ["modified_ids"] = ToJArray(modifiedIds),
                ["warnings"] = ToJArray(warnings),
                ["rollback"] = rollback ?? Rollback("None", false, string.Empty)
            };
        }

        public static JArray ToJArray(IEnumerable<long> values)
        {
            var array = new JArray();
            if (values == null)
                return array;

            foreach (var value in values)
                array.Add(value);
            return array;
        }

        public static JArray ToJArray(IEnumerable<string> values)
        {
            var array = new JArray();
            if (values == null)
                return array;

            foreach (var value in values)
                array.Add(value ?? string.Empty);
            return array;
        }

        public static long[] ReadLongArray(JObject request, string name)
        {
            var token = request[name];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type != JTokenType.Array)
                throw new ArgumentException(name + " must be an array of integers.");

            var values = new List<long>();
            var array = (JArray)token;
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i].Type != JTokenType.Integer)
                    throw new ArgumentException(name + "[" + i.ToString(CultureInfo.InvariantCulture) + "] must be an integer.");
                var value = array[i].Value<long>();
                if (!RevitCompat.CanRepresentElementId(value))
                    throw new ArgumentException(name + "[" + i.ToString(CultureInfo.InvariantCulture) + "] " + RevitCompat.ElementIdRangeError(value));
                values.Add(value);
            }

            return values.ToArray();
        }

        public static string[] ReadStringArray(JObject request, string name)
        {
            var token = request[name];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type != JTokenType.Array)
                throw new ArgumentException(name + " must be an array of strings.");

            var values = new List<string>();
            foreach (var item in (JArray)token)
            {
                var value = item.Type == JTokenType.String ? item.Value<string>() : item.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value.Trim());
            }

            return values.ToArray();
        }

        public static bool TryResolveCategory(Document doc, string input, out Category category, out string error)
        {
            category = null;
            error = null;

            if (doc == null)
            {
                error = "Document is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "Category name is required.";
                return false;
            }

            var trimmed = input.Trim();
            foreach (Category cat in doc.Settings.Categories)
            {
                try
                {
                    if (cat != null && cat.Name != null && cat.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        category = cat;
                        return true;
                    }
                }
                catch { }
            }

            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                if (!bic.ToString().Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    category = Category.GetCategory(doc, bic);
                    if (category != null)
                        return true;
                }
                catch { }
            }

            error = "Category '" + input + "' could not be resolved.";
            return false;
        }

        public static List<Element> CollectInstances(Document doc, Category category, int limit, out bool truncated)
        {
            truncated = false;
            var elements = new List<Element>();
            if (doc == null || category == null)
                return elements;

            var collector = new FilteredElementCollector(doc)
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType();

            foreach (var element in collector)
            {
                if (element == null)
                    continue;
                if (elements.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                elements.Add(element);
            }

            return elements;
        }

        public static string SafeName(Element element)
        {
            if (element == null)
                return null;

            try { return element.Name; }
            catch { return null; }
        }

        public static string SafeCategoryName(Element element)
        {
            try { return element?.Category?.Name; }
            catch { return null; }
        }

        public static JObject ElementSummary(Element element)
        {
            if (element == null)
                return null;

            return new JObject
            {
                ["element_id"] = RevitCompat.GetId(element.Id),
                ["unique_id"] = element.UniqueId,
                ["name"] = SafeName(element),
                ["category"] = SafeCategoryName(element)
            };
        }

        public static string UniqueName(Document doc, IEnumerable<Element> existingElements, string requestedBase)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existingElements != null)
            {
                foreach (var element in existingElements)
                {
                    var name = SafeName(element);
                    if (!string.IsNullOrEmpty(name))
                        existing.Add(name);
                }
            }

            var baseName = string.IsNullOrWhiteSpace(requestedBase) ? "RvtMcp Workflow" : requestedBase.Trim();
            var candidate = baseName;
            var index = 1;
            while (existing.Contains(candidate))
            {
                index++;
                candidate = baseName + " " + index.ToString(CultureInfo.InvariantCulture);
            }

            return candidate;
        }

        public static string ValidateRootedPath(string path, string parameterName, bool requireParent, bool requireExistingFile = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                return parameterName + " is required.";

            if (!Path.IsPathRooted(path))
                return parameterName + " must be an absolute rooted path: " + path;

            if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
                return parameterName + " cannot contain relative traversal elements: " + path;

            var fullPath = Path.GetFullPath(path);

            if (requireParent)
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    return parameterName + " parent directory does not exist: " + (parent ?? "<null>");
            }

            if (requireExistingFile && !File.Exists(fullPath))
                return parameterName + " file does not exist: " + fullPath;

            return null;
        }

        public static string NormalizeName(string value)
        {
            if (value == null)
                return string.Empty;

            var cleaned = value.Trim();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");

            foreach (var ch in new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' })
                cleaned = cleaned.Replace(ch, '-');

            return cleaned;
        }

        public static string ApplyPattern(string pattern, Element element, int index)
        {
            var name = SafeName(element) ?? string.Empty;
            var result = string.IsNullOrWhiteSpace(pattern) ? NormalizeName(name) : pattern;
            result = result.Replace("{Name}", name);
            result = result.Replace("{Original}", name);
            result = result.Replace("{Index}", index.ToString("000", CultureInfo.InvariantCulture));
            result = result.Replace("{Id}", RevitCompat.GetId(element.Id).ToString(CultureInfo.InvariantCulture));
            result = result.Replace("{Category}", SafeCategoryName(element) ?? string.Empty);

            var view = element as View;
            if (view != null)
                result = result.Replace("{Type}", view.ViewType.ToString());

            var sheet = element as ViewSheet;
            if (sheet != null)
                result = result.Replace("{SheetNumber}", sheet.SheetNumber ?? string.Empty);

            var level = element as Level;
            if (level != null)
                result = result.Replace("{ElevationMm}", Math.Round(level.Elevation * FeetToMm, 0).ToString(CultureInfo.InvariantCulture));

            return NormalizeName(result);
        }

        public static object ReadParameterValue(Document doc, Element element, string parameterName)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
                return null;

            Parameter parameter = null;
            try { parameter = element.LookupParameter(parameterName); }
            catch { }

            if (parameter == null)
                return null;

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString();
                    case StorageType.Integer:
                        return parameter.AsInteger();
                    case StorageType.Double:
                        return ConvertDoubleFromInternal(parameter);
                    case StorageType.ElementId:
                        var id = parameter.AsElementId();
                        var idValue = id == null ? (long?)null : RevitCompat.GetId(id);
                        var referenced = id == null ? null : doc.GetElement(id);
                        return referenced == null ? (object)idValue : SafeName(referenced);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool TrySetParameterFromString(Parameter parameter, string value, out string error)
        {
            error = null;
            if (parameter == null)
            {
                error = "Parameter not found.";
                return false;
            }

            if (parameter.IsReadOnly)
            {
                error = "Parameter is read-only.";
                return false;
            }

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return CheckSet(parameter.Set(value ?? string.Empty), out error);

                    case StorageType.Integer:
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                        {
                            error = "Value must be an integer.";
                            return false;
                        }
                        return CheckSet(parameter.Set(intValue), out error);

                    case StorageType.Double:
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                        {
                            error = "Value must be a number.";
                            return false;
                        }
                        return CheckSet(parameter.Set(ConvertDoubleToInternal(parameter, doubleValue)), out error);

                    case StorageType.ElementId:
                        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
                        {
                            error = "Value must be an element id integer.";
                            return false;
                        }
                        if (!RevitCompat.CanRepresentElementId(idValue))
                        {
                            error = RevitCompat.ElementIdRangeError(idValue);
                            return false;
                        }
                        return CheckSet(parameter.Set(RevitCompat.ToElementId(idValue)), out error);

                    default:
                        error = "Unsupported parameter storage type: " + parameter.StorageType;
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static JArray ReadRowsFromJsonOrCsv(string path, out string error)
        {
            error = null;
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            try
            {
                if (ext == ".json")
                {
                    var token = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
                    if (token.Type != JTokenType.Array)
                    {
                        error = "JSON import file must be an array of objects.";
                        return new JArray();
                    }
                    return (JArray)token;
                }

                if (ext == ".csv")
                    return ReadCsv(path);

                error = "Unsupported import extension '" + ext + "'. Use .json or .csv.";
                return new JArray();
            }
            catch (Exception ex)
            {
                error = "Failed to read import file: " + ex.Message;
                return new JArray();
            }
        }

        public static void WriteJsonOrCsv(string path, JArray rows, IEnumerable<string> columns)
        {
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            if (ext == ".csv")
            {
                File.WriteAllText(path, BuildCsv(rows, columns), new UTF8Encoding(false));
                return;
            }

            if (ext == ".json" || string.IsNullOrEmpty(ext))
            {
                File.WriteAllText(path, rows.ToString(Formatting.Indented), new UTF8Encoding(false));
                return;
            }

            throw new InvalidOperationException("Unsupported output extension '" + ext + "'. Use .json or .csv.");
        }

        private static bool CheckSet(bool result, out string error)
        {
            error = result ? null : "Revit rejected the parameter value.";
            return result;
        }

        private static object ConvertDoubleFromInternal(Parameter parameter)
        {
            var raw = parameter.AsDouble();
            var typeId = GetDataTypeId(parameter);
            if (MatchesSpec(typeId, "length"))
                return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Millimeters), 3);
            if (MatchesSpec(typeId, "area"))
                return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters), 4);
            if (MatchesSpec(typeId, "volume"))
                return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.CubicMeters), 4);
            if (MatchesSpec(typeId, "angle"))
                return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Degrees), 4);
            return Math.Round(raw, 6);
        }

        private static double ConvertDoubleToInternal(Parameter parameter, double value)
        {
            var typeId = GetDataTypeId(parameter);
            if (MatchesSpec(typeId, "length"))
                return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
            if (MatchesSpec(typeId, "area"))
                return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.SquareMeters);
            if (MatchesSpec(typeId, "volume"))
                return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.CubicMeters);
            if (MatchesSpec(typeId, "angle"))
                return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Degrees);
            return value;
        }

        private static string GetDataTypeId(Parameter parameter)
        {
            try { return parameter.Definition?.GetDataType()?.TypeId; }
            catch { return null; }
        }

        private static bool MatchesSpec(string typeId, string specName)
        {
            if (string.IsNullOrWhiteSpace(typeId))
                return false;

            var lower = typeId.ToLowerInvariant();
            return lower.EndsWith(":" + specName, StringComparison.Ordinal)
                || lower.EndsWith("." + specName, StringComparison.Ordinal)
                || lower.Contains(":" + specName + "-")
                || lower.Contains("." + specName + "-");
        }

        private static JArray ReadCsv(string path)
        {
            var rows = new JArray();
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0)
                return rows;

            var headers = ParseCsvLine(lines[0]).ToArray();
            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var values = ParseCsvLine(lines[i]).ToArray();
                var obj = new JObject();
                for (var c = 0; c < headers.Length; c++)
                    obj[headers[c]] = c < values.Length ? values[c] : string.Empty;
                rows.Add(obj);
            }

            return rows;
        }

        private static IEnumerable<string> ParseCsvLine(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    cells.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }
            cells.Add(sb.ToString());
            return cells;
        }

        private static string BuildCsv(JArray rows, IEnumerable<string> columns)
        {
            var cols = columns.ToArray();
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", cols.Select(CsvEscape)));
            foreach (var rowToken in rows)
            {
                var obj = rowToken as JObject;
                if (obj == null)
                    continue;

                var cells = cols.Select(col => CsvEscape(obj[col]?.ToString() ?? string.Empty));
                sb.AppendLine(string.Join(",", cells));
            }
            return sb.ToString();
        }

        private static string CsvEscape(string value)
        {
            value = value ?? string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
                return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
