using System;
using System.Windows;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation.History;

namespace RvtMcp.Plugin.Infrastructure.UI
{
    public sealed class HistoryDialogs : IHistoryDialogs
    {
        private readonly Window _owner;
        public HistoryDialogs(Window owner) { _owner = owner ?? throw new ArgumentNullException(nameof(owner)); }
        public bool ConfirmNewSession() => MessageBox.Show(_owner, L.T("history.newSession.confirm"), L.T("history.newSession.title"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        public bool ConfirmRerun(bool redacted) => MessageBox.Show(_owner,
            L.T(redacted ? "security.send_code.rerunConfirmRedacted" : "security.send_code.rerunConfirm"),
            L.T("security.send_code.rerunConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}
