using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class RenameFamilyTypeHandler : IRevitCommand
    {
        public string Name => "rename_family_type";
        public string Description => "Rename a FamilySymbol (type) or system family type by ElementType.Name = newName. Returns the old/new name and the family id.";
        public string ParametersSchema => @"{""type"":""object"",""required"":[""type_id"",""new_name""],""properties"":{""type_id"":{""type"":""string"",""description"":""ElementId string of the FamilySymbol or system type to rename.""},""new_name"":{""type"":""string"",""description"":""New name. Must be unique within the family.""}}}";

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

            var typeIdToken = request["type_id"];
            if (typeIdToken == null || typeIdToken.Type == JTokenType.Null)
                return CommandResult.Fail("type_id is required.");

            var typeIdRaw = typeIdToken.Type == JTokenType.String
                ? typeIdToken.Value<string>()
                : typeIdToken.ToString(Formatting.None).Trim('"');

            if (string.IsNullOrWhiteSpace(typeIdRaw))
                return CommandResult.Fail("type_id is required.");

            if (!long.TryParse(typeIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var typeIdValue))
                return CommandResult.Fail("type_id must be a numeric ElementId string. Got: " + typeIdRaw);

            if (!RevitCompat.CanRepresentElementId(typeIdValue))
                return CommandResult.Fail("type_id " + RevitCompat.ElementIdRangeError(typeIdValue));

            var newNameToken = request["new_name"];
            if (newNameToken == null || newNameToken.Type == JTokenType.Null)
                return CommandResult.Fail("new_name is required.");

            var newName = newNameToken.Type == JTokenType.String
                ? newNameToken.Value<string>()
                : newNameToken.ToString(Formatting.None).Trim('"');

            if (string.IsNullOrWhiteSpace(newName))
                return CommandResult.Fail("new_name must be a non-empty string.");

            var type = doc.GetElement(RevitCompat.ToElementId(typeIdValue)) as ElementType;
            if (type == null)
                return BuildErrorResult(typeIdRaw, null, newName, null, null, "ElementType with ID " + typeIdValue.ToString(CultureInfo.InvariantCulture) + " not found.");

            var oldName = SafeName(type);
            var familyName = SafeFamilyName(type);
            var categoryName = SafeCategoryName(type);

            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return CommandResult.Ok(new
                {
                    renamed = false,
                    type_id = typeIdValue.ToString(CultureInfo.InvariantCulture),
                    old_name = oldName,
                    new_name = newName,
                    family_name = familyName,
                    category = categoryName,
                    error = (string)null
                });
            }

            using (var tx = new Transaction(doc, "RvtMcp: rename type"))
            {
                TransactionStatus status;
                try
                {
                    tx.Start();
                    type.Name = newName;
                    status = tx.Commit();
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    SafeRollback(tx);
                    return BuildErrorResult(
                        typeIdValue.ToString(CultureInfo.InvariantCulture),
                        oldName,
                        newName,
                        familyName,
                        categoryName,
                        "Revit rejected the new name (likely duplicate or invalid characters): " + ex.Message);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    SafeRollback(tx);
                    return BuildErrorResult(
                        typeIdValue.ToString(CultureInfo.InvariantCulture),
                        oldName,
                        newName,
                        familyName,
                        categoryName,
                        "Revit could not rename this type: " + ex.Message);
                }
                catch (Exception ex)
                {
                    SafeRollback(tx);
                    return BuildErrorResult(
                        typeIdValue.ToString(CultureInfo.InvariantCulture),
                        oldName,
                        newName,
                        familyName,
                        categoryName,
                        "Failed to rename type: " + ex.Message);
                }

                if (status != TransactionStatus.Committed)
                {
                    return BuildErrorResult(
                        typeIdValue.ToString(CultureInfo.InvariantCulture),
                        oldName,
                        newName,
                        familyName,
                        categoryName,
                        "Revit did not commit the rename transaction. Status: " + status);
                }
            }

            var refreshed = doc.GetElement(RevitCompat.ToElementId(typeIdValue)) as ElementType;
            var finalName = refreshed != null ? SafeName(refreshed) : newName;
            var finalFamilyName = refreshed != null ? SafeFamilyName(refreshed) : familyName;
            var finalCategory = refreshed != null ? SafeCategoryName(refreshed) : categoryName;

            return CommandResult.Ok(new
            {
                renamed = true,
                type_id = typeIdValue.ToString(CultureInfo.InvariantCulture),
                old_name = oldName,
                new_name = finalName,
                family_name = finalFamilyName,
                category = finalCategory,
                error = (string)null
            });
        }

        private static CommandResult BuildErrorResult(
            string typeId,
            string oldName,
            string newName,
            string familyName,
            string categoryName,
            string error)
        {
            return CommandResult.Ok(new
            {
                renamed = false,
                type_id = typeId,
                old_name = oldName,
                new_name = newName,
                family_name = familyName,
                category = categoryName,
                error
            });
        }

        private static void SafeRollback(Transaction tx)
        {
            try
            {
                if (tx != null && tx.HasStarted() && !tx.HasEnded())
                    tx.RollBack();
            }
            catch
            {
                // ignore
            }
        }

        private static string SafeName(Element element)
        {
            if (element == null)
                return null;
            try
            {
                return element.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeFamilyName(ElementType type)
        {
            if (type == null)
                return null;
            try
            {
                var familySymbol = type as FamilySymbol;
                if (familySymbol != null)
                    return familySymbol.FamilyName;
                return type.FamilyName;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeCategoryName(ElementType type)
        {
            if (type == null)
                return null;
            try
            {
                return type.Category?.Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
