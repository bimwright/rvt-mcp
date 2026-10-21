using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RvtMcp.Plugin.Presentation
{
    public sealed class AsyncRelayCommand : ObservableObject, ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private readonly Action<Exception> _onError;
        private bool _isRunning;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        }

        public bool IsRunning => _isRunning;
        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object parameter) => !_isRunning && (_canExecute == null || _canExecute());
        public async void Execute(object parameter) => await ExecuteAsync(parameter);
        public async Task ExecuteAsync(object parameter = null)
        {
            if (!CanExecute(parameter)) return;
            SetProperty(ref _isRunning, true, nameof(IsRunning));
            RaiseCanExecuteChanged();
            try { await _execute(); }
            catch (Exception ex) { _onError(ex); }
            finally
            {
                SetProperty(ref _isRunning, false, nameof(IsRunning));
                RaiseCanExecuteChanged();
            }
        }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
