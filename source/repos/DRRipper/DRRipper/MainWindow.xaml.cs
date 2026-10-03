using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DRRipper.Scheduler;
using DRRipper.UI;
using DRRipper.Views;
using WpfDataGrid = System.Windows.Controls.DataGrid;

namespace DRRipper
{
    /// <summary>
    /// Thin composition root (Ticket #006 §4/§45, #006.1 tray + close-safety,
    /// #007 browser bridge): builds settings → store → scheduler → view-model →
    /// tray → close controller → browser bridge, syncs DataGrid selection,
    /// routes column sorting into the VM, and hosts dialogs.
    /// Close/minimize/exit policy lives in <see cref="CloseController"/> (headless
    /// tested); bridge enqueue policy lives in <see cref="BrowserBridgeService"/>
    /// (headless tested); this class only performs window/tray side effects.
    /// No queue business logic lives here — all of it is in
    /// <see cref="MainViewModel"/> / <see cref="DownloadScheduler"/>.
    /// </summary>
    public partial class MainWindow : Window
    {
        private SettingsService? _settingsService;
        private JobStore? _store;
        private DownloadScheduler? _scheduler;
        private MainViewModel? _vm;
        private ITrayService? _tray;
        private TrayMenuRouter? _trayRouter;
        private CloseController? _closeController;
        private BrowserBridgeService? _browserBridge;
        private AppSettings _settings = AppSettings.Defaults();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            StateChanged += MainWindow_StateChanged;
        }

        /// <summary>Test/alt composition (internal): same wiring, injectable paths.</summary>
        internal MainWindow(
            SettingsService settingsService,
            JobStore store,
            DownloadScheduler scheduler,
            MainViewModel vm)
            : this()
        {
            _settingsService = settingsService;
            _store = store;
            _scheduler = scheduler;
            _vm = vm;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_vm != null) return; // injected composition (tests)
            try
            {
                _settingsService = new SettingsService();
                _settings = await _settingsService.LoadAsync();
                _store = await JobStore.CreateAsync(SchedulerPaths.DefaultDatabasePath());
                _scheduler = new DownloadScheduler(_store, new SchedulerSettings
                {
                    ActiveDownloadLimit = _settings.ActiveDownloadLimit,
                    GlobalConnectionBudget = _settings.GlobalConnectionBudget,
                    PerHostConnectionBudget = _settings.PerHostConnectionBudget,
                });
                var vm = new MainViewModel(
                    _scheduler,
                    new WpfDispatcher(Dispatcher),
                    shell: new ShellService(),
                    clipboard: new ClipboardService(),
                    showAddDialog: v => new WpfDialogService(this).ShowAddDownloads(v),
                    showSettingsDialog: v => new WpfDialogService(this).ShowSettings(v),
                    refreshMs: _settings.UiRefreshMs);
                vm.ApplySettings(_settings, _settings.UiRefreshMs);
                vm.FocusSearchRequested += () => { try { SearchBox?.Focus(); } catch { } };
                vm.PropertyChanged += Vm_PropertyChanged;
                _vm = vm;
                DataContext = _vm;
                CreateTray();
                CreateCloseController();
                CreateBrowserBridge();
                ApplyWindowPlacement();
                await _vm.InitializeAsync();
                if (App.LaunchOptions.Background)
                {
                    // Native-host background launch (§29): no window flash; the
                    // bridge is already serving, jobs enqueue into the tray app.
                    try
                    {
                        _tray?.Show();
                        Hide();
                        ShowInTaskbar = false;
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(_vm.StartupError))
                    System.Windows.MessageBox.Show(_vm.StartupError, "DRRipper",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("DRRipper failed to start the download queue:" + Environment.NewLine + UI.Formatting.FormatError(ex.Message),
                    "DRRipper", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- browser bridge composition (Ticket #007 §13) ----------

        private void CreateBrowserBridge()
        {
            try { _browserBridge?.Dispose(); } catch { }
            _browserBridge = null;
            var scheduler = _scheduler;
            if (scheduler == null)
                return;
            try
            {
                var bridge = new BrowserBridgeService(
                    scheduler,
                    () => string.IsNullOrWhiteSpace(_settings.DefaultDownloadDirectory)
                        ? AppSettings.Defaults().DefaultDownloadDirectory
                        : _settings.DefaultDownloadDirectory);
                bridge.Start();
                _browserBridge = bridge;
            }
            catch { }
        }

        // ---------- tray + close composition ----------

        private void CreateTray()
        {
            try { _tray?.Dispose(); } catch { }
            var tray = new WindowsTrayService();
            tray.OpenRequested += () => { try { _closeController?.RequestRestore(); } catch { } };
            tray.AddRequested += () => { try { Dispatcher.BeginInvoke(new Action(() => { try { _vm?.AddCommand.Execute(null); } catch { } })); } catch { } };
            tray.ExitRequested += () => { try { _ = _closeController?.RequestExitAsync(); } catch { } };
            _tray = tray;
            _trayRouter = _vm == null ? null : new TrayMenuRouter(_vm);
            _trayRouter?.Attach(tray);
        }

        private void CreateCloseController()
        {
            var vm = _vm;
            var tray = _tray;
            if (vm == null || tray == null) return;
            _closeController = new CloseController(
                hasActiveWork: () => { try { return vm.HasActiveTransfers; } catch { return false; } },
                getSettings: () => _settings,
                tray: tray,
                shutdownAsync: ShutdownForExitAsync,
                hideWindow: () =>
                {
                    try
                    {
                        Hide();
                        ShowInTaskbar = false;
                    }
                    catch { }
                },
                showWindow: () =>
                {
                    try
                    {
                        Show();
                        if (WindowState == WindowState.Minimized)
                            WindowState = WindowState.Normal;
                        ShowInTaskbar = true;
                        Activate();
                    }
                    catch { }
                });
        }

        /// <summary>
        /// The ONE real shutdown path (Ticket #006.1 §11/§12): placement capture +
        /// settings save first (§18), then preservation shutdown, then disposal.
        /// The controller guarantees exactly-once; the tray is disposed there too.
        /// </summary>
        private async Task ShutdownForExitAsync()
        {
            CaptureWindowPlacement();
            try
            {
                if (_settingsService != null)
                    await _settingsService.SaveAsync(_settings);
            }
            catch { }
            var vm = _vm;
            if (vm != null)
            {
                try { await vm.ShutdownAsync(); } catch { }
            }
            try { _trayRouter?.Dispose(); } catch { }
            _trayRouter = null;
            try
            {
                if (_browserBridge != null)
                    await _browserBridge.StopAsync();
            }
            catch { }
            try { _browserBridge?.Dispose(); } catch { }
            _browserBridge = null;
            try { _scheduler?.Dispose(); } catch { }
            try { _store?.Dispose(); } catch { }
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { System.Windows.Application.Current?.Shutdown(); }
                    catch
                    {
                        try { Close(); } catch { }
                    }
                }));
            }
            catch { }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            var controller = _closeController;
            if (controller == null)
            {
                // Pre-composition close (shouldn't happen): fall back to legacy
                // bounded preservation shutdown.
                if (_vm == null) return;
                e.Cancel = true;
                _ = LegacyShutdownAsync();
                return;
            }
            if (controller.Phase == ShutdownPhase.Exited) return; // final close proceeds
            e.Cancel = true;
            try { controller.RequestClose(); } catch { }
        }

        private async Task LegacyShutdownAsync()
        {
            try { if (_vm != null) await _vm.ShutdownAsync(); } catch { }
            try { _trayRouter?.Dispose(); } catch { }
            _trayRouter = null;
            try
            {
                if (_browserBridge != null)
                    await _browserBridge.StopAsync();
            }
            catch { }
            try { _browserBridge?.Dispose(); } catch { }
            _browserBridge = null;
            try { _scheduler?.Dispose(); } catch { }
            try { _store?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { Dispatcher.BeginInvoke(new Action(() => { try { Close(); } catch { } })); } catch { }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            try { _closeController?.RequestMinimize(WindowState == WindowState.Minimized); } catch { }
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Throttled tray status mirrors the coalesced refresh tick (§15/§24):
            // no extra timers, no per-event tooltip writes (service throttles to 5 s).
            if (e.PropertyName == nameof(MainViewModel.ActiveJobs) ||
                e.PropertyName == nameof(MainViewModel.AggregateMBps))
            {
                try
                {
                    var vm = _vm;
                    if (vm != null && _tray != null && !IsShuttingDown(vm))
                        _tray.UpdateStatus(vm.ActiveJobs, vm.AggregateMBps);
                }
                catch { }
            }
        }

        private static bool IsShuttingDown(MainViewModel vm)
        {
            try { return vm.IsShuttingDown; } catch { return false; }
        }

        // ---------- window placement (§17/§18) ----------

        private void CaptureWindowPlacement()
        {
            try
            {
                var p = new WindowPlacement
                {
                    Width = Width,
                    Height = Height,
                    Left = Left,
                    Top = Top,
                    Maximized = WindowState == WindowState.Maximized,
                };
                // Minimized is never persisted (§17); capture the restored bounds instead.
                if (WindowState == WindowState.Minimized)
                {
                    p.Maximized = false;
                    p.Left = RestoreBounds.Left;
                    p.Top = RestoreBounds.Top;
                    p.Width = RestoreBounds.Width;
                    p.Height = RestoreBounds.Height;
                }
                p.ApplyTo(_settings);
            }
            catch { }
        }

        private void ApplyWindowPlacement()
        {
            try
            {
                if (!_settings.RememberWindowPlacement) return;
                var areas = new List<WindowPlacement.WorkArea>();
                try
                {
                    foreach (var s in System.Windows.Forms.Screen.AllScreens)
                    {
                        var w = s.WorkingArea;
                        areas.Add(new WindowPlacement.WorkArea(w.Left, w.Top, w.Width, w.Height));
                    }
                }
                catch { }
                var placement = WindowPlacement.FromSettings(_settings).Normalized(areas);
                Width = placement.Width;
                Height = placement.Height;
                if (!double.IsNaN(placement.Left) && !double.IsNaN(placement.Top))
                {
                    Left = placement.Left;
                    Top = placement.Top;
                }
                if (placement.Maximized)
                    WindowState = WindowState.Maximized;
            }
            catch { }
        }

        // ---------- view event routing (visual only) ----------

        private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var vm = _vm;
            if (vm == null || sender is not WpfDataGrid grid) return;
            var ids = new List<Guid>();
            try
            {
                foreach (var item in grid.SelectedItems)
                    if (item is DownloadJobViewModel row) ids.Add(row.JobId);
            }
            catch { }
            try { vm.SyncSelection(ids); } catch { }
        }

        private void QueueGrid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
        {
            // Visual sort only (§13): route into the VM, never touch QueuePosition.
            var vm = _vm;
            if (vm == null) { e.Handled = true; return; }
            try
            {
                var col = e.Column?.SortMemberPath ?? e.Column?.Header?.ToString() ?? string.Empty;
                var mapped = col switch
                {
                    "State" => JobSortColumn.Status,
                    "FileName" => JobSortColumn.Name,
                    "Progress" => JobSortColumn.Progress,
                    "TotalBytes" => JobSortColumn.Size,
                    "SpeedMBps" => JobSortColumn.Speed,
                    "Added" => JobSortColumn.Added,
                    "Host" => JobSortColumn.Host,
                    _ => (JobSortColumn?)null,
                };
                if (mapped == null) { e.Handled = true; return; }
                if (vm.SortColumn == mapped.Value)
                    vm.SortDescending = !vm.SortDescending;
                else
                {
                    vm.SortColumn = mapped.Value;
                    vm.SortDescending = false;
                }
                foreach (var c in QueueGrid.Columns) c.SortDirection = null;
                e.Column.SortDirection = vm.SortDescending
                    ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }
            catch { }
            e.Handled = true;
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = _vm;
            if (vm == null || _settingsService == null) return;
            try { await vm.ShowSettingsAsync(v => new WpfDialogService(this).ShowSettings(v), _settingsService); } catch { }
            try { _settings = vm.Settings; } catch { }
            // NOTE: placement is captured + saved at real exit (§18), never here,
            // so in-memory values cannot drift past the save.
        }
    }
}
