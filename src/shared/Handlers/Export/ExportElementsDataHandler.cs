using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports element parameter data for a category to a JSON or CSV file.
    /// This writes a data file (not the model), so NO Transaction is used.
    /// </summary>
    public class ExportElementsDataHandler : IRevitCommand
    {
        public string Name => "export_elements_data";

        public string Description =>
            "Export element parameter data for a category to a JSON or CSV file. " +
            "Reads the requested parameters from every instance of the category and writes them to output_path.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""category"",""output_path""],
  ""properties"":{
    ""category"":{""type"":""string"",""description"":""Category name, e.g. 'Walls', 'Doors'.""},
    ""output_path"":{""type"":""string"",""description"":""Absolute file path including extension (.json or .csv).""},
    ""parameter_names"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Parameter names to export. If omitted, exports a default set (Mark, Type, Level, plus common ones).""},
    ""format"":{""type"":""string"",""enum"":[""json"",""csv""],""default"":""json""}
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

            var categoryName = request.Value<string>("category");
            if (string.IsNullOrWhiteSpace(categoryName))
                return CommandResult.Fail("category is required.");

            var outputPath = request.Value<string>("output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                return CommandResult.Fail("output_path is required.");

            if (!Path.IsPathRooted(outputPath))
                return CommandResult.Fail("output_path must be an absolute rooted path: " + outputPath);

            string parentDir;
            try
            {
                parentDir = Path.GetDirectoryName(outputPath);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("output_path is not a valid file path: " + ex.Message);
            }

            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                return CommandResult.Fail("output_path parent directory does not exist: " + (parentDir ?? "<null>"));

            // Resolve format: explicit parameter wins, otherwise infer from extension, default json.
            var format = (request.Value<string>("format") ?? string.Empty).Trim().ToLowerInvariant();
            if (format.Length == 0)
            {
                var ext = (Path.GetExtension(outputPath) ?? string.Empty).Trim().ToLowerInvariant();
                format = ext == ".csv" ? "csv" : "json";
            }
            if (format != "json" && format != "csv")
                return CommandResult.Fail("format must be 'json' or 'csv' (got '" + format + "').");

            // Resolve category via BuiltInCategory enum (case-insensitive name match).
            BuiltInCategory? matchedBic = null;
            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var c = Category.GetCategory(doc, bic);
                    if (c != null && c.Name != null &&
                        c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedBic = bic;
                        break;
                    }
                }
                catch { }
            }

            if (matchedBic == null)
                return CommandResult.Fail("Category '" + categoryName + "' not found.");

            string resolvedCategoryName = categoryName;
            try
            {
                var cat = Category.GetCategory(doc, matchedBic.Value);
                if (cat != null && !string.IsNullOrEmpty(cat.Name))
                    resolvedCategoryName = cat.Name;
            }
            catch { }

            // Custom parameter list, or null to signal "use default set".
            string[] requestedParams = null;
            var paramToken = request["parameter_names"] as JArray;
            if (paramToken != null && paramToken.Count > 0)
            {
                var list = new List<string>();
                foreach (var t in paramToken)
                {
                    var n = t?.ToString();
                    if (!string.IsNullOrWhiteSpace(n))
                        list.Add(n.Trim());
                }
                if (list.Count > 0)
                    requestedParams = list.ToArray();
            }

            // Collect instances of the category.
            List<Element> elements;
            try
            {
                elements = new FilteredElementCollector(doc)
                    .OfCategory(matchedBic.Value)
                    .WhereElementIsNotElementType()
                    .ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to collect elements for category '" + resolvedCategoryName + "': " + ex.Message);
            }

            // Columns: built-ins (id, name, type, level) plus any explicitly requested parameters.
            var columns = new List<string> { "id", "name", "type", "level" };
            if (requestedParams != null)
            {
                foreach (var p in requestedParams)
                {
                    if (!columns.Contains(p, StringComparer.OrdinalIgnoreCase))
                        columns.Add(p);
                }
            }
            else
            {
                // Default extra parameter: Mark.
                columns.Add("Mark");
            }

            // Build one row dictionary per element.
            var rows = new List<Dictionary<string, object>>();
            foreach (var element in elements)
            {
                if (element == null)
                    continue;

                var row = new Dictionary<string, object>();

                row["id"] = RevitCompat.GetId(element.Id);
                row["name"] = SafeName(element);
                row["type"] = ResolveTypeName(doc, element);
                row["level"] = ResolveLevelName(doc, element);

                if (requestedParams != null)
                {
                    foreach (var p in requestedParams)
                        row[p] = ReadParameterValue(element, p);
                }
                else
                {
                    row["Mark"] = ReadParameterValue(element, "Mark");
                }

                rows.Add(row);
            }

            // Serialize and write.
            string content;
            try
            {
                content = format == "csv"
                    ? BuildCsv(columns, rows)
                    : BuildJson(columns, rows);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to serialize export data: " + ex.Message);
            }

            try
            {
                File.WriteAllText(outputPath, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to write output file '" + outputPath + "': " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_path = outputPath,
                category = resolvedCategoryName,
                element_count = rows.Count,
                parameter_count = columns.Count,
                format = format,
                error = (string)null
            });
        }

        // -- value extraction ------------------------------------------------

        /// <summary>
        /// Reads an instance parameter by name with unit-aware conversion
        /// (length-like specs to mm, area to m2, volume to m3, angle to degrees). Returns null when absent.
        /// </summary>
        private static object ReadParameterValue(Element element, string name)
        {
            Parameter parameter;
            try
            {
                parameter = element.LookupParameter(name);
            }
            catch
            {
                return null;
            }

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
                        return ConvertDouble(parameter);

                    case StorageType.ElementId:
                        var idValue = RevitCompat.GetIdOrNull(parameter.AsElementId());
                        // Prefer a human-readable string when one exists.
                        var vs = SafeValueString(parameter);
                        return string.IsNullOrEmpty(vs) ? (object)idValue : vs;

                    default:
                        return SafeValueString(parameter);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Converts a Double parameter to display units based on its data type.</summary>
        private static object ConvertDouble(Parameter parameter)
        {
            var raw = parameter.AsDouble();
            ForgeTypeId dataType = null;
            try
            {
                dataType = parameter.Definition?.GetDataType();
            }
            catch { }

            if (dataType != null)
            {
                try
                {
                    if (IsLengthSpec(dataType))
                        return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Millimeters), 2);
                    if (IsAreaSpec(dataType))
                        return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters), 4);
                    if (IsVolumeSpec(dataType))
                        return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.CubicMeters), 4);
                    if (IsAngleSpec(dataType))
                        return Math.Round(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Degrees), 4);
                }
                catch { }
            }

            return Math.Round(raw, 6);
        }

        private static bool IsLengthSpec(ForgeTypeId dataType)
        {
            return SameForgeTypeId(dataType, SpecTypeId.Length)
                   || SpecSupportsUnit(dataType, UnitTypeId.Millimeters)
                   || MatchesSpecType(dataType, "PipeSize")
                   || MatchesSpecType(dataType, "DuctSize")
                   || MatchesSpecType(dataType, "WireDiameter")
                   || MatchesSpecType(dataType, "CableTraySize")
                   || MatchesSpecType(dataType, "Reinforcement", "ReinforcementLength")
                   || MatchesSpecType(dataType, "HvacRoughness")
                   || MatchesSpecType(dataType, "Electrical", "CableTraySize");
        }

        private static bool IsAreaSpec(ForgeTypeId dataType)
        {
            return SameForgeTypeId(dataType, SpecTypeId.Area)
                   || SpecSupportsUnit(dataType, UnitTypeId.SquareMeters);
        }

        private static bool IsVolumeSpec(ForgeTypeId dataType)
        {
            return SameForgeTypeId(dataType, SpecTypeId.Volume)
                   || SpecSupportsUnit(dataType, UnitTypeId.CubicMeters);
        }

        private static bool IsAngleSpec(ForgeTypeId dataType)
        {
            return SameForgeTypeId(dataType, SpecTypeId.Angle)
                   || SpecSupportsUnit(dataType, UnitTypeId.Degrees);
        }

        private static bool SpecSupportsUnit(ForgeTypeId dataType, ForgeTypeId unitType)
        {
            try
            {
                foreach (var validUnit in UnitUtils.GetValidUnits(dataType))
                {
                    if (SameForgeTypeId(validUnit, unitType))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool MatchesSpecType(ForgeTypeId dataType, params string[] path)
        {
            try
            {
                var type = typeof(SpecTypeId);
                for (var i = 0; i < path.Length - 1; i++)
                {
                    type = type.GetNestedType(path[i], BindingFlags.Public);
                    if (type == null)
                        return false;
                }

                var prop = type.GetProperty(path[path.Length - 1], BindingFlags.Public | BindingFlags.Static);
                var value = prop?.GetValue(null, null) as ForgeTypeId;
                return SameForgeTypeId(dataType, value);
            }
            catch
            {
                return false;
            }
        }

        private static bool SameForgeTypeId(ForgeTypeId left, ForgeTypeId right)
        {
            try { return left != null && right != null && left == right; }
            catch { return false; }
        }

        private static string SafeValueString(Parameter parameter)
        {
            try
            {
                var v = parameter.AsValueString();
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveTypeName(Document doc, Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return null;
                var typeElement = doc.GetElement(typeId);
                return typeElement?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveLevelName(Document doc, Element element)
        {
            try
            {
                var levelId = element.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                {
                    var level = doc.GetElement(levelId) as Level;
                    if (level != null)
                        return level.Name;
                }
            }
            catch { }

            // Fallback: the LEVEL_PARAM built-in (covers families hosted by level).
            try
            {
                var lp = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                         ?? element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                         ?? element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                if (lp != null && lp.StorageType == StorageType.ElementId)
                {
                    var level = doc.GetElement(lp.AsElementId()) as Level;
                    if (level != null)
                        return level.Name;
                }
            }
            catch { }

            return null;
        }

        // -- serialization ---------------------------------------------------

        private static string BuildJson(List<string> columns, List<Dictionary<string, object>> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                var obj = new JObject();
                foreach (var col in columns)
                {
                    row.TryGetValue(col, out var value);
                    obj[col] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                }
                array.Add(obj);
            }
            return array.ToString(Formatting.Indented);
        }

        private static string BuildCsv(List<string> columns, List<Dictionary<string, object>> rows)
        {
            var sb = new StringBuilder();

            // Header row.
            sb.Append(string.Join(",", columns.Select(CsvEscape)));
            sb.Append("\r\n");

            // Data rows.
            foreach (var row in rows)
            {
                var cells = new List<string>(columns.Count);
                foreach (var col in columns)
                {
                    row.TryGetValue(col, out var value);
                    cells.Add(CsvEscape(FormatCsvValue(value)));
                }
                sb.Append(string.Join(",", cells));
                sb.Append("\r\n");
            }

            return sb.ToString();
        }

        private static string FormatCsvValue(object value)
        {
            if (value == null)
                return string.Empty;

            switch (value)
            {
                case string s:
                    return s;
                case bool b:
                    return b ? "true" : "false";
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case int i:
                    return i.ToString(CultureInfo.InvariantCulture);
                case long l:
                    return l.ToString(CultureInfo.InvariantCulture);
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        /// <summary>
        /// Quotes a CSV field when it contains the delimiter, a quote, or a line
        /// break. Embedded double-quotes are escaped by doubling them.
        /// </summary>
        private static string CsvEscape(string field)
        {
            if (field == null)
                return string.Empty;

            var needsQuoting =
                field.IndexOf(',') >= 0 ||
                field.IndexOf('"') >= 0 ||
                field.IndexOf('\n') >= 0 ||
                field.IndexOf('\r') >= 0;

            if (!needsQuoting)
                return field;

            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
    }
}
