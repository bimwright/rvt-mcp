using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>
    /// "Actual changes" content shared by both modes: a note when there is nothing to list, otherwise the
    /// Added / Modified / Deleted groups per document, each showing at most <see cref="GroupLimit"/> items
    /// at first; each "Show N more" press adds up to <see cref="MoreStep"/> more.
    /// </summary>
    internal sealed class ReportChangesView : StackPanel
    {
        public const int GroupLimit = 200;

        /// <summary>Items added per "Show N more" press; thousands at once keep the window busy for seconds.</summary>
        public const int MoreStep = 1000;

        public ReportChangesView(ChangeSectionViewModel changes)
        {
            Changes = changes;
            if (changes == null) return;
            if (!changes.HasData || Total(changes) == 0) Children.Add(Note(changes.NoteText, ReportsStyles.TextSecondary));
            else
            {
                var multiple = changes.Documents.Count >= 2;
                foreach (var document in changes.Documents)
                {
                    if (multiple)
                        Children.Add(new TextBlock { Text = document.Title, FontSize = 11.5, Foreground = ReportsStyles.TextSecondary,
                            Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Tag = "ChangeDocumentTitle" });
                    AddGroup(document, document.Added, "reports.changes.group.added");
                    AddGroup(document, document.Modified, "reports.changes.group.modified");
                    AddGroup(document, document.Deleted, "reports.changes.group.deleted");
                }
            }
            if (!string.IsNullOrEmpty(changes.IncompleteText)) Children.Add(Note(changes.IncompleteText, ReportsStyles.WarningText));
            if (!string.IsNullOrEmpty(changes.TruncatedText)) Children.Add(Note(changes.TruncatedText, ReportsStyles.TextSecondary));
        }

        public ChangeSectionViewModel Changes { get; }

        public static int Total(ChangeSectionViewModel changes) =>
            changes == null ? 0 : changes.AddedCount + changes.ModifiedCount + changes.DeletedCount;

        /// <summary>Count badges for the wide header and the compact chip; groups with zero items have none.</summary>
        public static IEnumerable<Border> Badges(ChangeSectionViewModel changes)
        {
            if (changes == null || !changes.HasData)
            {
                yield return ReportsStyles.Badge(L.T("reports.changes.badge.noData"), ReportsStyles.NeutralBadge);
                yield break;
            }
            if (Total(changes) == 0)
            {
                yield return ReportsStyles.Badge(L.T("reports.changes.badge.none"), ReportsStyles.NeutralBadge);
                yield break;
            }
            if (changes.AddedCount > 0)
                yield return ReportsStyles.Badge(L.T("reports.changes.badge.added", ("count", changes.AddedCount)), ReportsStyles.AddedBadge);
            if (changes.ModifiedCount > 0)
                yield return ReportsStyles.Badge(L.T("reports.changes.badge.modified", ("count", changes.ModifiedCount)), ReportsStyles.ModifiedBadge);
            if (changes.DeletedCount > 0)
                yield return ReportsStyles.Badge(L.T("reports.changes.badge.deleted", ("count", changes.DeletedCount)), ReportsStyles.DeletedBadge);
        }

        private void AddGroup(ChangeDocumentViewModel document, IReadOnlyList<ChangeItemViewModel> items, string titleKey)
        {
            if (items == null || items.Count == 0) return;
            Children.Add(new TextBlock { Text = L.T(titleKey, ("count", items.Count)), FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                Foreground = ReportsStyles.TextSecondary, Margin = new Thickness(0, 6, 0, 3) });
            var wrap = new WrapPanel();
            Children.Add(wrap);
            var tooltip = L.T("reports.link.otherModel", ("model", document.Title));
            AddItems(wrap, items, 0, System.Math.Min(GroupLimit, items.Count), tooltip);
            if (items.Count <= GroupLimit) return;
            var shown = GroupLimit;
            var more = new Button { Style = (Style)ReportsStyles.Dictionary[ReportsStyles.TextButtonKey],
                Content = MoreLabel(items.Count - shown), Margin = new Thickness(0, 0, 0, 2) };
            // Stays applied until another report is shown: this view lives as long as the report's detail.
            more.Click += (s, e) =>
            {
                var next = System.Math.Min(shown + MoreStep, items.Count);
                AddItems(wrap, items, shown, next, tooltip);
                shown = next;
                if (shown < items.Count) more.Content = MoreLabel(items.Count - shown);
                else Children.Remove(more);
            };
            Children.Add(more);
        }

        private static string MoreLabel(int remaining) =>
            L.T("reports.changes.showMore", ("count", System.Math.Min(MoreStep, remaining)));

        private static void AddItems(WrapPanel wrap, IReadOnlyList<ChangeItemViewModel> items, int from, int to, string tooltip)
        {
            for (var i = from; i < to; i++)
            {
                var item = items[i];
                if (item == null) continue;
                var text = new TextBlock { Margin = new Thickness(0, 0, 14, 4), DataContext = item };
                var id = new Run(item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    { FontFamily = ReportsStyles.Mono, FontSize = 11.5 };
                if (item.IsDeleted)
                {
                    id.Foreground = ReportsStyles.Muted;
                    id.TextDecorations = TextDecorations.Strikethrough;
                    text.Inlines.Add(id);
                }
                else
                {
                    var link = ReportFlowDocumentBuilder.CreateLink(item, item.OpenCommand, tooltip);
                    link.Inlines.Add(new Run(string.IsNullOrEmpty(item.Category) ? L.T("reports.changes.element") : item.Category));
                    id.Foreground = ReportsStyles.TextSecondary;
                    text.Inlines.Add(link);
                    text.Inlines.Add(new Run(" "));
                    text.Inlines.Add(id);
                }
                wrap.Children.Add(text);
            }
        }

        private static TextBlock Note(string text, System.Windows.Media.Brush foreground) => new TextBlock
        {
            Text = text, Foreground = foreground, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2), Tag = "ChangesNote"
        };
    }
}
