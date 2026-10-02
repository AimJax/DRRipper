using System;
using System.Threading.Tasks;

namespace DRRipper.UI
{
    /// <summary>
    /// Tray-menu → ViewModel routing (Ticket #006.1 §9/§24). Lives in production
    /// code (MainWindow uses it) AND is headless-tested with <see cref="FakeTrayService"/>:
    /// menu actions call existing VM commands/scheduler APIs, never duplicated logic.
    /// Window-specific actions (show/hide/exit) stay in the close controller.
    /// </summary>
    public sealed class TrayMenuRouter : IDisposable
    {
        private readonly MainViewModel _vm;
        private ITrayService? _tray;
        private bool _disposed;

        public TrayMenuRouter(MainViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        }

        public void Attach(ITrayService tray)
        {
            Detach();
            _tray = tray ?? throw new ArgumentNullException(nameof(tray));
            tray.PauseAllRequested += OnPauseAll;
            tray.ResumeAllRequested += OnResumeAll;
        }

        public void Detach()
        {
            var tray = _tray;
            _tray = null;
            if (tray == null) return;
            try { tray.PauseAllRequested -= OnPauseAll; } catch { }
            try { tray.ResumeAllRequested -= OnResumeAll; } catch { }
        }

        private void OnPauseAll()
        {
            try { _vm.PauseAllCommand.Execute(null); } catch { }
        }

        private void OnResumeAll()
        {
            try { _vm.ResumeAllCommand.Execute(null); } catch { }
        }

        /// <summary>Headless-friendly direct invocation (same path as the events).</summary>
        public Task OnPauseAllAsync()
        {
            OnPauseAll();
            return Task.CompletedTask;
        }

        public Task OnResumeAllAsync()
        {
            OnResumeAll();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
        }
    }
}
