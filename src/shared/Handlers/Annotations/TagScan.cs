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
        /// <summary>Ids of the host elements tagged in the view by element, room and area tags.</summary>
        public static HashSet<long> CollectTaggedElementIds(Document doc, View view)
        {
            var ids = new HashSet<long>();

            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>())
            {
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

        /// <summary>
        /// Host ids tagged by one tag. An empty result means the tag tags nothing (an empty tag);
        /// when no API strategy can be read at all the scan is incomplete and this throws.
        /// </summary>
        public static HashSet<long> GetTaggedElementIds(IndependentTag tag)
        {
            var ids = new HashSet<long>();
            if (tag == null) return ids;
            var anyRead = false;
            Exception lastError = null;

            // 1. GetTaggedElementIds() (Revit 2022+)
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedElementIds");
                if (method != null)
                {
                    var refs = method.Invoke(tag, null) as System.Collections.IEnumerable;
                    if (refs != null)
                    {
                        anyRead = true;
                        foreach (var linkId in refs)
                        {
                            var hostIdProp = linkId.GetType().GetProperty("HostElementId");
                            if (hostIdProp != null)
                            {
                                var hostId = hostIdProp.GetValue(linkId) as ElementId;
                                if (hostId != null && hostId != ElementId.InvalidElementId)
                                    ids.Add(RevitCompat.GetId(hostId));
                            }
                        }
                        if (ids.Count > 0) return ids;
                    }
                }
            }
            catch (Exception ex) { lastError = ex; }

            // 2. TaggedLocalElementId (Revit 2021 and older)
            try
            {
                var prop = tag.GetType().GetProperty("TaggedLocalElementId");
                if (prop != null)
                {
                    anyRead = true;
                    var val = prop.GetValue(tag) as ElementId;
                    if (val != null && val != ElementId.InvalidElementId)
                    {
                        ids.Add(RevitCompat.GetId(val));
                        return ids;
                    }
                }
            }
            catch (Exception ex) { lastError = ex; }

            // 3. GetTaggedLocalElementIds()
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedLocalElementIds");
                if (method != null)
                {
                    var localIds = method.Invoke(tag, null) as ICollection<ElementId>;
                    if (localIds != null)
                    {
                        anyRead = true;
                        foreach (var lid in localIds)
                        {
                            if (lid != ElementId.InvalidElementId)
                                ids.Add(RevitCompat.GetId(lid));
                        }
                        return ids;
                    }
                }
            }
            catch (Exception ex) { lastError = ex; }

            if (!anyRead)
                throw new InvalidOperationException("Could not read the elements tagged by tag " + RevitCompat.GetId(tag.Id)
                    + (lastError == null ? "." : ": " + (lastError.InnerException ?? lastError).Message));
            return ids;
        }
    }
}
