using System;
using System.Collections.Generic;
using System.Windows.Threading;
using RvtMcp.Failures;
using RvtMcp.Plugin.Failures;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation.Issues;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Infrastructure.UI
{
    internal sealed class IssuesRuntime : IIssuesRuntime
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private bool _active, _disposed;
        private int _generation;
        public event EventHandler ContextChanged;
        public event EventHandler LanguageChanged;
        public void Activate()
        {
            if (_active || _disposed) return;
            _active = true;
            _generation++;
            FailureControl.UiContextChanged += OnContextChanged;
            L.Changed += OnLanguageChanged;
        }
        public void Deactivate()
        {
            if (!_active) return;
            _active = false;
            _generation++;
            FailureControl.UiContextChanged -= OnContextChanged;
            L.Changed -= OnLanguageChanged;
        }
        private void OnContextChanged(object sender, EventArgs e) => Publish(() => ContextChanged?.Invoke(this, EventArgs.Empty), true);
        private void OnLanguageChanged(object sender, EventArgs e) => Publish(() => LanguageChanged?.Invoke(this, EventArgs.Empty), false);
        private void Publish(Action action, bool deferred)
        {
            if (!_active || _disposed) return;
            var generation = _generation;
            Action guarded = () => { if (_active && !_disposed && generation == _generation) action(); };
            try { if (!deferred && _dispatcher.CheckAccess()) guarded(); else _dispatcher.BeginInvoke(guarded); } catch { }
        }
        private static FailureLedgerStore Reader() => new FailureLedgerStore(readOnly: true);
        public IReadOnlyList<FailureIssueModel> Models(string state) => Reader().GetIssueModels(state);
        public FailureRecordPage Query(FailureRecordQuery query) => Reader().Query(query);
        public FailureOperation Operation(string id) => Reader().GetOperation(id);
        public IReadOnlyList<FailureRule> Rules(string scope) => Reader().GetRules(scope);
        public FailureIssuesContext Context(string scope = null) => scope == null ? FailureControl.GetUiContext() : FailureControl.GetUiContext(scope);
        public ICollection<string> OpenScopes() => FailureControl.CopyOpenModelScopes();
        public FailureGate Gate(string scope) => FailureControl.GetUiGate(scope);
        public FailurePersistenceResult Save(FailureDecision decision) => FailureControl.ApplyUiDecision(decision);
        public bool CanReset => FailureControl.Store != null && FailureControl.Active == null;
        public FailureLedgerSummary ResetSummary() => FailureControl.GetResetSummary();
        public string BackupFolder => FailureControl.Store.BackupFolder;
        public string Reset() => FailureControl.ResetLedger();
        public string WindowsUser => Environment.UserName;
        public void Dispose() { if (_disposed) return; Deactivate(); _disposed = true; }
    }
}
