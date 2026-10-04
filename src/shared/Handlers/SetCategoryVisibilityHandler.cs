using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetCategoryVisibilityHandler : IRevitCommand
    {
        public string Name => "set_category_visibility";
        public string Description => "Show or hide one or more model categories in a view";
        public string ParametersSchema => @"{""type"":""object"",""required"":[""categories"",""hidden""],""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Category names, e.g. ['Walls','Furniture'].""},""hidden"":{""type"":""boolean"",""description"":""true = hide, false = show.""},""view_id"":{""type"":""integer"",""description"":""If omitted, active view.""}}}";

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
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var categoriesToken = request["categories"];
            if (categoriesToken == null || categoriesToken.Type != JTokenType.Array)
                return CommandResult.Fail("'categories' is required and must be an array of strings.");

            var categoryNames = categoriesToken
                .Select(t => t?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();
            if (categoryNames.Count == 0)
                return CommandResult.Fail("'categories' must contain at least one non-empty category name.");

            var hiddenToken = request["hidden"];
            if (hiddenToken == null || hiddenToken.Type == JTokenType.Null)
                return CommandResult.Fail("'hidden' is required.");
            var hidden = hiddenToken.Value<bool>();

            var viewIdRaw = request.Value<long?>("view_id");

            // Resolve the target view.
            View view;
            if (viewIdRaw.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdRaw.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdRaw.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewIdRaw.Value)) as View;
                if (view == null)
                    return Error($"View with ID {viewIdRaw.Value} not found.", null, null, hidden);
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return Error("No active view is available.", null, null, hidden);
            }

            var resolvedViewId = RevitCompat.GetId(view.Id);

            // Build a case-insensitive lookup of all document categories.
            var categoryLookup = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || string.IsNullOrEmpty(cat.Name))
                    continue;
                if (!categoryLookup.ContainsKey(cat.Name))
                    categoryLookup[cat.Name] = cat;
            }

            var succeeded = new List<string>();
            var failed = new List<object>();

            using (var tx = new Transaction(doc, "RvtMcp: set category visibility"))
            {
                tx.Start();
                try
                {
                    foreach (var name in categoryNames)
                    {
                        if (!categoryLookup.TryGetValue(name, out var category) || category == null)
                        {
                            failed.Add(new { category = name, error = "Category not found in this document." });
                            continue;
                        }

                        try
                        {
                            if (!category.get_AllowsVisibilityControl(view))
                            {
                                failed.Add(new
                                {
                                    category = category.Name,
                                    error = $"Category '{category.Name}' does not allow visibility control in view '{view.Name}'."
                                });
                                continue;
                            }

                            view.SetCategoryHidden(category.Id, hidden);
                            succeeded.Add(category.Name);
                        }
                        catch (Exception exCat)
                        {
                            failed.Add(new { category = category.Name, error = exCat.Message });
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        updated = true,
                        view_id = resolvedViewId,
                        view_name = view.Name,
                        hidden = hidden,
                        succeeded = succeeded,
                        failed = failed,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return Error($"Failed to set category visibility: {ex.Message}",
                        resolvedViewId, view.Name, hidden);
                }
            }
        }

        private static CommandResult Error(string message, long? viewId, string viewName, bool hidden)
        {
            return CommandResult.Ok(new
            {
                updated = false,
                view_id = viewId,
                view_name = viewName,
                hidden = hidden,
                succeeded = new List<string>(),
                failed = new List<object>(),
                error = message
            });
        }
    }
}
