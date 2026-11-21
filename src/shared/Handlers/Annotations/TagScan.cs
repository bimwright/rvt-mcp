using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Shared reads for the tag tools, so they agree on which elements a view already tags.
    /// A scan that cannot finish throws; callers turn that into a command error instead of
    /// answering as if the view had no tags.
    /// </summary>
    internal static class TagScan
    {
        /// <summary>
        /// Ids of the host elements tagged in the view by element, room and area tags. With
        /// <paramref name="elementTagCategories"/> only element tags of those categories count (so a
        /// keynote or material tag does not make a wall "tagged").
        /// </summary>
        public static HashSet<long> CollectTaggedElementIds(Document doc, View view, params BuiltInCategory[] elementTagCategories)
        {
            var ids = new HashSet<long>();
            var onlyCategories = elementTagCategories != null && elementTagCategories.Length > 0
                ? new HashSet<long>(elementTagCategories.Select(c => (long)c))
                : null;

            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>())
            {
                if (onlyCategories != null
                    && (tag.Category == null || !onlyCategories.Contains(RevitCompat.GetId(tag.Category.Id))))
                    continue;
                foreach (var id in GetTaggedElementIds(tag))
                    ids.Add(id);
            }

            // RoomTag cannot be used with OfClass (ElementClassFilter does not support it); collect by category.
            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType()
                .OfType<RoomTag>())
            {
                if (!tag.IsTaggingLink && tag.Room != null)
                    ids.Add(RevitCompat.GetId(tag.Room.Id));
            }

            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_AreaTags)
                .WhereElementIsNotElementType())
            {
                var areaProp = tag.GetType().GetProperty("Area");
                if (areaProp?.GetValue(tag) is Element area)
                    ids.Add(RevitCompat.GetId(area.Id));
            }

            return ids;
        }

        /// <summary>The refusal for a sheet, naming the model views placed on it; null for any other view.</summary>
        public static string SheetRefusal(Document doc, View view, bool usesActiveView = false)
        {
            if (!(view is ViewSheet sheet))
                return null;

            var placed = sheet.GetAllViewports()
                .Select(id => doc.GetElement(id) as Viewport)
                .Where(vp => vp != null)
                .Select(vp => doc.GetElement(vp.ViewId) as View)
                .Where(v => v != null)
                .Select(v => new KeyValuePair<long, string>(RevitCompat.GetId(v.Id), v.Name))
                .ToList();
            return TagViewPolicy.SheetRefusal(view.ViewType.ToString(), view.Name, placed, usesActiveView);
        }

        /// <summary>
        /// Host ids tagged by one tag. A strategy's ids are used only after it has read to the end;
        /// an empty result means the tag tags nothing (an empty tag). When no strategy completes a
        /// read the scan is incomplete and this throws.
        /// </summary>
        public static HashSet<long> GetTaggedElementIds(IndependentTag tag)
        {
            if (tag == null) return new HashSet<long>();
            var anyRead = false;
            Exception lastError = null;

            // 1. GetTaggedElementIds() (Revit 2022+)
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedElementIds");
                if (method != null && method.Invoke(tag, null) is System.Collections.IEnumerable refs)
                {
                    var read = new HashSet<long>();
                    foreach (var linkId in refs)
                    {
                        var hostIdProp = linkId.GetType().GetProperty("HostElementId");
                        if (hostIdProp != null && hostIdProp.GetValue(linkId) is ElementId hostId && hostId != ElementId.InvalidElementId)
                            read.Add(RevitCompat.GetId(hostId));
                    }
                    anyRead = true;
                    if (read.Count > 0) return read;
                }
            }
            catch (Exception ex) { lastError = ex; }

            // 2. TaggedLocalElementId (Revit 2021 and older)
            try
            {
                var prop = tag.GetType().GetProperty("TaggedLocalElementId");
                if (prop != null)
                {
                    var val = prop.GetValue(tag) as ElementId;
                    anyRead = true;
                    if (val != null && val != ElementId.InvalidElementId)
                        return new HashSet<long> { RevitCompat.GetId(val) };
                }
            }
            catch (Exception ex) { lastError = ex; }

            // 3. GetTaggedLocalElementIds()
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedLocalElementIds");
                if (method != null && method.Invoke(tag, null) is ICollection<ElementId> localIds)
                {
                    var read = new HashSet<long>();
                    foreach (var lid in localIds)
                    {
                        if (lid != ElementId.InvalidElementId)
                            read.Add(RevitCompat.GetId(lid));
                    }
                    return read;
                }
            }
            catch (Exception ex) { lastError = ex; }

            if (!anyRead)
                throw new InvalidOperationException("Could not read the elements tagged by tag " + RevitCompat.GetId(tag.Id)
                    + (lastError == null ? "." : ": " + (lastError.InnerException ?? lastError).Message));
            return new HashSet<long>();
        }
    }
}
