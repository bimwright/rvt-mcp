using RvtMcp.Plugin.Ribbon;
using Xunit;

namespace RvtMcp.Tests
{
    public class RibbonIconOnlyTests
    {
        public enum ItemSize { Standard, Large }
        public class Node
        {
            public object[] Tabs { get; set; } = new object[0];
            public object[] Panels { get; set; } = new object[0];
            public object[] Items { get; set; } = new object[0];
            public Node Source { get; set; }
            public string Title { get; set; }
            public string Id { get; set; }
            public bool ShowText { get; set; } = true;
            public ItemSize Size { get; set; }
        }
        [Fact] public void Only_matching_panel_and_exact_command_suffix_are_styled()
        {
            var report = new Node { Id = "Custom%ShowReports" }; var history = new Node { Id = "Custom%ShowHistory" };
            var wrong = new Node { Id = "Custom%ShowReports" };
            var root = new Node { Tabs = new object[] { new Node { Panels = new object[] {
                new Node { Source = new Node { Title = "Other", Items = new object[] { wrong } } },
                new Node { Source = new Node { Title = "RvtMcp", Items = new object[] { new Node { Items = new object[] { report, history } } } } } } } } };
            Assert.Same(report, RibbonIconOnly.Apply(root, "RvtMcp", "ShowReports"));
            Assert.False(report.ShowText); Assert.Equal(ItemSize.Large, report.Size);
            Assert.True(history.ShowText); Assert.True(wrong.ShowText);
            Assert.Null(RibbonIconOnly.Apply(root, "RvtMcp", "Reports"));
        }
        [Fact] public void Missing_host_tree_is_a_safe_retry() => Assert.Null(RibbonIconOnly.Apply(null, "RvtMcp", "ShowReports"));
    }
}
