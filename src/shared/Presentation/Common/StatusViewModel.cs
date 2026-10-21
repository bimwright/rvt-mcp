using System;
using System.ComponentModel;

namespace RvtMcp.Plugin.Presentation
{
    public enum UiTone { Neutral, Error, Review }

    public interface IHistoryTabState : INotifyPropertyChanged, IDisposable
    {
        string StatusText { get; }
        UiTone StatusTone { get; }
    }

    public abstract class StatusViewModel : ObservableObject, IHistoryTabState
    {
        private string _statusText = "";
        private UiTone _statusTone;
        public string StatusText => _statusText;
        public UiTone StatusTone => _statusTone;
        protected void SetStatus(string text, UiTone tone = UiTone.Neutral)
        {
            SetProperty(ref _statusTone, tone, nameof(StatusTone));
            SetProperty(ref _statusText, text ?? "", nameof(StatusText));
        }
        public abstract void Dispose();
    }
}
