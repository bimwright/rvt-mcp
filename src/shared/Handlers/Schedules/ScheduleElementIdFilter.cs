using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    internal static class ScheduleElementIdFilter
    {
        public static ElementId Resolve(Document doc, ViewSchedule schedule, ScheduleFieldId fieldId, JToken value)
        {
            var field = schedule.Definition.GetField(fieldId);
            if (field.ParameterId == ElementId.InvalidElementId) return null;
            var parameterId = RevitCompat.GetId(field.ParameterId);
            var parameterElement = doc.GetElement(field.ParameterId) as ParameterElement;
            Parameter sample = null;
            foreach (var element in new FilteredElementCollector(doc).OfCategoryId(schedule.Definition.CategoryId).WhereElementIsNotElementType())
            {
                var owner = field.FieldType == ScheduleFieldType.ElementType ? doc.GetElement(element.GetTypeId()) : element;
                if (owner == null) continue;
                sample = parameterId < 0
                    ? owner.get_Parameter((BuiltInParameter)parameterId)
                    : parameterElement == null ? null : owner.get_Parameter(parameterElement.GetDefinition());
                if (sample != null) break;
            }
            if (sample == null || sample.StorageType != StorageType.ElementId) return null;
            if (value == null || value.Type == JTokenType.Null)
                throw new ArgumentException("An element name or ID is required for " + field.GetName() + ".");
            var reference = doc.GetElement(sample.AsElementId());
            var text = value.ToString();
            if (long.TryParse(text, out var id))
            {
                if (!RevitCompat.CanRepresentElementId(id)) throw new ArgumentException(RevitCompat.ElementIdRangeError(id));
                var resolved = doc.GetElement(RevitCompat.ToElementId(id));
                if (resolved == null || (reference != null && !reference.GetType().IsInstanceOfType(resolved)))
                    throw new ArgumentException("Element ID does not match the reference type of " + field.GetName() + ".");
                return resolved.Id;
            }
            if (reference == null)
                throw new ArgumentException("Cannot resolve names for " + field.GetName() + "; supply an element ID.");
            var candidates = reference is Phase
                ? new FilteredElementCollector(doc).OfClass(typeof(Phase))
                : new FilteredElementCollector(doc).OfCategoryId(reference.Category.Id);
            var matches = candidates.Where(e => reference.GetType().IsInstanceOfType(e)
                && string.Equals(e.Name, text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count != 1)
                throw new ArgumentException("Element name '" + text + "' is missing or ambiguous for " + field.GetName() + "; supply its ID.");
            return matches[0].Id;
        }
    }
}
