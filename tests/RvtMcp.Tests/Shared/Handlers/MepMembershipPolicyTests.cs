using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class MepMembershipPolicyTests
    {
        [Fact]
        public void PipingNetworkWithNoFixtures_IsNotEmpty()
        {
            var counts = MepMembershipPolicy.Evaluate(separateFlowNetwork: true, flowCount: 3, terminalCount: 0);

            Assert.Equal(3, counts.ElementCount);
            Assert.Equal(0, counts.TerminalCount);
            Assert.False(counts.IsEmpty);
        }

        [Fact]
        public void FixtureWithoutPipes_IsNotEmpty()
        {
            var counts = MepMembershipPolicy.Evaluate(separateFlowNetwork: true, flowCount: 0, terminalCount: 1);

            Assert.Equal(0, counts.ElementCount);
            Assert.Equal(1, counts.TerminalCount);
            Assert.False(counts.IsEmpty);
        }

        [Fact]
        public void PipingSystemWithNeitherNetworkNorTerminals_IsEmpty()
        {
            var counts = MepMembershipPolicy.Evaluate(separateFlowNetwork: true, flowCount: 0, terminalCount: 0);

            Assert.True(counts.IsEmpty);
        }

        [Theory]
        [InlineData(4, false)]
        [InlineData(0, true)]
        public void ElectricalUsesItsSingleMemberList(int members, bool isEmpty)
        {
            var counts = MepMembershipPolicy.Evaluate(separateFlowNetwork: false, flowCount: 99, terminalCount: members);

            Assert.Equal(members, counts.ElementCount);
            Assert.Equal(members, counts.TerminalCount);
            Assert.Equal(isEmpty, counts.IsEmpty);
        }
    }
}
