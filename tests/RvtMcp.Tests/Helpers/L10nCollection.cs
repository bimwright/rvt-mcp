using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Tests that touch the static <c>L</c> facade share global state — running them
    /// without other collections also protects UI readers that use the active locale.
    /// </summary>
    [CollectionDefinition("L10n", DisableParallelization = true)]
    public class L10nCollection { }
}
