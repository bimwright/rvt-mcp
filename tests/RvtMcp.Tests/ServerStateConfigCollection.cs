using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Tests that mutate the static <c>ServerState.Config</c> share global state —
    /// running them in the same xunit collection keeps them sequential.
    /// </summary>
    [CollectionDefinition("ServerStateConfig")]
    public class ServerStateConfigCollection { }
}
