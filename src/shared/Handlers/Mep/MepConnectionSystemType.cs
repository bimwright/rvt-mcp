using System;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin.Handlers
{
    internal static class MepConnectionSystemType
    {
        // Resolve the selected port, never an arbitrary system on multi-port equipment.
        // Null means genuinely unassigned/not applicable; API failures must propagate.
        public static MEPSystemType Read(Connector connector)
        {
            var domain = connector.Domain;
            if (domain != Domain.DomainPiping && domain != Domain.DomainHvac) return null;

            var system = connector.MEPSystem;
            ElementId typeId;
            if (system != null)
            {
                typeId = system.GetTypeId();
            }
            else if (connector.Owner is MEPCurve)
            {
                var parameter = connector.Owner.get_Parameter(domain == Domain.DomainPiping
                    ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM
                    : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                if (parameter == null)
                    throw new InvalidOperationException("Cannot read the selected curve's system type.");
                if (!parameter.HasValue) return null;
                typeId = parameter.AsElementId();
            }
            else
            {
                // An unassigned equipment port has no system instance yet. Other ports
                // on this equipment must not supply a type for this connection.
                return null;
            }

            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                if (system == null) return null;
                throw new InvalidOperationException("The selected connector's system has no readable type.");
            }
            var type = connector.Owner.Document.GetElement(typeId) as MEPSystemType;
            if (type == null)
                throw new InvalidOperationException("Cannot resolve the selected connector's system type.");
            return type;
        }

        public static bool Mismatch(MEPSystemType first, MEPSystemType second)
            => first != null && second != null && first.Id != second.Id;
    }
}
