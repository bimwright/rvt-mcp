using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class RemoveParameterBindingHandler : IRevitCommand
    {
        public string Name => "remove_parameter_binding";
        public string Description => "Remove a parameter binding or individual bound categories from a parameter in the document.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""guid"": { ""type"": ""string"" },
    ""categories"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" },
      ""description"": ""Optional subset of bound categories to remove.""
    },
    ""removeAllCategories"": { ""type"": ""boolean"", ""default"": false },
    ""dryRun"": { ""type"": ""boolean"", ""default"": true }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var name = request.Value<string>("name");
            var guidInput = request.Value<string>("guid");
            var categoriesInput = request["categories"]?.ToObject<string[]>();
            var removeAllCategories = request.Value<bool?>("removeAllCategories") ?? false;
            var dryRun = request.Value<bool?>("dryRun") ?? true;

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(guidInput))
                return CommandResult.Fail("Either name or guid is required.");

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guidInput))
                return CommandResult.Fail("Only one of name or guid can be specified, not both.");

            Guid guidObj = Guid.Empty;
            if (!string.IsNullOrEmpty(guidInput))
            {
                if (!Guid.TryParse(guidInput, out guidObj))
                    return CommandResult.Fail("Invalid GUID format.");
            }

            if (!removeAllCategories && (categoriesInput == null || categoriesInput.Length == 0))
                return CommandResult.Fail("categories must be supplied when removeAllCategories is false.");

            Definition targetDef = null;
            Binding targetBinding = null;

            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                var definition = iterator.Key;
                var binding = iterator.Current as Binding;
                if (definition == null || binding == null) continue;

                if (guidObj != Guid.Empty)
                {
                    if (SharedParameterGuid.Of(doc, definition) == guidObj)
                    {
                        targetDef = definition;
                        targetBinding = binding;
                        break;
                    }
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (targetDef != null)
                        {
                            return CommandResult.Fail($"Ambiguous parameter name '{name}'. Multiple bindings found. Use guid.");
                        }
                        targetDef = definition;
                        targetBinding = binding;
                    }
                }
            }

            if (targetDef == null)
                return CommandResult.Fail("Parameter binding not found in the document.");

            var eb = targetBinding as ElementBinding;
            if (eb == null)
                return CommandResult.Fail("Target binding is not an ElementBinding.");

            var currentCats = new List<Category>();
            var currentCatIds = new HashSet<long>();
            if (eb.Categories != null)
            {
                foreach (Category cat in eb.Categories)
                {
                    if (cat != null)
                    {
                        currentCats.Add(cat);
                        currentCatIds.Add(RevitCompat.GetId(cat.Id));
                    }
                }
            }

            var removedCategoriesList = new List<Category>();
            var remainingCategoriesList = new List<Category>();

            if (removeAllCategories)
            {
                removedCategoriesList.AddRange(currentCats);
            }
            else
            {
                var targetCatsToRemove = new List<Category>();
                foreach (var catInput in categoriesInput)
                {
                    var resolved = ResolveCategory(doc, catInput);
                    if (resolved == null)
                    {
                        return CommandResult.Fail($"Category '{catInput}' could not be resolved.");
                    }
                    var catId = RevitCompat.GetId(resolved.Id);
                    if (!currentCatIds.Contains(catId))
                    {
                        return CommandResult.Fail($"Category '{catInput}' is not bound to this parameter.");
                    }
                    targetCatsToRemove.Add(resolved);
                }

                foreach (var cat in currentCats)
                {
                    var catId = RevitCompat.GetId(cat.Id);
                    if (targetCatsToRemove.Any(rc => RevitCompat.GetId(rc.Id) == catId))
                    {
                        removedCategoriesList.Add(cat);
                    }
                    else
                    {
                        remainingCategoriesList.Add(cat);
                    }
                }

                if (remainingCategoriesList.Count == 0)
                {
                    return CommandResult.Fail("Removing all bound categories is not allowed via categories subset. Set removeAllCategories to true to remove the entire binding.");
                }
            }

            bool wouldRemoveBinding = false;
            bool removedBinding = false;
            bool rebuiltBinding = false;

            if (dryRun)
            {
                wouldRemoveBinding = removeAllCategories;
                rebuiltBinding = !removeAllCategories;
            }
            else
            {
                using (var tx = new Transaction(doc, "RvtMcp: remove parameter binding"))
                {
                    tx.Start();

                    if (removeAllCategories)
                    {
                        bool success = doc.ParameterBindings.Remove(targetDef);
                        if (!success)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("Failed to remove parameter binding from the document.");
                        }
                        removedBinding = true;
                    }
                    else
                    {
                        var replacementCategorySet = app.Application.Create.NewCategorySet();
                        foreach (var cat in remainingCategoriesList)
                        {
                            replacementCategorySet.Insert(cat);
                        }

                        Binding replacementBinding = null;
                        if (targetBinding is TypeBinding)
                        {
                            replacementBinding = app.Application.Create.NewTypeBinding(replacementCategorySet);
                        }
                        else
                        {
                            replacementBinding = app.Application.Create.NewInstanceBinding(replacementCategorySet);
                        }

                        var groupTypeId = GroupTypeId.Data;
                        try { groupTypeId = targetDef.GetGroupTypeId(); } catch { }

                        bool success = doc.ParameterBindings.ReInsert(targetDef, replacementBinding, groupTypeId);
                        if (!success)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("Failed to re-insert the parameter binding.");
                        }
                        rebuiltBinding = true;
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit status: {status}");
                    }
                }
            }

            var removedCatsDto = removedCategoriesList.Select(c => new
            {
                id = RevitCompat.GetId(c.Id),
                name = c.Name
            }).OrderBy(c => c.name).ToArray();

            var remainingCatsDto = remainingCategoriesList.Select(c => new
            {
                id = RevitCompat.GetId(c.Id),
                name = c.Name
            }).OrderBy(c => c.name).ToArray();

            string bindingKind = targetBinding is TypeBinding ? "type" : "instance";

            return CommandResult.Ok(new
            {
                dryRun,
                wouldRemoveBinding,
                removedBinding,
                rebuiltBinding,
                name = targetDef.Name,
                guid = SharedParameterGuid.Of(doc, targetDef)?.ToString("d"),
                bindingKind,
                removedCategories = removedCatsDto,
                remainingCategories = remainingCatsDto,
                warnings = Array.Empty<string>()
            });
        }

        private static Category ResolveCategory(Document doc, string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            if (input.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<BuiltInCategory>(input, true, out var bic))
                {
                    try
                    {
                        return Category.GetCategory(doc, bic);
                    }
                    catch { }
                }
            }

            foreach (Category cat in doc.Settings.Categories)
            {
                if (string.Equals(cat.Name, input, StringComparison.OrdinalIgnoreCase))
                {
                    return cat;
                }
            }

            return null;
        }
    }
}
