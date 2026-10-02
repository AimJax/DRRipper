using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DRRipper.UI
{
    /// <summary>
    /// Production tray backed by WinForms NotifyIcon (Ticket #006.1 §5/§9/§14/§15).
    /// Lightweight: one icon, one menu, one 5 s status timer. Lifetime belongs to
    /// window composition (shown on hide, disposed on real exit — never per job).
    /// Menu actions surface as events; the window routes them to existing
    /// ViewModel/scheduler APIs (no duplicated business logic).
    /// </summary>
    public sealed class WindowsTrayService : ITrayService
    {
        private NotifyIcon? _icon;
        private ContextMenuStrip? _menu;
        private System.Threading.Timer? _statusTimer;
        private readonly object _lock = new();
        private int _activeCount;
        private double _aggregateMBps;
        private bool _statusDirty = true;
        private DateTimeOffset _lastWriteUtc = DateTimeOffset.MinValue;
        private bool _balloonShown;
        private int _disposed;

        public event Action? OpenRequested;
        public event Action? AddRequested;
        public event Action? PauseAllRequested;
        public event Action? ResumeAllRequested;
        public event Action? ExitRequested;

        public bool IsVisible { get; private set; }
        public bool IsDisposed => _disposed != 0;

        private void EnsureIcon()
        {
            if (_icon != null) return;
            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = TrayStatusFormatter.FormatTooltip(0, 0),
                Visible = false,
            };
            _menu = new ContextMenuStrip();
            AddItem("Open DRRipper", () => OpenRequested?.Invoke(), bold: true);
            AddItem("Add Downloads…", () => AddRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            AddItem("Pause All", () => PauseAllRequested?.Invoke());
            AddItem("Resume All", () => ResumeAllRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            var status = new ToolStripMenuItem("Queue idle") { Enabled = false, Name = "TrayStatusItem" };
            _menu.Items.Add(status);
            _menu.Items.Add(new ToolStripSeparator());
            AddItem("Exit DRRipper", () => ExitRequested?.Invoke());
            _icon.ContextMenuStrip = _menu;
            _icon.DoubleClick += (_, __) => { try { OpenRequested?.Invoke(); } catch { } };
        }

        private void AddItem(string text, Action onClick, bool bold = false)
        {
            var item = new ToolStripMenuItem(text);
            if (bold) item.Font = new Font(item.Font, System.Drawing.FontStyle.Bold);
            item.Click += (_, __) => { try { onClick(); } catch { } };
            _menu!.Items.Add(item);
        }

        public void Show()
        {
            lock (_lock)
            {
                if (_disposed != 0) return;
                EnsureIcon();
                FlushStatusLocked(force: true);
                _icon!.Visible = true;
                IsVisible = true;
            }
        }

        public void Hide()
        {
            lock (_lock)
            {
                if (_disposed != 0) return;
                if (_icon != null) _icon.Visible = false;
                IsVisible = false;
            }
        }

        public void UpdateStatus(int activeCount, double aggregateMBps)
        {
            lock (_lock)
            {
                _activeCount = activeCount;
                _aggregateMBps = aggregateMBps;
                _statusDirty = true;
                _statusTimer ??= new System.Threading.Timer(_ =>
                {
                    lock (_lock)
                    {
                        if (_disposed != 0 || !_statusDirty || _icon == null) return;
                        FlushStatusLocked(force: false);
                    }
                }, null, TrayStatusFormatter.ThrottleInterval, TrayStatusFormatter.ThrottleInterval);
            }
        }

        private void FlushStatusLocked(bool force)
        {
            if (_icon == null) return;
            var now = DateTimeOffset.UtcNow;
            if (!force && !TrayStatusFormatter.ShouldUpdate(_lastWriteUtc, now)) return;
            _lastWriteUtc = now;
            _statusDirty = false;
            try { _icon.Text = TrayStatusFormatter.FormatTooltip(_activeCount, _aggregateMBps); } catch { }
            try
            {
                if (_menu?.Items["TrayStatusItem"] is ToolStripMenuItem status)
                    status.Text = _activeCount > 0
                        ? $"{_activeCount} active — {Formatting.FormatRate(_aggregateMBps * 1024.0 * 1024.0)}"
                        : "Queue idle";
            }
            catch { }
        }

        public void NotifyFirstHide()
        {
            lock (_lock)
            {
                if (_disposed != 0 || _balloonShown) return;
                _balloonShown = true;
                try
                {
                    EnsureIcon();
                    _icon!.ShowBalloonTip(3000, "DRRipper",
                        "DRRipper is still running in the system tray.", ToolTipIcon.Info);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_lock)
            {
                IsVisible = false;
                try { _statusTimer?.Dispose(); } catch { }
                _statusTimer = null;
                try
                {
                    if (_icon != null)
                    {
                        _icon.Visible = false;
                        _icon.Dispose();
                    }
                }
                catch { }
                _icon = null;
                try { _menu?.Dispose(); } catch { }
                _menu = null;
            }
        }
    }
}
