using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Loads an .rfa family file from disk into the active Revit document.
    /// </summary>
    public class LoadFamilyFromPathHandler : IRevitCommand
    {
        public string Name => "load_family_from_path";

        public string Description =>
            "Load an .rfa family file from disk into the active Revit document. " +
            "Returns the loaded family id and the new symbol/type ids created.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""path""],
  ""properties"": {
    ""path"": {""type"": ""string"", ""description"": ""Absolute path to a .rfa file on disk.""},
    ""overwrite_existing"": {""type"": ""boolean"", ""default"": true, ""description"": ""Pass to IFamilyLoadOptions: if family already loaded, overwrite parameters and types.""},
    ""overwrite_parameter_values"": {""type"": ""boolean"", ""default"": false, ""description"": ""If overwriting, also overwrite parameter values of existing instances.""},
    ""include_symbols"": {""type"": ""boolean"", ""default"": false, ""description"": ""Include a bounded symbol id/name preview in the response.""},
    ""max_symbol_results"": {""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 1000}
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
                request = JObject.Parse(paramsJson ?? "{}");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("Parameter 'path' is required.");

            var overwriteExisting = request["overwrite_existing"] != null
                ? request.Value<bool>("overwrite_existing")
                : true;
            var overwriteParameterValues = request["overwrite_parameter_values"] != null
                ? request.Value<bool>("overwrite_parameter_values")
                : false;
            var includeSymbols = request.Value<bool?>("include_symbols") ?? false;
            var maxSymbolResults = request.Value<int?>("max_symbol_results") ?? 200;
            if (maxSymbolResults < 1 || maxSymbolResults > 1000)
                return CommandResult.Fail("max_symbol_results must be between 1 and the hard maximum of 1000.");

            if (!File.Exists(path))
            {
                return CommandResult.Ok(new
                {
                    loaded = false,
                    error = "File does not exist: " + path
                });
            }

            if (!string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Ok(new
                {
                    loaded = false,
                    error = "File is not an .rfa family file: " + path
                });
            }

            var loadOptions = new RvtMcpFamilyLoadOptions(overwriteExisting, overwriteParameterValues);

            using (var tx = new Transaction(doc, "RvtMcp: load family"))
            {
                try
                {
                    tx.Start();

                    Family family = null;
                    bool ok = false;
                    try
                    {
                        ok = doc.LoadFamily(path, loadOptions, out family);
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Ok(new
                        {
                            loaded = false,
                            error = "LoadFamily threw: " + ex.Message
                        });
                    }

                    if (!ok)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Ok(new
                        {
                            loaded = false,
                            error = "Revit refused to load the family (likely already loaded with same content, or invalid file). Pass overwrite_existing=true to force reload."
                        });
                    }

                    if (family == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Ok(new
                        {
                            loaded = false,
                            error = "Revit reported the family loaded but did not return a Family."
                        });
                    }

                    var symbolIds = new List<string>();
                    var symbolNames = new List<string>();
                    try
                    {
                        var ids = family.GetFamilySymbolIds();
                        if (ids != null)
                        {
                            foreach (var sid in ids)
                            {
                                if (sid == null) continue;
                                symbolIds.Add(RevitCompat.GetId(sid).ToString());
                                var sym = doc.GetElement(sid) as FamilySymbol;
                                symbolNames.Add(sym != null ? sym.Name : string.Empty);
                            }
                        }
                    }
                    catch
                    {
                        // Reflection fallback: some older builds may expose Symbols (ElementSet) instead.
                        try
                        {
                            var symbolsProp = family.GetType().GetProperty("Symbols");
                            if (symbolsProp != null)
                            {
                                var symEnum = symbolsProp.GetValue(family, null) as System.Collections.IEnumerable;
                                if (symEnum != null)
                                {
                                    foreach (var obj in symEnum)
                                    {
                                        var sym = obj as FamilySymbol;
                                        if (sym == null) continue;
                                        symbolIds.Add(RevitCompat.GetId(sym.Id).ToString());
                                        symbolNames.Add(sym.Name);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    string familyName = family.Name;
                    string category = family.FamilyCategory != null ? family.FamilyCategory.Name : null;

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        loaded = true,
                        family_id = RevitCompat.GetId(family.Id).ToString(),
                        family_name = familyName,
                        category = category,
                        kind = "loadable",
                        symbol_count = symbolIds.Count,
                        symbols_included = includeSymbols,
                        symbols_truncated = includeSymbols && symbolIds.Count > maxSymbolResults,
                        symbol_ids = includeSymbols ? symbolIds.Take(maxSymbolResults).ToArray() : null,
                        symbol_names = includeSymbols ? symbolNames.Take(maxSymbolResults).ToArray() : null,
                        was_overwrite = loadOptions.FamilyWasFound,
                        warnings = new string[0]
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to load family: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// IFamilyLoadOptions implementation that controls behavior when a family
        /// or shared family with the same name is already present in the document.
        /// </summary>
        private class RvtMcpFamilyLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwriteExisting;
            private readonly bool _overwriteParameterValues;

            public bool FamilyWasFound { get; private set; }

            public RvtMcpFamilyLoadOptions(bool overwriteExisting, bool overwriteParameterValues)
            {
                _overwriteExisting = overwriteExisting;
                _overwriteParameterValues = overwriteParameterValues;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                FamilyWasFound = true;
                overwriteParameterValues = _overwriteParameterValues;
                return _overwriteExisting;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                FamilyWasFound = true;
                source = FamilySource.Family;
                overwriteParameterValues = _overwriteParameterValues;
                return _overwriteExisting;
            }
        }
    }
}
