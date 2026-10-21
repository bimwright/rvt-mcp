using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using RvtMcp.Failures;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Presentation.Issues
{
        internal sealed class ModelRow : INotifyPropertyChanged
        {
            private readonly FailureIssueModel _model;
            private bool _isOpen;
            public FailureIssueModel Source => _model;
            public string ModelScopeKey => _model.ModelScopeKey;
            public int RevitYear => _model.RevitYear;
            public bool IsCurrent { get; private set; }
            public double SearchScore { get; private set; } = 1;
            public bool MatchesSearch => SearchScore >= 0.8;
            public bool IsOpen
            {
                get => _isOpen;
                set
                {
                    if (_isOpen == value) return;
                    _isOpen = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
                }
            }
            public string Title => string.IsNullOrWhiteSpace(_model.Title) ? IssueRowText.Unknown() + " (" + ModelScopeKey.Substring(0, Math.Min(8, ModelScopeKey.Length)) + ")" : _model.Title;
            public string Label => (RevitYear == 0 ? "R?" : "R" + (RevitYear % 100).ToString("D2", CultureInfo.InvariantCulture))
                + " - " + (_model.IsFamilyDocument.HasValue
                    ? L.T(_model.IsFamilyDocument.Value ? "failure.modelKind.family" : "failure.modelKind.project") : IssueRowText.Unknown()) + " - " + Title;
            public string Prefix => Label;
            public string Count => _model.IssueCount.ToString("n0", CultureInfo.CurrentCulture);
            public string Description => (IsCurrent ? L.T("failure.activeModel", ("title", Title)) + "\n" : "") + Prefix + "\n" + Count + "\n" + ModelScopeKey;
            public event PropertyChangedEventHandler PropertyChanged;
            internal ModelRow(FailureIssueModel model, FailureIssuesContext context, ICollection<string> openScopes)
            {
                _model = model;
                UpdateContext(context, openScopes);
            }

            internal void UpdateContext(FailureIssuesContext context, ICollection<string> openScopes)
            {
                IsOpen = RevitYear == context?.RevitYear && FailureIssuesPresentation.IssueModelIsOpen(ModelScopeKey, openScopes);
                var current = context?.IsAvailable == true && ModelScopeKey == context.ModelScopeKey && RevitYear == context.RevitYear;
                if (IsCurrent == current) return;
                IsCurrent = current;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Prefix)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
            }

            internal void UpdateSearch(string query)
            {
                var score = FailureIssuesPresentation.ModelNameSearchScore(_model.Title, query);
                if (SearchScore == score) return;
                SearchScore = score;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchScore)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchesSearch)));
            }
        }

        internal sealed class IssueRow
        {
            internal FailureIncident Incident { get; }
            public int Number { get; set; }
            public string Date => Incident.CreatedUtc.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
            public string Clock => Incident.CreatedUtc.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            public string Tool { get; }
            public int Count => Incident.MessageCount;
            internal IssueRow(FailureIncident incident, FailureOperation operation)
            { Incident = incident; Tool = operation?.Tool ?? IssueRowText.Unknown(); }
        }

        internal sealed class RuleRow
        {
            internal FailureRule Rule { get; }
            public string Label => IssuesText.RuleText(Rule);
            internal RuleRow(FailureRule rule) { Rule = rule; }
        }
    internal static class IssueRowText { internal static string Unknown() => L.T("failure.detail.unknown"); }
}
