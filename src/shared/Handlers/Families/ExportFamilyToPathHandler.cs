using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports (saves) a loadable family from the current project back to an .rfa file
    /// at the given path. Opens the family in a background Document via Document.EditFamily,
    /// performs SaveAs, then always closes the background document.
    /// </summary>
    public class ExportFamilyToPathHandler : IRevitCommand
    {
        public string Name => "export_family_to_path";

        public string Description =>
            "Export (save) a loadable family from the current project back to an .rfa file at the given path. " +
            "Opens family in background, saves, closes.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""output_path""],
  ""properties"": {
    ""family_id"": {""type"": ""string"", ""description"": ""Family ElementId. Either family_id or family_name required.""},
    ""family_name"": {""type"": ""string""},
    ""output_path"": {""type"": ""string"", ""description"": ""Absolute path with .rfa extension. Parent directory must exist.""},
    ""overwrite_existing"": {""type"": ""boolean"", ""default"": false}
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
            var outputPath = request.Value<string>("output_path");
            var overwriteExisting = request["overwrite_existing"] != null
                ? request.Value<bool>("overwrite_existing")
                : false;

            if (string.IsNullOrWhiteSpace(outputPath))
                return BuildErrorDto(null, familyName, outputPath, 0, "Parameter 'output_path' is required.");

            if (!Path.IsPathRooted(outputPath))
                return BuildErrorDto(null, familyName, outputPath, 0,
                    "output_path must be an absolute path (e.g. C:\\... or D:\\...). Relative paths are rejected.");

            if (string.IsNullOrWhiteSpace(familyIdStr) && string.IsNullOrWhiteSpace(familyName))
                return BuildErrorDto(null, familyName, outputPath, 0, "Either family_id or family_name is required.");

            // Validate extension
            if (!string.Equals(Path.GetExtension(outputPath), ".rfa", StringComparison.OrdinalIgnoreCase))
                return BuildErrorDto(null, familyName, outputPath, 0,
                    "output_path must have .rfa extension (got '" + (Path.GetExtension(outputPath) ?? "") + "').");

            // Validate parent directory exists
            string parentDir;
            try
            {
                parentDir = Path.GetDirectoryName(outputPath);
            }
            catch (Exception ex)
            {
                return BuildErrorDto(null, familyName, outputPath, 0,
                    "Could not resolve parent directory: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
                return BuildErrorDto(null, familyName, outputPath, 0,
                    "Parent directory does not exist: " + (parentDir ?? "<null>"));

            // Validate overwrite policy
            if (File.Exists(outputPath) && !overwriteExisting)
                return BuildErrorDto(null, familyName, outputPath, 0,
                    "File already exists and overwrite_existing=false: " + outputPath);

            // Resolve the Family
            Family family = null;
            if (!string.IsNullOrWhiteSpace(familyIdStr))
            {
                if (!long.TryParse(familyIdStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId))
                    return BuildErrorDto(null, familyName, outputPath, 0,
                        "family_id must be a numeric element id (got '" + familyIdStr + "').");

                if (!RevitCompat.CanRepresentElementId(rawId))
                    return BuildErrorDto(null, familyName, outputPath, 0, RevitCompat.ElementIdRangeError(rawId));

                var elId = RevitCompat.ToElementId(rawId);
                family = doc.GetElement(elId) as Family;
                if (family == null)
                    return BuildErrorDto(null, familyName, outputPath, 0,
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
                    return BuildErrorDto(null, familyName, outputPath, 0,
                        "No Family found with name '" + familyName + "'.");

                if (matches.Count > 1)
                    return BuildErrorDto(null, familyName, outputPath, 0,
                        "Multiple Families found with name '" + familyName + "' (" + matches.Count
                        + "). Disambiguate by passing family_id.");

                family = matches[0];
            }

            var resolvedFamilyId = RevitCompat.GetId(family.Id);
            var resolvedFamilyName = SafeName(family) ?? string.Empty;

            // Reject in-place families — they cannot be saved as .rfa
            if (SafeIsInPlace(family))
            {
                return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, outputPath, 0,
                    "Family '" + resolvedFamilyName + "' is in-place and cannot be exported to an .rfa file.");
            }

            // Reject system families (Wall/Floor/Pipe types etc.)
            if (IsSystemFamily(family))
            {
                return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, outputPath, 0,
                    "System families cannot be exported.");
            }

            // Open family in background, save, close. familyDoc.Close() must always run.
            Document familyDoc = null;
            try
            {
                try
                {
                    familyDoc = doc.EditFamily(family);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException revitEx)
                {
                    return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, outputPath, 0,
                        "EditFamily failed: " + revitEx.Message);
                }
                catch (Exception ex)
                {
                    return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, outputPath, 0,
                        "EditFamily failed: " + ex.Message);
                }

                if (familyDoc == null)
                {
                    return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, outputPath, 0,
                        "EditFamily returned null Document.");
                }

                var saveOpts = new SaveAsOptions
                {
                    OverwriteExistingFile = true
                };

                try
                {
                    familyDoc.SaveAs(outputPath, saveOpts);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException revitEx)
                {
                    return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, outputPath, 0,
                        "SaveAs failed: " + revitEx.Message);
                }
                catch (Exception ex)
                {
                    return BuildErrorDto(resolvedFamilyId, resolvedFamilyName, outputPath, 0,
                        "SaveAs failed: " + ex.Message);
                }

                long fileSize = 0;
                try
                {
                    fileSize = new FileInfo(outputPath).Length;
                }
                catch
                {
                    // Leave fileSize=0 if we can't stat the file (very unlikely after a successful SaveAs).
                }

                return CommandResult.Ok(new
                {
                    exported = true,
                    family_id = resolvedFamilyId.ToString(CultureInfo.InvariantCulture),
                    family_name = resolvedFamilyName,
                    output_path = outputPath,
                    file_size_bytes = fileSize,
                    error = (string)null
                });
            }
            finally
            {
                if (familyDoc != null)
                {
                    try { familyDoc.Close(false); }
                    catch { /* best-effort close; do not mask the outer result */ }
                }
            }
        }

        private static CommandResult BuildErrorDto(
            long? familyId,
            string familyName,
            string outputPath,
            long fileSize,
            string error)
        {
            return CommandResult.Ok(new
            {
                exported = false,
                family_id = familyId.HasValue ? familyId.Value.ToString(CultureInfo.InvariantCulture) : null,
                family_name = familyName ?? string.Empty,
                output_path = outputPath ?? string.Empty,
                file_size_bytes = fileSize,
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
        /// Loadable families are editable; system families are not. In-place families are also editable
        /// but excluded explicitly (they are handled by their own IsInPlace check upstream).
        /// </summary>
        private static bool IsSystemFamily(Family family)
        {
            if (family == null) return false;

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
                if (!family.IsEditable) return true;
            }
            catch
            {
                // older Revit may throw — assume not system
            }

            return false;
        }
    }
}
