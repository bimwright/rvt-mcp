using System;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Reads the shared-parameter GUID of a definition. Document bindings are keyed by
    /// InternalDefinition, which carries no GUID; the GUID lives on the
    /// SharedParameterElement with the same id. An ExternalDefinition (from the shared
    /// parameter file) carries it directly.
    /// </summary>
    internal static class SharedParameterGuid
    {
        public static Guid? Of(Document doc, Definition definition)
        {
            if (definition is ExternalDefinition external)
                return external.GUID;
            if (definition is InternalDefinition internalDefinition)
                return (doc.GetElement(internalDefinition.Id) as SharedParameterElement)?.GuidValue;
            return null;
        }
    }
}
