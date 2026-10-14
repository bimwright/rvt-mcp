namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// How many members an MEP system reports. Piping and HVAC keep the flow
    /// network (pipes, ducts, fittings) separate from terminal equipment.
    /// Electrical has a single member list.
    /// </summary>
    public static class MepMembershipPolicy
    {
        public readonly struct Counts
        {
            public Counts(int elementCount, int terminalCount, bool isEmpty)
            {
                ElementCount = elementCount;
                TerminalCount = terminalCount;
                IsEmpty = isEmpty;
            }

            public int ElementCount { get; }
            public int TerminalCount { get; }
            public bool IsEmpty { get; }
        }

        public static Counts Evaluate(bool separateFlowNetwork, int flowCount, int terminalCount, bool hasBaseEquipment = false)
        {
            if (!separateFlowNetwork)
                return new Counts(terminalCount, terminalCount, terminalCount == 0 && !hasBaseEquipment);

            return new Counts(flowCount, terminalCount, flowCount == 0 && terminalCount == 0 && !hasBaseEquipment);
        }
    }
}
