using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DRRipper.UI
{
    /// <summary>
    /// Minimal ICommand implementation (Ticket #006 §42). Deliberately avoids
    /// CommandManager (PresentationCore) so view-models stay headless-testable;
    /// views call <see cref="RaiseCanExecuteChanged"/> after relevant state changes
    /// (MainViewModel does this on its coalesced refresh tick).
    /// System.Windows.Input.ICommand itself lives in System.ObjectModel — no WPF needed.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            try { return _canExecute?.Invoke(parameter) ?? true; }
            catch { return false; }
        }

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged()
        {
            try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <summary>
    /// Async ICommand: guards re-entrancy while the operation runs and routes
    /// exceptions to an optional handler (surfaced as VM status text, never raw dumps).
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private volatile bool _running;

        public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        /// <summary>True while the operation is in flight (bindable for busy UI).</summary>
        public bool IsRunning => _running;

        public event Action<Exception>? Faulted;

        public bool CanExecute(object? parameter)
        {
            if (_running) return false;
            try { return _canExecute?.Invoke(parameter) ?? true; }
            catch { return false; }
        }

        public async void Execute(object? parameter)
        {
            if (_running) return;
            _running = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute(parameter).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { Faulted?.Invoke(ex); } catch { }
            }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }
}
