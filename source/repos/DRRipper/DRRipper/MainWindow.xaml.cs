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
    /// Thin composition root (Ticket #006 §4/§45): builds settings → store →
    /// scheduler → view-model, hosts dialogs, syncs DataGrid selection, routes
    /// column sorting into the VM, and drives orderly shutdown. No queue business
    /// logic lives here — all of it is in <see cref="MainViewModel"/>.
    /// </summary>
    public partial class MainWindow : Window
    {
        private SettingsService? _settingsService;
        private JobStore? _store;
        private DownloadScheduler? _scheduler;
        private MainViewModel? _vm;
        private AppSettings _settings = AppSettings.Defaults();
        private bool _shutdownStarted;
        private bool _shutdownDone;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
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
                _vm = vm;
                DataContext = _vm;
                ApplyWindowPlacement();
                await _vm.InitializeAsync();
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

        private async void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_shutdownDone) return;
            if (_vm == null) return;
            // Bounded preservation shutdown (§27): show state, never freeze, never cancel jobs.
            // NOTE: Window.Close() may not be called re-entrantly from inside Closing
            // (InvalidOperationException), so the final close is deferred to the Dispatcher.
            e.Cancel = true;
            if (_shutdownStarted) return;
            _shutdownStarted = true;
            try { await _vm.ShutdownAsync(); } catch { }
            try { _scheduler?.Dispose(); } catch { }
            try { _store?.Dispose(); } catch { }
            _shutdownDone = true;
            try { Dispatcher.BeginInvoke(new Action(() => { try { Close(); } catch { } })); } catch { }
        }

        private void ApplyWindowPlacement()
        {
            try
            {
                if (_settings.RememberWindowPlacement)
                {
                    Width = _settings.WindowWidth;
                    Height = _settings.WindowHeight;
                }
            }
            catch { }
        }

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
            try
            {
                _settings = vm.Settings;
                if (_settings.RememberWindowPlacement)
                {
                    _settings.WindowWidth = Width;
                    _settings.WindowHeight = Height;
                }
            }
            catch { }
        }
    }
}
