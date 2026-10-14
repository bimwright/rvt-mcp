using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Tests that touch the static <c>L</c> facade share global state — running them
    /// in the same xunit collection keeps them sequential relative to each other.
    /// </summary>
    [CollectionDefinition("L10n")]
    public class L10nCollection { }
}
