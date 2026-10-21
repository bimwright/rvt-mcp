using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Views.Settings;
using RvtMcp.ToolCatalog;

namespace RvtMcp.Plugin.Presentation.Settings
{
    public interface IToolsCatalogSource : IDisposable
    {
        /// <summary>Delivered on the owning presentation thread.</summary>
        event EventHandler Changed;
        ToolsCatalogSnapshot Read();
    }

    public sealed class ToolsCatalogSnapshot
    {
        public ToolCatalogStatus Status { get; set; }
        public string Error { get; set; }
        public IReadOnlyList<ToolRow> Rows { get; set; } = new ToolRow[0];
        public int BuiltInCount { get; set; }
        public int CataloguedBuiltInCount { get; set; }
        public int BakedCount { get; set; }
    }
}
