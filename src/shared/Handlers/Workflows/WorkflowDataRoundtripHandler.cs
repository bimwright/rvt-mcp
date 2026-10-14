using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class WorkflowDataRoundtripHandler : IRevitCommand
    {
        public string Name => "workflow_data_roundtrip";
        public string Description => "Export category parameter data to JSON/CSV and optionally import edited values back with dry-run validation.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""category"", ""export_path""],
  ""properties"": {
    ""category"": { ""type"": ""string"" },
    ""parameter_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""export_path"": { ""type"": ""string"" },
    ""import_path"": { ""type"": ""string"" },
    ""mode"": { ""type"": ""string"", ""enum"": [""export_only"", ""import_only"", ""export_then_import""], ""default"": ""export_only"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""key_field"": { ""type"": ""string"", ""default"": ""element_id"" },
    ""output"": { ""type"": ""string"", ""enum"": [""inline"", ""file""], ""default"": ""inline"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No active document is available.");

            JObject request;
            try
            {
                request = WorkflowSupport.ParseParams(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var categoryName = request.Value<string>("category");
            var exportPath = request.Value<string>("export_path");
            var importPath = request.Value<string>("import_path");
            var mode = (request.Value<string>("mode") ?? "export_only").Trim().ToLowerInvariant();
            var dryRun = request.Value<bool?>("dry_run") ?? true;
            var keyField = request.Value<string>("key_field") ?? "element_id";
            string[] parameterNames;
            try
            {
                parameterNames = WorkflowSupport.ReadStringArray(request, "parameter_names");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            if (string.IsNullOrWhiteSpace(categoryName))
                return CommandResult.Fail("category is required.");
            if (mode != "export_only" && mode != "import_only" && mode != "export_then_import")
                return CommandResult.Fail("mode must be one of: export_only, import_only, export_then_import.");
            if (!WorkflowSupport.TryResolveCategory(doc, categoryName, out var category, out var categoryError))
                return CommandResult.Fail(categoryError);

            var requiresExport = mode == "export_only" || mode == "export_then_import";
            var requiresImport = mode == "import_only" || mode == "export_then_import";
            if (requiresExport)
            {
                var error = WorkflowSupport.ValidateRootedPath(exportPath, "export_path", true);
                if (error != null)
                    return CommandResult.Fail(error);
                var ext = (Path.GetExtension(exportPath) ?? string.Empty).ToLowerInvariant();
                if (ext != ".json" && ext != ".csv")
                    return CommandResult.Fail("export_path must end with .json or .csv.");
            }

            if (requiresImport)
            {
                var error = WorkflowSupport.ValidateRootedPath(importPath, "import_path", false, true);
                if (error != null)
                    return CommandResult.Fail(error);
            }

            var steps = new JArray();
            var warnings = new List<string>();
            var changedElements = new JArray();
            var skippedRows = new JArray();
            var validationErrors = new JArray();
            var modifiedIds = new List<long>();
            var exportedRows = 0;

            if (requiresExport)
            {
                try
                {
                    var rows = BuildExportRows(doc, category, parameterNames, out var columns, out var truncated);
                    WorkflowSupport.WriteJsonOrCsv(exportPath, rows, columns);
                    exportedRows = rows.Count;
                    if (truncated)
                        warnings.Add("Export was capped at 10000 elements to avoid locking the Revit UI.");

                    steps.Add(WorkflowSupport.Step(
                        "Export Element Parameters",
                        "export_elements_data",
                        "succeeded",
                        "Export category '" + category.Name + "' to " + exportPath,
                        new { output_path = exportPath, rows = rows.Count, columns = columns.Length, truncated }));
                }
                catch (Exception ex)
                {
                    steps.Add(WorkflowSupport.Step("Export Element Parameters", "export_elements_data", "failed", "Export category data.", null, ex.Message));
                    return CommandResult.Ok(BuildEnvelope("failed", dryRun, steps, warnings, modifiedIds, exportPath, exportedRows, 0, changedElements, skippedRows, validationErrors, WorkflowSupport.Rollback("None", false, "Export failed before any model transaction.")));
                }
            }

            var importedRows = 0;
            if (requiresImport)
            {
                var importRows = WorkflowSupport.ReadRowsFromJsonOrCsv(importPath, out var readError);
                if (readError != null)
                {
                    validationErrors.Add(readError);
                    steps.Add(WorkflowSupport.Step("Read Import File", "workflow_data_roundtrip", "failed", "Read import rows from " + importPath, null, readError));
                    return CommandResult.Ok(BuildEnvelope("failed", dryRun, steps, warnings, modifiedIds, exportPath, exportedRows, 0, changedElements, skippedRows, validationErrors, WorkflowSupport.Rollback("None", false, "Import file validation failed.")));
                }

                importedRows = importRows.Count;
                steps.Add(WorkflowSupport.Step(
                    "Read Import File",
                    "workflow_data_roundtrip",
                    "succeeded",
                    "Read import rows from " + importPath,
                    new { rows = importRows.Count }));

                var changes = ValidateImportRows(doc, importRows, parameterNames, keyField, changedElements, skippedRows, validationErrors);
                steps.Add(WorkflowSupport.Step(
                    "Validate Import Changes",
                    "set_element_parameter_values",
                    validationErrors.Count == 0 ? "succeeded" : "failed",
                    "Validate key fields, duplicate rows, writable parameters, and value conversion.",
                    new { proposed_changes = changes.Count, skipped_rows = skippedRows.Count, validation_errors = validationErrors.Count }));

                if (validationErrors.Count > 0)
                    return CommandResult.Ok(BuildEnvelope("failed", dryRun, steps, warnings, modifiedIds, exportPath, exportedRows, importedRows, changedElements, skippedRows, validationErrors, WorkflowSupport.Rollback("None", false, "Import validation failed before transaction.")));

                if (dryRun)
                {
                    steps.Add(WorkflowSupport.Step(
                        "Apply Import Changes",
                        "set_element_parameter_values",
                        "skipped",
                        "Dry-run: validated changes were not committed.",
                        new { proposed_changes = changes.Count }));
                    return CommandResult.Ok(BuildEnvelope("succeeded", true, steps, warnings, modifiedIds, exportPath, exportedRows, importedRows, changedElements, skippedRows, validationErrors, WorkflowSupport.Rollback("Transaction", false, "Dry-run; no transaction opened.")));
                }

                using (var tx = new Transaction(doc, "RvtMcp: workflow data roundtrip import"))
                {
                    tx.Start();
                    try
                    {
                        foreach (var change in changes)
                        {
                            if (!WorkflowSupport.TrySetParameterFromString(change.Parameter, change.Value, out var setError))
                                throw new InvalidOperationException("Element " + change.ElementId.ToString(CultureInfo.InvariantCulture) + " parameter '" + change.ParameterName + "': " + setError);
                            modifiedIds.Add(change.ElementId);
                        }

                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                            throw new InvalidOperationException("Transaction status: " + status);

                        steps.Add(WorkflowSupport.Step(
                            "Apply Import Changes",
                            "set_element_parameter_values",
                            "succeeded",
                            "Committed validated parameter changes.",
                            new { changed_values = changes.Count, changed_elements = modifiedIds.Distinct().Count() }));
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted())
                            tx.RollBack();
                        validationErrors.Add(ex.Message);
                        steps.Add(WorkflowSupport.Step("Apply Import Changes", "set_element_parameter_values", "failed", "Committed validated parameter changes.", null, ex.Message));
                        return CommandResult.Ok(BuildEnvelope("failed", false, steps, warnings, modifiedIds, exportPath, exportedRows, importedRows, changedElements, skippedRows, validationErrors, WorkflowSupport.Rollback("Transaction", true, "Import transaction failed and was rolled back.")));
                    }
                }
            }

            return CommandResult.Ok(BuildEnvelope(
                "succeeded",
                dryRun,
                steps,
                warnings,
                modifiedIds,
                exportPath,
                exportedRows,
                importedRows,
                changedElements,
                skippedRows,
                validationErrors,
                WorkflowSupport.Rollback(requiresImport ? "Transaction" : "None", false, requiresImport ? "Import committed or not requested." : "Export-only file write; no model transaction.")));
        }

        private JObject BuildEnvelope(
            string status,
            bool dryRun,
            JArray steps,
            IEnumerable<string> warnings,
            IEnumerable<long> modifiedIds,
            string exportPath,
            int exportedRows,
            int importedRows,
            JArray changedElements,
            JArray skippedRows,
            JArray validationErrors,
            JObject rollback)
        {
            var envelope = WorkflowSupport.Envelope(Name, dryRun, status, steps, Array.Empty<long>(), modifiedIds.Distinct(), warnings, rollback);
            envelope["exported_file_path"] = exportPath ?? string.Empty;
            envelope["exported_rows"] = exportedRows;
            envelope["imported_row_count"] = importedRows;
            envelope["changed_elements"] = changedElements ?? new JArray();
            envelope["skipped_rows"] = skippedRows ?? new JArray();
            envelope["validation_errors"] = validationErrors ?? new JArray();
            return envelope;
        }

        private static JArray BuildExportRows(Document doc, Category category, string[] parameterNames, out string[] columns, out bool truncated)
        {
            var elements = WorkflowSupport.CollectInstances(doc, category, 10000, out truncated);
            var parameters = parameterNames != null && parameterNames.Length > 0
                ? parameterNames
                : new[] { "Mark", "Comments" };

            var cols = new List<string> { "element_id", "unique_id", "category", "name", "type_name", "level_name" };
            cols.AddRange(parameters.Where(p => !cols.Contains(p, StringComparer.OrdinalIgnoreCase)));
            columns = cols.ToArray();

            var rows = new JArray();
            foreach (var element in elements)
            {
                var row = new JObject
                {
                    ["element_id"] = RevitCompat.GetId(element.Id),
                    ["unique_id"] = element.UniqueId,
                    ["category"] = WorkflowSupport.SafeCategoryName(element),
                    ["name"] = WorkflowSupport.SafeName(element),
                    ["type_name"] = ResolveTypeName(doc, element),
                    ["level_name"] = ResolveLevelName(doc, element)
                };

                foreach (var parameter in parameters)
                {
                    var value = WorkflowSupport.ReadParameterValue(doc, element, parameter);
                    row[parameter] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                }

                rows.Add(row);
            }

            return rows;
        }

        private static List<ImportChange> ValidateImportRows(
            Document doc,
            JArray rows,
            string[] requestedParameters,
            string keyField,
            JArray changedElements,
            JArray skippedRows,
            JArray validationErrors)
        {
            var changes = new List<ImportChange>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var metadata = new HashSet<string>(new[] { "element_id", "id", "unique_id", "category", "name", "type_name", "level_name" }, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < rows.Count; i++)
            {
                var obj = rows[i] as JObject;
                if (obj == null)
                {
                    skippedRows.Add(new JObject { ["row"] = i, ["reason"] = "Row is not an object." });
                    continue;
                }

                var keyToken = obj[keyField];
                if (keyToken == null && string.Equals(keyField, "element_id", StringComparison.OrdinalIgnoreCase))
                    keyToken = obj["id"];
                if (keyToken == null || string.IsNullOrWhiteSpace(keyToken.ToString()))
                {
                    validationErrors.Add("Row " + i.ToString(CultureInfo.InvariantCulture) + ": missing key_field '" + keyField + "'.");
                    continue;
                }

                var key = keyToken.ToString();
                if (!seenKeys.Add(key))
                {
                    validationErrors.Add("Duplicate import key: " + key);
                    continue;
                }

                var element = ResolveElement(doc, keyField, key);
                if (element == null)
                {
                    skippedRows.Add(new JObject { ["row"] = i, ["key"] = key, ["reason"] = "Element not found." });
                    continue;
                }

                var parameters = requestedParameters != null && requestedParameters.Length > 0
                    ? requestedParameters
                    : obj.Properties().Select(p => p.Name).Where(n => !metadata.Contains(n)).ToArray();

                foreach (var parameterName in parameters)
                {
                    if (!obj.TryGetValue(parameterName, StringComparison.OrdinalIgnoreCase, out var valueToken))
                        continue;

                    var parameter = element.LookupParameter(parameterName);
                    if (parameter == null)
                    {
                        validationErrors.Add("Row " + i.ToString(CultureInfo.InvariantCulture) + " element " + RevitCompat.GetId(element.Id).ToString(CultureInfo.InvariantCulture) + ": parameter '" + parameterName + "' not found.");
                        continue;
                    }
                    if (parameter.IsReadOnly)
                    {
                        validationErrors.Add("Row " + i.ToString(CultureInfo.InvariantCulture) + " element " + RevitCompat.GetId(element.Id).ToString(CultureInfo.InvariantCulture) + ": parameter '" + parameterName + "' is read-only.");
                        continue;
                    }

                    var oldValue = WorkflowSupport.ReadParameterValue(doc, element, parameterName);
                    var newValue = valueToken.Type == JTokenType.Null ? string.Empty : valueToken.ToString();
                    if (string.Equals(Convert.ToString(oldValue, CultureInfo.InvariantCulture), newValue, StringComparison.Ordinal))
                        continue;

                    changes.Add(new ImportChange
                    {
                        Element = element,
                        ElementId = RevitCompat.GetId(element.Id),
                        Parameter = parameter,
                        ParameterName = parameterName,
                        Value = newValue
                    });
                    changedElements.Add(new JObject
                    {
                        ["row"] = i,
                        ["element_id"] = RevitCompat.GetId(element.Id),
                        ["parameter"] = parameterName,
                        ["old_value"] = oldValue == null ? JValue.CreateNull() : JToken.FromObject(oldValue),
                        ["new_value"] = newValue
                    });
                }
            }

            return changes;
        }

        private static Element ResolveElement(Document doc, string keyField, string key)
        {
            if (string.Equals(keyField, "unique_id", StringComparison.OrdinalIgnoreCase))
                return doc.GetElement(key);

            if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId) && RevitCompat.CanRepresentElementId(rawId))
                return doc.GetElement(RevitCompat.ToElementId(rawId));

            return null;
        }

        private static string ResolveTypeName(Document doc, Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                var type = typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId);
                return WorkflowSupport.SafeName(type);
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
                    return WorkflowSupport.SafeName(doc.GetElement(levelId));
            }
            catch { }
            return null;
        }

        private class ImportChange
        {
            public Element Element;
            public long ElementId;
            public Parameter Parameter;
            public string ParameterName;
            public string Value;
        }
    }
}
