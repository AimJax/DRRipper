using System;

namespace DRRipper.UI
{
    /// <summary>
    /// System-tray behind an interface (Ticket #006.1 §26). View-models never see
    /// WinForms types; the window wires menu events to existing VM commands.
    /// </summary>
    public interface ITrayService : IDisposable
    {
        event Action? OpenRequested;
        event Action? AddRequested;
        event Action? PauseAllRequested;
        event Action? ResumeAllRequested;
        event Action? ExitRequested;

        /// <summary>Show the icon (idempotent).</summary>
        void Show();
        /// <summary>Hide the icon without disposing (idempotent).</summary>
        void Hide();
        /// <summary>Conservatively refresh tooltip/menu status (throttled internally).</summary>
        void UpdateStatus(int activeCount, double aggregateMBps);
        /// <summary>First-hide hint, once per session at most.</summary>
        void NotifyFirstHide();
        bool IsVisible { get; }
        bool IsDisposed { get; }
    }

    /// <summary>
    /// Tray status text + throttle policy, shared by prod and fake services so the
    /// rules are unit-tested once (Ticket #006.1 §15/§26). No URLs, no filenames.
    /// </summary>
    public static class TrayStatusFormatter
    {
        /// <summary>Minimum interval between tooltip writes.</summary>
        public static readonly TimeSpan ThrottleInterval = TimeSpan.FromSeconds(5);

        public static string FormatTooltip(int activeCount, double aggregateMBps)
        {
            // Windows NotifyIcon.Text caps at 63 chars; keep it far shorter.
            if (activeCount <= 0) return "DRRipper — queue idle";
            string text = $"DRRipper — {activeCount} active — {Formatting.FormatRate(aggregateMBps * 1024.0 * 1024.0)}";
            return text.Length > 60 ? text.Substring(0, 60) : text;
        }

        public static bool ShouldUpdate(DateTimeOffset lastWriteUtc, DateTimeOffset nowUtc)
            => nowUtc - lastWriteUtc >= ThrottleInterval;
    }

    /// <summary>
    /// Headless/test tray: records visibility, status pushes, and disposal;
    /// menu actions surface as raisable events. No windows, no timers.
    /// </summary>
    public sealed class FakeTrayService : ITrayService
    {
        private int _showCount;
        private int _hideCount;
        private int _statusCount;
        private int _disposed;
        private string _tooltip = string.Empty;

        public event Action? OpenRequested;
        public event Action? AddRequested;
        public event Action? PauseAllRequested;
        public event Action? ResumeAllRequested;
        public event Action? ExitRequested;

        public bool IsVisible { get; private set; }
        public bool IsDisposed => _disposed != 0;
        public int ShowCount => _showCount;
        public int HideCount => _hideCount;
        public int StatusCount => _statusCount;
        public string Tooltip => _tooltip;
        public int DisposeCount { get; private set; }

        public void Show() { _showCount++; IsVisible = true; }
        public void Hide() { _hideCount++; IsVisible = false; }

        public void UpdateStatus(int activeCount, double aggregateMBps)
        {
            _statusCount++;
            _tooltip = TrayStatusFormatter.FormatTooltip(activeCount, aggregateMBps);
        }

        public void NotifyFirstHide() { }

        public void RaiseOpen() => OpenRequested?.Invoke();
        public void RaiseAdd() => AddRequested?.Invoke();
        public void RaisePauseAll() => PauseAllRequested?.Invoke();
        public void RaiseResumeAll() => ResumeAllRequested?.Invoke();
        public void RaiseExit() => ExitRequested?.Invoke();

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
                IsVisible = false;
            }
        }
    }
}
