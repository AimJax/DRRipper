using System;
using System.Threading;
using System.Threading.Tasks;

namespace DRRipper.UI
{
    /// <summary>Close-button decision (Ticket #006.1 §6/§7). Pure function of settings + work.</summary>
    public enum CloseOutcome { HideToTray, ExitNow }

    /// <summary>Single shutdown state machine (Ticket #006.1 §12).</summary>
    public enum ShutdownPhase { Running, Hiding, Exiting, Exited }

    /// <summary>
    /// Window close/minimize/exit orchestration without any WPF types (Ticket #006.1
    /// §12/§25). The window supplies tiny side-effect delegates (hide/show) and one
    /// shutdown delegate; all policy lives here and is unit-tested headlessly.
    /// Hiding NEVER touches the scheduler: transfers continue untouched.
    /// </summary>
    public sealed class CloseController
    {
        private readonly Func<bool> _hasActiveWork;
        private readonly Func<AppSettings> _getSettings;
        private readonly ITrayService _tray;
        private readonly Func<Task> _shutdownAsync;
        private readonly Action _hideWindow;
        private readonly Action _showWindow;
        private readonly object _lock = new();
        private Task? _exitTask;
        private int _shutdownCalls;
        private bool _balloonShown;

        public CloseController(
            Func<bool> hasActiveWork,
            Func<AppSettings> getSettings,
            ITrayService tray,
            Func<Task> shutdownAsync,
            Action hideWindow,
            Action showWindow)
        {
            _hasActiveWork = hasActiveWork ?? throw new ArgumentNullException(nameof(hasActiveWork));
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            _tray = tray ?? throw new ArgumentNullException(nameof(tray));
            _shutdownAsync = shutdownAsync ?? throw new ArgumentNullException(nameof(shutdownAsync));
            _hideWindow = hideWindow ?? throw new ArgumentNullException(nameof(hideWindow));
            _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        }

        public ShutdownPhase Phase { get; private set; } = ShutdownPhase.Running;

        /// <summary>Number of times the real shutdown delegate ran (exactly-once proof).</summary>
        public int ShutdownCalls => Volatile.Read(ref _shutdownCalls);

        /// <summary>Pure close decision for the current settings + work state.</summary>
        public CloseOutcome DecideClose()
        {
            var settings = _getSettings();
            return DecideClose(settings.CloseBehavior, _hasActiveWork());
        }

        public static CloseOutcome DecideClose(CloseButtonBehavior behavior, bool hasActiveWork)
        {
            return behavior switch
            {
                CloseButtonBehavior.AlwaysExit => CloseOutcome.ExitNow,
                CloseButtonBehavior.ExitWhenIdle => hasActiveWork ? CloseOutcome.HideToTray : CloseOutcome.ExitNow,
                _ => CloseOutcome.HideToTray,
            };
        }

        /// <summary>
        /// Window X handler: returns true when the close event was consumed
        /// (hide initiated or exit already underway). Hiding never stops the scheduler.
        /// </summary>
        public bool RequestClose()
        {
            lock (_lock)
            {
                if (Phase == ShutdownPhase.Exited) return false; // allow the final close
                if (Phase == ShutdownPhase.Exiting) return true; // swallow duplicates
                if (DecideClose() == CloseOutcome.HideToTray)
                {
                    Phase = ShutdownPhase.Hiding;
                    DoHide();
                    return true;
                }
                Phase = ShutdownPhase.Exiting;
            }
            // Exit proceeds outside the lock (async shutdown below).
            _ = RequestExitAsync();
            return true;
        }

        /// <summary>
        /// Explicit exit (tray Exit, Alt+F4-when-AlwaysExit, app shutdown).
        /// Exactly-once: concurrent/repeated calls share one shutdown task.
        /// </summary>
        public Task RequestExitAsync()
        {
            lock (_lock)
            {
                if (Phase == ShutdownPhase.Exited) return Task.CompletedTask;
                Phase = ShutdownPhase.Exiting;
                if (_exitTask != null) return _exitTask;
                _exitTask = RunShutdownAsync();
                return _exitTask;
            }
        }

        private async Task RunShutdownAsync()
        {
            Interlocked.Increment(ref _shutdownCalls);
            try
            {
                await _shutdownAsync().ConfigureAwait(false);
            }
            catch { }
            finally
            {
                try { _tray.Dispose(); } catch { }
                lock (_lock) { Phase = ShutdownPhase.Exited; }
            }
        }

        /// <summary>Minimize-box handler: hides only when enabled; never stops work.</summary>
        public bool RequestMinimize(bool minimized)
        {
            if (!minimized) return false;
            if (!_getSettings().MinimizeToTray) return false;
            lock (_lock)
            {
                if (Phase == ShutdownPhase.Exiting || Phase == ShutdownPhase.Exited) return false;
                Phase = ShutdownPhase.Hiding;
                DoHide();
                return true;
            }
        }

        /// <summary>Tray open / double-click: restores the same window/session.</summary>
        public void RequestRestore()
        {
            lock (_lock)
            {
                if (Phase == ShutdownPhase.Exiting || Phase == ShutdownPhase.Exited) return;
                Phase = ShutdownPhase.Running;
            }
            try { _showWindow(); } catch { }
        }

        private void DoHide()
        {
            // Called with _lock held: side effects are best-effort window/tray ops.
            try { _tray.Show(); } catch { }
            try { _hideWindow(); } catch { }
            if (!_balloonShown)
            {
                _balloonShown = true;
                try { _tray.NotifyFirstHide(); } catch { }
            }
        }
    }
}
