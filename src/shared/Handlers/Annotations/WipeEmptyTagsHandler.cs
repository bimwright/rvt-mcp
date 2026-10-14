using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class WipeEmptyTagsHandler : IRevitCommand
    {
        public string Name => "wipe_empty_tags";
        public string Description => "Find and delete empty tags in a view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""view_id"": { ""type"": ""integer"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""maximum"": 500 }
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

            long? viewId = request.Value<long?>("view_id");
            // wipe_empty_tags defaults to dry_run = true
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            int limit = request["limit"] == null ? 200 : request.Value<int>("limit");

            if (limit > 500)
                return CommandResult.Fail("limit cannot exceed 500.");

            // Resolve View
            View view = null;
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value))
                    return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                if (view == null)
                    return CommandResult.Fail("View ID " + viewId.Value + " does not resolve to a View.");
            }
            else
            {
                view = doc.ActiveView;
            }

            if (view == null)
                return CommandResult.Fail("No target view could be resolved.");

            if (!EnsureViewCanHostAnnotation(view, out var viewError))
                return CommandResult.Fail(viewError);

            // Collect IndependentTags in the view
            var scannedTags = new List<IndependentTag>();
            try
            {
                scannedTags = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to collect tags in view: " + ex.Message);
            }

            var emptyTags = new List<IndependentTag>();
            var wouldDelete = new JArray();
            var failed = new JArray();

            foreach (var tag in scannedTags)
            {
                try
                {
                    string tagText = tag.TagText;
                    if (string.IsNullOrWhiteSpace(tagText))
                    {
                        emptyTags.Add(tag);

                        var taggedIds = GetTaggedElementIds(tag);
                        wouldDelete.Add(new JObject
                        {
                            ["tag_id"] = RevitCompat.GetId(tag.Id),
                            ["tag_text"] = "",
                            ["owner_view_id"] = RevitCompat.GetId(tag.OwnerViewId),
                            ["tagged_element_ids"] = JArray.FromObject(taggedIds.ToList())
                        });
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject
                    {
                        ["tag_id"] = RevitCompat.GetId(tag.Id),
                        ["error"] = "Failed to inspect TagText: " + ex.Message
                    });
                }
            }

            bool truncated = emptyTags.Count > limit;
            if (truncated)
            {
                emptyTags = emptyTags.Take(limit).ToList();
                // Slice wouldDelete
                var tempArray = new JArray();
                for (int i = 0; i < limit; i++)
                {
                    tempArray.Add(wouldDelete[i]);
                }
                wouldDelete = tempArray;
            }

            var deletedIds = new JArray();
            int deletedCount = 0;

            if (!dryRun && emptyTags.Count > 0)
            {
                using (var tx = new Transaction(doc, "RvtMcp: wipe empty tags"))
                {
                    tx.Start();
                    try
                    {
                        foreach (var tag in emptyTags)
                        {
                            try
                            {
                                doc.Delete(tag.Id);
                                deletedCount++;
                                deletedIds.Add(RevitCompat.GetId(tag.Id));
                            }
                            catch (Exception ex)
                            {
                                failed.Add(new JObject
                                {
                                    ["tag_id"] = RevitCompat.GetId(tag.Id),
                                    ["error"] = ex.Message
                                });
                            }
                        }
                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                            return CommandResult.Fail("Wipe empty tags transaction did not commit. Status: " + status);
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Failed to commit deletion of empty tags: " + ex.Message);
                    }
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = dryRun,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["scanned"] = scannedTags.Count,
                ["empty_count"] = emptyTags.Count,
                ["deleted"] = deletedCount,
                ["would_delete"] = wouldDelete,
                ["deleted_ids"] = deletedIds,
                ["failed"] = failed,
                ["truncated"] = truncated,
                ["error"] = null
            });
        }

        private static bool EnsureViewCanHostAnnotation(View view, out string error)
        {
            error = null;
            if (view == null)
            {
                error = "Target view is null.";
                return false;
            }
            if (view.IsTemplate)
            {
                error = "Cannot add annotations to a view template.";
                return false;
            }
            var vt = view.ViewType;
            if (vt == ViewType.Schedule || vt == ViewType.ColumnSchedule || vt == ViewType.ProjectBrowser || vt == ViewType.SystemBrowser || vt == ViewType.Legend)
            {
                error = "View type '" + vt + "' does not support annotations.";
                return false;
            }
            return true;
        }

        private static HashSet<long> GetTaggedElementIds(IndependentTag tag)
        {
            var ids = new HashSet<long>();
            if (tag == null) return ids;

            // 1. Try GetTaggedElementIds() (Revit 2022+)
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedElementIds");
                if (method != null)
                {
                    var refs = method.Invoke(tag, null) as System.Collections.IEnumerable;
                    if (refs != null)
                    {
                        foreach (var linkId in refs)
                        {
                            var hostIdProp = linkId.GetType().GetProperty("HostElementId");
                            if (hostIdProp != null)
                            {
                                var hostId = hostIdProp.GetValue(linkId) as ElementId;
                                if (hostId != null && hostId != ElementId.InvalidElementId)
                                {
                                    ids.Add(RevitCompat.GetId(hostId));
                                }
                            }
                        }
                        if (ids.Count > 0) return ids;
                    }
                }
            }
            catch { }

            // 2. Try TaggedLocalElementId (Revit 2021 and older)
            try
            {
                var prop = tag.GetType().GetProperty("TaggedLocalElementId");
                if (prop != null)
                {
                    var val = prop.GetValue(tag) as ElementId;
                    if (val != null && val != ElementId.InvalidElementId)
                    {
                        ids.Add(RevitCompat.GetId(val));
                        return ids;
                    }
                }
            }
            catch { }

            // 3. Fallback: Try GetTaggedLocalElementIds() or direct fields
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedLocalElementIds");
                if (method != null)
                {
                    var localIds = method.Invoke(tag, null) as ICollection<ElementId>;
                    if (localIds != null)
                    {
                        foreach (var lid in localIds)
                        {
                            if (lid != ElementId.InvalidElementId)
                                ids.Add(RevitCompat.GetId(lid));
                        }
                        return ids;
                    }
                }
            }
            catch { }

            return ids;
        }
    }
}
