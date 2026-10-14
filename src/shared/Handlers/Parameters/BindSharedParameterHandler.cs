using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class BindSharedParameterHandler : IRevitCommand
    {
        public string Name => "bind_shared_parameter";
        public string Description => "Bind a shared parameter to document categories.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""guid"", ""categories""],
  ""properties"": {
    ""guid"": { ""type"": ""string"" },
    ""categories"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" },
      ""description"": ""Category display names or BuiltInCategory tokens such as OST_Doors.""
    },
    ""bindingKind"": {
      ""type"": ""string"",
      ""enum"": [""instance"", ""type""],
      ""default"": ""instance""
    },
    ""parameterGroupId"": {
      ""type"": ""string"",
      ""description"": ""Built-in parameter group ForgeTypeId, e.g. autodesk.parameter.group:data-1.0.0. Omit for Data.""
    },
    ""sharedParameterFilePath"": { ""type"": ""string"" },
    ""allowRebind"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""If true, merges category changes into an existing binding with ReInsert.""
    }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var guidInput = request.Value<string>("guid");
            var categoriesInput = request["categories"]?.ToObject<string[]>();
            var bindingKind = request.Value<string>("bindingKind") ?? "instance";
            var parameterGroupInput = request.Value<string>("parameterGroupId");
            var customPath = request.Value<string>("sharedParameterFilePath");
            var allowRebind = request.Value<bool?>("allowRebind") ?? false;

            if (string.IsNullOrWhiteSpace(guidInput) || !Guid.TryParse(guidInput, out var guidObj))
                return CommandResult.Fail("A valid guid is required.");

            if (categoriesInput == null || categoriesInput.Length == 0)
                return CommandResult.Fail("categories is required and cannot be empty.");

            if (!string.Equals(bindingKind, "instance", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(bindingKind, "type", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("bindingKind must be 'instance' or 'type'.");
            }

            var groupTypeId = string.IsNullOrWhiteSpace(parameterGroupInput)
                ? GroupTypeId.Data
                : new ForgeTypeId(parameterGroupInput.Trim());
            if (!ParameterUtils.IsBuiltInGroup(groupTypeId))
                return CommandResult.Fail($"parameterGroupId '{parameterGroupInput}' is not a built-in parameter group. Omit it for Data ({GroupTypeId.Data.TypeId}).");

            if (!string.IsNullOrEmpty(customPath) && !Path.IsPathRooted(customPath))
                return CommandResult.Fail("sharedParameterFilePath must be an absolute path.");

            string resolvedPath = customPath;
            if (string.IsNullOrEmpty(resolvedPath))
            {
                resolvedPath = app.Application.SharedParametersFilename;
            }

            if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
            {
                return CommandResult.Fail("Shared parameters file does not exist or is not set.");
            }

            string originalFilename = app.Application.SharedParametersFilename;
            DefinitionFile defFile = null;
            try
            {
                if (!string.IsNullOrEmpty(customPath))
                {
                    app.Application.SharedParametersFilename = customPath;
                }
                defFile = app.Application.OpenSharedParameterFile();
            }
            finally
            {
                if (!string.IsNullOrEmpty(customPath))
                {
                    app.Application.SharedParametersFilename = originalFilename;
                }
            }

            if (defFile == null)
                return CommandResult.Fail("Could not load the shared parameters file.");

            ExternalDefinition externalDefinition = null;
            foreach (DefinitionGroup group in defFile.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is ExternalDefinition extDef && extDef.GUID == guidObj)
                    {
                        externalDefinition = extDef;
                        break;
                    }
                }
                if (externalDefinition != null) break;
            }

            if (externalDefinition == null)
                return CommandResult.Fail($"Shared parameter GUID {guidInput} was not found in the shared parameters file.");

            var resolvedCategories = new List<Category>();
            foreach (var catInput in categoriesInput)
            {
                var cat = ResolveCategory(doc, catInput);
                if (cat == null)
                {
                    return CommandResult.Fail($"Category '{catInput}' could not be resolved.");
                }
                resolvedCategories.Add(cat);
            }

            var categorySet = app.Application.Create.NewCategorySet();
            foreach (var cat in resolvedCategories)
            {
                categorySet.Insert(cat);
            }

            SharedParameterElement paramElement = null;
            var collector = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement));
            foreach (SharedParameterElement spe in collector)
            {
                if (spe.GuidValue == guidObj)
                {
                    paramElement = spe;
                    break;
                }
            }

            bool createdSharedParameterElement = false;
            bool alreadyBound = false;
            bool rebuiltBinding = false;
            var warnings = new List<string>();

            using (var tx = new Transaction(doc, "RvtMcp: bind shared parameter"))
            {
                tx.Start();

                if (paramElement == null)
                {
                    paramElement = SharedParameterElement.Create(doc, externalDefinition);
                    createdSharedParameterElement = true;
                }

                Binding existingBinding = null;
                Definition existingDef = null;
                var iterator = doc.ParameterBindings.ForwardIterator();
                iterator.Reset();
                while (iterator.MoveNext())
                {
                    if (SharedParameterGuid.Of(doc, iterator.Key) == guidObj)
                    {
                        existingDef = iterator.Key;
                        existingBinding = iterator.Current as Binding;
                        break;
                    }
                }

                if (existingBinding != null)
                {
                    var existingKind = existingBinding is TypeBinding ? "type" : "instance";
                    var isKindMatch = string.Equals(existingKind, bindingKind, StringComparison.OrdinalIgnoreCase);

                    var existingCats = new HashSet<long>();
                    if (existingBinding is ElementBinding eb && eb.Categories != null)
                    {
                        foreach (Category c in eb.Categories)
                        {
                            if (c != null) existingCats.Add(RevitCompat.GetId(c.Id));
                        }
                    }

                    var requestedCats = resolvedCategories.Select(c => RevitCompat.GetId(c.Id)).ToList();
                    var hasAllCats = requestedCats.All(id => existingCats.Contains(id));

                    if (isKindMatch && hasAllCats && existingCats.Count == requestedCats.Count)
                    {
                        tx.RollBack();
                        alreadyBound = true;
                    }
                    else
                    {
                        if (!allowRebind)
                        {
                            tx.RollBack();
                            return CommandResult.Fail($"Parameter with GUID {guidInput} is already bound in the document. Set allowRebind=true to merge categories or change binding kind.");
                        }

                        if (!isKindMatch)
                        {
                            warnings.Add($"Changing binding kind from {existingKind} to {bindingKind} may affect existing parameter values.");
                        }

                        var mergedCategorySet = app.Application.Create.NewCategorySet();
                        var allCats = new HashSet<long>(existingCats);
                        foreach (var id in requestedCats) allCats.Add(id);

                        foreach (var catId in allCats)
                        {
                            var cat = Category.GetCategory(doc, RevitCompat.ToElementId(catId));
                            if (cat != null) mergedCategorySet.Insert(cat);
                        }

                        Binding newBinding = null;
                        if (string.Equals(bindingKind, "type", StringComparison.OrdinalIgnoreCase))
                        {
                            newBinding = app.Application.Create.NewTypeBinding(mergedCategorySet);
                        }
                        else
                        {
                            newBinding = app.Application.Create.NewInstanceBinding(mergedCategorySet);
                        }

                        bool success = doc.ParameterBindings.ReInsert(existingDef ?? externalDefinition, newBinding, groupTypeId);
                        if (!success)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("Failed to rebind the shared parameter.");
                        }

                        rebuiltBinding = true;
                    }
                }
                else
                {
                    Binding newBinding = null;
                    if (string.Equals(bindingKind, "type", StringComparison.OrdinalIgnoreCase))
                    {
                        newBinding = app.Application.Create.NewTypeBinding(categorySet);
                    }
                    else
                    {
                        newBinding = app.Application.Create.NewInstanceBinding(categorySet);
                    }

                    bool success = doc.ParameterBindings.Insert(externalDefinition, newBinding, groupTypeId);
                    if (!success)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("Failed to bind the shared parameter.");
                    }
                }

                if (tx.GetStatus() == TransactionStatus.Started)
                {
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit status: {status}");
                    }
                }
            }

            var catsResponse = resolvedCategories.Select(c => new
            {
                id = RevitCompat.GetId(c.Id),
                name = c.Name
            }).OrderBy(c => c.name).ToArray();

            return CommandResult.Ok(new
            {
                bound = true,
                createdSharedParameterElement,
                alreadyBound,
                rebuiltBinding,
                name = externalDefinition.Name,
                guid = guidObj.ToString("d"),
                bindingKind,
                parameterGroupId = groupTypeId.TypeId,
                categoryCount = catsResponse.Length,
                categories = catsResponse,
                warnings = warnings.ToArray()
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
