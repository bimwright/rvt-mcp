using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetElementRelationshipsHandler : IRevitCommand
    {
        public string Name => "get_element_relationships";
        public string Description => "Get type, grouping, assembly, view, host, family component, dependent, and design option relationships for elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""includeDependents"":{""type"":""boolean"",""default"":true}},""required"":[""elementIds""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var elementIds = request["elementIds"]?.ToObject<long[]>() ?? new long[0];
            var includeDependents = request.Value<bool?>("includeDependents") ?? true;

            if (elementIds.Length == 0)
                return CommandResult.Fail("elementIds array is required.");

            var elements = new JArray();
            foreach (var rawId in elementIds)
            {
                var id = RevitCompat.ToElementId(rawId);
                var element = doc.GetElement(id);
                if (element == null)
                {
                    elements.Add(new JObject
                    {
                        ["id"] = rawId,
                        ["found"] = false,
                        ["error"] = "Element not found."
                    });
                    continue;
                }

                elements.Add(BuildElementRelationship(doc, element, includeDependents));
            }

            return CommandResult.Ok(new
            {
                count = elements.Count,
                includeDependents,
                elements
            });
        }

        private static JObject BuildElementRelationship(Document doc, Element element, bool includeDependents)
        {
            var result = new JObject
            {
                ["id"] = RevitCompat.GetId(element.Id),
                ["found"] = true,
                ["name"] = element.Name,
                ["category"] = element.Category?.Name
            };

            AddType(doc, element, result);
            AddGroup(doc, element, result);
            AddAssembly(doc, element, result);
            AddOwnerView(doc, element, result);
            AddHost(element, result);
            AddFamilyComponents(element, result);
            AddDesignOption(element, result);

            if (includeDependents)
                result["dependentElementIds"] = ToIdArray(element.GetDependentElements(null));

            return result;
        }

        private static void AddType(Document doc, Element element, JObject result)
        {
            var typeId = element.GetTypeId();
            if (!IsValidId(typeId))
                return;

            var typeElement = doc.GetElement(typeId);
            result["typeId"] = RevitCompat.GetId(typeId);
            result["typeName"] = typeElement?.Name;
        }

        private static void AddGroup(Document doc, Element element, JObject result)
        {
            var groupId = element.GroupId;
            if (!IsValidId(groupId))
                return;

            var group = doc.GetElement(groupId) as Group;
            result["groupId"] = RevitCompat.GetId(groupId);
            result["groupName"] = group?.Name;

            var groupType = group?.GroupType;
            if (groupType != null)
            {
                result["groupTypeId"] = RevitCompat.GetId(groupType.Id);
                result["groupTypeName"] = groupType.Name;
            }
        }

        private static void AddAssembly(Document doc, Element element, JObject result)
        {
            var assemblyInstanceId = element.AssemblyInstanceId;
            if (!IsValidId(assemblyInstanceId))
                return;

            var assemblyInstance = doc.GetElement(assemblyInstanceId);
            result["assemblyInstanceId"] = RevitCompat.GetId(assemblyInstanceId);
            result["assemblyInstanceName"] = assemblyInstance?.Name;
        }

        private static void AddOwnerView(Document doc, Element element, JObject result)
        {
            var ownerViewId = element.OwnerViewId;
            if (!IsValidId(ownerViewId))
                return;

            var ownerView = doc.GetElement(ownerViewId);
            result["ownerViewId"] = RevitCompat.GetId(ownerViewId);
            result["ownerViewName"] = ownerView?.Name;
        }

        private static void AddHost(Element element, JObject result)
        {
            var host = GetHostElement(element);
            if (host == null)
                return;

            result["hostId"] = RevitCompat.GetId(host.Id);
            result["hostName"] = host.Name;
        }

        private static Element GetHostElement(Element element)
        {
            var familyInstance = element as FamilyInstance;
            if (familyInstance?.Host != null)
                return familyInstance.Host;

            var hostProperty = element.GetType().GetProperty("Host", BindingFlags.Instance | BindingFlags.Public);
            if (hostProperty == null || !typeof(Element).IsAssignableFrom(hostProperty.PropertyType))
                return null;

            try
            {
                return hostProperty.GetValue(element, null) as Element;
            }
            catch
            {
                return null;
            }
        }

        private static void AddFamilyComponents(Element element, JObject result)
        {
            var familyInstance = element as FamilyInstance;
            if (familyInstance == null)
                return;

            var superComponent = familyInstance.SuperComponent;
            if (superComponent != null)
            {
                result["superComponentId"] = RevitCompat.GetId(superComponent.Id);
                result["superComponentName"] = superComponent.Name;
            }

            result["subComponentIds"] = ToIdArray(familyInstance.GetSubComponentIds());
        }

        private static void AddDesignOption(Element element, JObject result)
        {
            var designOption = element.DesignOption;
            if (designOption == null)
                return;

            result["designOptionId"] = RevitCompat.GetId(designOption.Id);
            result["designOptionName"] = designOption.Name;
        }

        private static bool IsValidId(ElementId id)
        {
            return id != null && RevitCompat.GetId(id) != RevitCompat.GetId(ElementId.InvalidElementId);
        }

        private static JArray ToIdArray(IEnumerable<ElementId> ids)
        {
            var array = new JArray();
            foreach (var id in ids ?? Enumerable.Empty<ElementId>())
            {
                if (IsValidId(id))
                    array.Add(RevitCompat.GetId(id));
            }

            return array;
        }
    }
}
