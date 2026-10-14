using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListKeynotesHandler : IRevitCommand
    {
        public string Name => "list_keynotes";
        public string Description => "List keynotes from the project keynote table or model references.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""key_prefix"": { ""type"": ""string"" },
    ""search"": { ""type"": ""string"" },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""maximum"": 1000 }
  }
}";

        private class KeynoteData
        {
            public string Key { get; set; }
            public string Text { get; set; }
            public string ParentKey { get; set; }
        }

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

            var keyPrefix = request.Value<string>("key_prefix") ?? "";
            var search = request.Value<string>("search") ?? "";
            var limit = request["limit"] == null ? 200 : request.Value<int>("limit");

            if (limit > 1000)
                return CommandResult.Fail("limit cannot exceed 1000.");

            var keynoteList = new List<KeynoteData>();
            string source = "keynote_table";
            long? keynoteTableId = null;
            var warnings = new List<string>();

            // Try direct KeynoteTable traversal
            try
            {
                var table = KeynoteTable.GetKeynoteTable(doc);
                if (table != null)
                {
                    keynoteTableId = RevitCompat.GetId(table.Id);

                    // Retrieve external resource reference for keynote table
                    ExternalResourceReference extRef = null;
                    try
                    {
                        extRef = table.GetExternalResourceReference(ExternalResourceTypes.BuiltInExternalResourceTypes.KeynoteTable);
                    }
                    catch { }

                    if (extRef != null && !string.IsNullOrWhiteSpace(extRef.InSessionPath))
                    {
                        var rawPath = extRef.InSessionPath;
                        var resolvedPath = rawPath;

                        // Check if file exists, else try relative path
                        if (!System.IO.File.Exists(resolvedPath) && !string.IsNullOrWhiteSpace(doc.PathName))
                        {
                            try
                            {
                                var docDir = System.IO.Path.GetDirectoryName(doc.PathName);
                                var fileName = System.IO.Path.GetFileName(rawPath);
                                var altPath = System.IO.Path.Combine(docDir, fileName);
                                if (System.IO.File.Exists(altPath))
                                {
                                    resolvedPath = altPath;
                                }
                            }
                            catch { }
                        }

                        if (System.IO.File.Exists(resolvedPath))
                        {
                            var lines = System.IO.File.ReadAllLines(resolvedPath);
                            foreach (var line in lines)
                            {
                                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith(";"))
                                    continue;

                                var parts = line.Split('\t');
                                if (parts.Length >= 2)
                                {
                                    keynoteList.Add(new KeynoteData
                                    {
                                        Key = parts[0].Trim(),
                                        Text = parts[1].Trim(),
                                        ParentKey = parts.Length >= 3 ? parts[2].Trim() : ""
                                    });
                                }
                            }
                        }
                        else
                        {
                            warnings.Add("Keynote table file not found locally: " + rawPath);
                        }
                    }
                    else
                    {
                        warnings.Add("Could not resolve keynote external resource path.");
                    }
                }
                else
                {
                    warnings.Add("No active keynote table is loaded in the document.");
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Direct KeynoteTable access is limited or threw an exception: " + ex.Message);
            }

            // Fallback if no table found/loaded
            if (keynoteList.Count == 0)
            {
                source = "limited_project_keynote_references";
                try
                {
                    var keynotesFound = new HashSet<string>();
                    var symbols = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .ToList();

                    foreach (var sym in symbols)
                    {
                        var p = sym.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
                        if (p != null && p.HasValue && !string.IsNullOrWhiteSpace(p.AsString()))
                        {
                            var val = p.AsString().Trim();
                            if (keynotesFound.Add(val))
                            {
                                keynoteList.Add(new KeynoteData
                                {
                                    Key = val,
                                    Text = sym.Name,
                                    ParentKey = ""
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("Fallback keynote collection failed: " + ex.Message);
                }
            }

            // Map parents for path construction
            var parentMap = keynoteList.ToDictionary(k => k.Key, k => k.ParentKey);

            // Filter Keynotes
            if (!string.IsNullOrEmpty(keyPrefix))
            {
                keynoteList = keynoteList.Where(k => k.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrEmpty(search))
            {
                keynoteList = keynoteList.Where(k =>
                    k.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    k.Text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                ).ToList();
            }

            bool truncated = keynoteList.Count > limit;
            var finalKeynotes = keynoteList.Take(limit).ToList();

            var keynotesArray = new JArray();
            foreach (var kn in finalKeynotes)
            {
                var fullPath = GetFullPath(kn.Key, parentMap);
                keynotesArray.Add(new JObject
                {
                    ["key"] = kn.Key,
                    ["text"] = kn.Text,
                    ["parent_key"] = kn.ParentKey,
                    ["full_path"] = JArray.FromObject(fullPath)
                });
            }

            return CommandResult.Ok(new JObject
            {
                ["source"] = source,
                ["keynote_table_id"] = keynoteTableId.HasValue ? (JToken)keynoteTableId.Value : JValue.CreateNull(),
                ["returned"] = finalKeynotes.Count,
                ["limit"] = limit,
                ["truncated"] = truncated,
                ["keynotes"] = keynotesArray,
                ["warnings"] = JArray.FromObject(warnings),
                ["error"] = null
            });
        }

        private static List<string> GetFullPath(string key, Dictionary<string, string> childToParentMap)
        {
            var path = new List<string>();
            var current = key;
            int safetyLimit = 15; // prevent infinite loops
            while (!string.IsNullOrEmpty(current) && safetyLimit-- > 0)
            {
                path.Insert(0, current);
                if (childToParentMap.TryGetValue(current, out var parent) && !string.IsNullOrEmpty(parent) && parent != current)
                {
                    current = parent;
                }
                else
                {
                    break;
                }
            }
            return path;
        }
    }
}
