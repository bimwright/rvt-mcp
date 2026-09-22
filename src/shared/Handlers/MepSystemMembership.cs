using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Read each API collection once. A failed read must never look like an empty system.
    /// Elements excludes the base equipment; network collections can overlap terminals.
    /// </summary>
    internal sealed class MepSystemMembership
    {
        private MepSystemMembership(Element[] flow, Element[] terminals, FamilyInstance equipment,
            bool separateFlowNetwork)
        {
            FlowElements = flow;
            BaseEquipment = equipment;
            Counts = MepMembershipPolicy.Evaluate(separateFlowNetwork, flow.Length, terminals.Length,
                equipment != null);
            AllElements = flow.Concat(terminals)
                .Concat(equipment == null ? new Element[0] : new Element[] { equipment })
                .GroupBy(element => RevitCompat.GetId(element.Id))
                .Select(group => group.First()).ToArray();
        }

        public Element[] FlowElements { get; }
        public Element[] AllElements { get; }
        public FamilyInstance BaseEquipment { get; }
        public MepMembershipPolicy.Counts Counts { get; }

        public static MepSystemMembership Read(MEPSystem system)
        {
            try
            {
                var terminals = ReadElements(system.Elements, "Elements");
                var flow = terminals;
                bool separate = false;
                if (system is PipingSystem piping)
                {
                    flow = ReadElements(piping.PipingNetwork, "PipingNetwork");
                    separate = true;
                }
                else if (system is MechanicalSystem mechanical)
                {
                    flow = ReadElements(mechanical.DuctNetwork, "DuctNetwork");
                    separate = true;
                }

                return new MepSystemMembership(flow, terminals, system.BaseEquipment, separate);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to read MEP system membership: " + ex.Message, ex);
            }
        }

        private static Element[] ReadElements(ElementSet elements, string source)
        {
            if (elements == null)
                throw new InvalidOperationException(source + " returned no collection.");
            var members = elements.Cast<Element>().ToArray();
            if (members.Any(element => element == null))
                throw new InvalidOperationException(source + " contains an unreadable member.");
            return members;
        }
    }
}
