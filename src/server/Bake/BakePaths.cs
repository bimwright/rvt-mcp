using System;
using System.IO;

namespace RvtMcp.Server.Bake
{
    public sealed class BakePaths
    {
        public BakePaths()
            : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        {
        }

        public BakePaths(string localApplicationData)
        {
            if (string.IsNullOrWhiteSpace(localApplicationData))
                throw new ArgumentException("Local application data path is required.", nameof(localApplicationData));

            Root = Path.Combine(localApplicationData, "Bimwright", "rvt-mcp");
            UsageJsonl = Path.Combine(Root, "usage.jsonl");
            BakeDb = Path.Combine(Root, "bake.db");
            AuditJsonl = Path.Combine(Root, "bake-audit.jsonl");
            LegacyBakedDir = Path.Combine(Root, "baked");
            LegacyRegistryJson = Path.Combine(LegacyBakedDir, "registry.json");
        }

        public string Root { get; }
        public string UsageJsonl { get; }
        public string BakeDb { get; }
        public string AuditJsonl { get; }
        public string LegacyBakedDir { get; }
        public string LegacyRegistryJson { get; }
    }
}
