using System;
using System.IO;
using Xunit;
using RvtMcp.Plugin;

public class LegacyDataMigrationTests
{
    [Fact]
    public void MigrateOnce_FamilyRootWithSiblingDoesNotCreateProductRoot()
    {
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            var sibling = Path.Combine(tempLocal, "Bimwright", "ipt-mcp", "baked");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "tool.json"), "{}");
            var oldRoot = Path.Combine(tempLocal, "RvtMcp");
            Directory.CreateDirectory(oldRoot);
            File.WriteAllText(Path.Combine(oldRoot, "rvtmcp.config.json"), "{}");

            LegacyDataMigration.MigrateOnce(tempLocal);

            Assert.False(Directory.Exists(Path.Combine(tempLocal, "Bimwright", "rvt-mcp")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(sibling, "tool.json")));
            Assert.True(File.Exists(Path.Combine(oldRoot, "rvtmcp.config.json")));
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }

    [Fact]
    public void MigrateOnce_CopiesBakedFolderAndCreatesMarker()
    {
        // Arrange: redirect LOCALAPPDATA to temp using parameterized helper.
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            var legacyBaked = Path.Combine(tempLocal, "Bimwright", "baked");
            Directory.CreateDirectory(legacyBaked);
            File.WriteAllText(Path.Combine(legacyBaked, "tool1.json"), "{}");

            // Act
            LegacyDataMigration.MigrateOnce(tempLocal);

            // Assert
            var newBaked = Path.Combine(tempLocal, "Bimwright", "rvt-mcp", "baked", "tool1.json");
            Assert.True(File.Exists(newBaked));
            var marker = Path.Combine(tempLocal, "Bimwright", "rvt-mcp", ".migrated-from-bimwright");
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }

    [Fact]
    public void MigrateOnce_IsIdempotent()
    {
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            Directory.CreateDirectory(Path.Combine(tempLocal, "Bimwright", "baked"));
            File.WriteAllText(Path.Combine(tempLocal, "Bimwright", "baked", "tool1.json"), "{}");

            LegacyDataMigration.MigrateOnce(tempLocal);
            var firstMtime = File.GetLastWriteTimeUtc(Path.Combine(tempLocal, "Bimwright", "rvt-mcp", "baked", "tool1.json"));

            LegacyDataMigration.MigrateOnce(tempLocal);   // second call — should be no-op
            var secondMtime = File.GetLastWriteTimeUtc(Path.Combine(tempLocal, "Bimwright", "rvt-mcp", "baked", "tool1.json"));

            Assert.Equal(firstMtime, secondMtime);
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }
}
