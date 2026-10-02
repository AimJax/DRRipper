using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DRRipper.Scheduler;

namespace DRRipper
{
    /// <summary>
    /// Thin scheduler host (Ticket #005 §23): owns the persistent DownloadScheduler,
    /// marshals its UI-framework-independent events onto the Dispatcher, and binds
    /// lightweight QueueRows. No transfer logic lives here. Full MVVM is a later milestone.
    /// </summary>
    public partial class MainWindow : Window
    {
        private JobStore? _store;
        private DownloadScheduler? _scheduler;
        private readonly ObservableCollection<QueueRow> _rows = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (long Bytes, DateTimeOffset At, double Speed)> _speeds = new();
        private bool _closing;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                var defaultDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (PathTextBox != null && string.IsNullOrWhiteSpace(PathTextBox.Text))
                    PathTextBox.Text = defaultDownloads;
            }
            catch { }
            if (QueueListView != null) QueueListView.ItemsSource = _rows;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var dbPath = SchedulerPaths.DefaultDatabasePath();
                _store = await JobStore.CreateAsync(dbPath);
                _scheduler = new DownloadScheduler(_store, new SchedulerSettings
                {
                    ActiveDownloadLimit = 3,
                    GlobalConnectionBudget = 16,
                    PerHostConnectionBudget = 8,
                });
                _scheduler.JobAdded += OnJobAdded;
                _scheduler.JobUpdated += OnJobUpdated;
                _scheduler.JobRemoved += OnJobRemoved;
                _scheduler.JobProgress += OnJobProgress;
                await _scheduler.StartAsync();
                await RefreshAllAsync();
                SetStatus("Queue ready.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Queue database failed to open:{Environment.NewLine}{ex.Message}", "DRRipper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                SetStatus("Queue unavailable.");
            }
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closing) return;
            _closing = true;
            // Orderly shutdown preserves partials for resume (§20). Brief wait only.
            try
            {
                if (_scheduler != null)
                {
                    var stop = _scheduler.StopAsync();
                    var winner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10)));
                }
            }
            catch { }
            try { _scheduler?.Dispose(); } catch { }
            try { _store?.Dispose(); } catch { }
        }

        // ---------- scheduler event marshaling (Dispatcher only here, never in engine) ----------

        private void OnJobAdded(object? s, DownloadJob job) =>
            Dispatcher.BeginInvoke(() => UpsertRow(job));

        private void OnJobUpdated(object? s, DownloadJob job) =>
            Dispatcher.BeginInvoke(() => UpsertRow(job));

        private void OnJobRemoved(object? s, Guid jobId) =>
            Dispatcher.BeginInvoke(() =>
            {
                var row = _rows.FirstOrDefault(r => r.JobId == jobId);
                if (row != null) _rows.Remove(row);
                _speeds.TryRemove(jobId, out _);
                UpdateSummary();
            });

        private void OnJobProgress(object? s, JobProgressEvent p) =>
            Dispatcher.BeginInvoke(() =>
            {
                var row = _rows.FirstOrDefault(r => r.JobId == p.JobId);
                if (row == null) return;
                var now = DateTimeOffset.UtcNow;
                double speed = 0.0;
                if (_speeds.TryGetValue(p.JobId, out var prev) && prev.Bytes <= p.CompletedBytes)
                {
                    var dt = (now - prev.At).TotalSeconds;
                    if (dt > 0.2)
                    {
                        var inst = (p.CompletedBytes - prev.Bytes) / 1024.0 / 1024.0 / dt;
                        speed = prev.Speed * 0.7 + Math.Max(0.0, inst) * 0.3;
                    }
                    else speed = prev.Speed;
                }
                _speeds[p.JobId] = (p.CompletedBytes, now, speed);
                row.Progress = p.TotalBytes.GetValueOrDefault() > 0
                    ? Math.Clamp(p.CompletedBytes * 100.0 / p.TotalBytes!.Value, 0.0, 100.0) : row.Progress;
                row.Bytes = $"{FormatBytes(p.CompletedBytes)} / {(p.TotalBytes > 0 ? FormatBytes(p.TotalBytes!.Value) : "?")}";
                row.Speed = $"{Math.Max(0.0, speed):F2} MB/s";
            });

        private void UpsertRow(DownloadJob job)
        {
            var row = _rows.FirstOrDefault(r => r.JobId == job.JobId);
            double speed = _speeds.TryGetValue(job.JobId, out var s) ? s.Speed : 0.0;
            if (row == null)
            {
                _rows.Add(QueueRow.FromJob(job));
            }
            else
            {
                row.Apply(job, speed);
            }
            // Keep FIFO display order.
            var ordered = _rows.OrderBy(r => r.JobId).ToList();
            UpdateSummary();
        }

        private async Task RefreshAllAsync()
        {
            if (_scheduler == null) return;
            var all = await _scheduler.GetAllJobsAsync();
            _rows.Clear();
            foreach (var j in all.OrderBy(j => j.Priority).ThenBy(j => j.QueuePosition))
                _rows.Add(QueueRow.FromJob(j));
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (QueueSummaryLabel == null) return;
            int active = 0, done = 0;
            foreach (var r in _rows)
            {
                if (r.Status.StartsWith("Downloading", StringComparison.Ordinal)) active++;
                if (r.Status.StartsWith("Completed", StringComparison.Ordinal)) done++;
            }
            QueueSummaryLabel.Text = $"{_rows.Count} jobs ({active} active, {done} done)";
        }

        private void SetStatus(string text)
        {
            try { if (StateLabel != null) StateLabel.Text = text; } catch { }
        }

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
            if (bytes >= GB) return $"{(double)bytes / GB:0.##} GB";
            if (bytes >= MB) return $"{(double)bytes / MB:0.##} MB";
            if (bytes >= KB) return $"{(double)bytes / KB:0.##} KB";
            return bytes + " B";
        }

        // ---------- commands ----------

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) { SetStatus("Queue not ready."); return; }
            var saveDir = (PathTextBox != null && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                ? PathTextBox.Text.Trim() : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            try { Directory.CreateDirectory(saveDir); } catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Cannot use save folder:{Environment.NewLine}{ex.Message}", "DRRipper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            var text = UrlTextBox?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                System.Windows.MessageBox.Show("Enter one URL per line.", "DRRipper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            try
            {
                var result = await _scheduler.ImportAsync(text, saveDir, connectionsPerFile: 8);
                SetStatus($"Added {result.Accepted}, rejected {result.Rejected}.");
                if (result.Rejected > 0)
                    System.Windows.MessageBox.Show($"Accepted {result.Accepted}, rejected {result.Rejected}.{Environment.NewLine}{string.Join(Environment.NewLine, result.Errors.Take(10))}",
                        "DRRipper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Import failed: {ex.Message}", "DRRipper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private System.Collections.Generic.List<Guid> SelectedIds()
        {
            var ids = new System.Collections.Generic.List<Guid>();
            try
            {
                foreach (var item in QueueListView.SelectedItems)
                    if (item is QueueRow r) ids.Add(r.JobId);
            }
            catch { }
            return ids;
        }

        private async void PauseResumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) return;
            foreach (var id in SelectedIds())
            {
                try
                {
                    var job = await _scheduler.GetJobAsync(id);
                    if (job == null) continue;
                    if (job.State == JobState.Paused || job.State == JobState.Interrupted)
                        await _scheduler.ResumeJobAsync(id);
                    else if (!job.IsTerminal)
                        await _scheduler.PauseJobAsync(id);
                }
                catch { }
            }
        }

        private async void CancelJobButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) return;
            foreach (var id in SelectedIds())
            {
                try { await _scheduler.CancelJobAsync(id); } catch { }
            }
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) return;
            foreach (var id in SelectedIds())
            {
                try { await _scheduler.RetryJobAsync(id); } catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "DRRipper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
            }
        }

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) return;
            var ids = SelectedIds();
            if (ids.Count == 0) return;
            var answer = System.Windows.MessageBox.Show($"Remove {ids.Count} job(s)? Partial files are deleted; completed files on disk are kept.",
                "DRRipper", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
            foreach (var id in ids)
            {
                try { await _scheduler.RemoveJobAsync(id); } catch { }
            }
        }

        private async void PauseAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) return;
            try { await _scheduler.PauseAllAsync(); SetStatus("Paused all."); } catch { }
        }

        private async void ResumeAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) return;
            try { await _scheduler.ResumeAllAsync(); SetStatus("Resumed all."); } catch { }
        }

        private async void ClearDoneButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduler == null) return;
            try { await _scheduler.ClearCompletedAsync(); await RefreshAllAsync(); } catch { }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = "Select download folder";
                dlg.UseDescriptionForTitle = true;
                dlg.ShowNewFolderButton = true;
                if (PathTextBox != null && !string.IsNullOrWhiteSpace(PathTextBox.Text) && Directory.Exists(PathTextBox.Text))
                    dlg.SelectedPath = PathTextBox.Text;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && PathTextBox != null)
                    PathTextBox.Text = dlg.SelectedPath;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Folder picker failed: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void QueueListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PauseResumeButton == null) return;
            var first = QueueListView.SelectedItems.Count > 0 ? QueueListView.SelectedItems[0] as QueueRow : null;
            PauseResumeButton.Content = first != null && (first.Status.StartsWith("Paused", StringComparison.Ordinal) || first.Status.StartsWith("Interrupted", StringComparison.Ordinal))
                ? "Resume" : "Pause";
        }
    }
}
