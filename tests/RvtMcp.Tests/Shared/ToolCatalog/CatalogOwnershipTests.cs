using RvtMcp.ToolCatalog;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class CatalogOwnershipTests
    {
        [Fact]
        public void Late_old_connection_cleanup_cannot_clear_new_catalog()
        {
            ToolCatalogStore.Clear();
            long old = ToolCatalogStore.BeginConnection();
            long current = ToolCatalogStore.BeginConnection();
            var json = "{\"schema_version\":1,\"server_version\":\"1.1.0\",\"catalogued_built_in_count\":0,\"tools\":[]}";
            Assert.True(ToolCatalogStore.AcceptJson(json, current).IsValid);
            Assert.False(ToolCatalogStore.Clear(old));
            Assert.Equal(ToolCatalogStatus.Current, ToolCatalogStore.Status);
            Assert.False(ToolCatalogStore.AcceptJson("{}", old).IsValid);
            Assert.Equal(ToolCatalogStatus.Current, ToolCatalogStore.Status);
            Assert.True(ToolCatalogStore.Clear(current));
            Assert.Equal(ToolCatalogStatus.NotConnected, ToolCatalogStore.Status);
        }

        [Fact]
        public void Stale_connection_cannot_overwrite_catalog_after_parse()
        {
            ToolCatalogStore.Clear();
            long old = ToolCatalogStore.BeginConnection();
            var json = "{\"schema_version\":1,\"server_version\":\"1.1.0\",\"catalogued_built_in_count\":0,\"tools\":[]}";

            // Owner wins the parse-vs-commit race: the stale connection's commit re-checks.
            var stale = ToolCatalogStore.AcceptJson(json, old, () => ToolCatalogStore.BeginConnection());
            Assert.False(stale.IsValid);
            Assert.Equal(ToolCatalogStatus.ConnectedNoCatalog, ToolCatalogStore.Status);
            Assert.Null(ToolCatalogStore.Current);
        }
    }
}
