using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DRRipper
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;
        private ParallelDownloader? _downloader;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                var defaultDownloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var pathTextBox = (System.Windows.Controls.TextBox?)FindName("PathTextBox");
                if (pathTextBox != null && string.IsNullOrWhiteSpace(pathTextBox.Text))
                    pathTextBox.Text = defaultDownloads;
            }
            catch { }

            UpdateButtonsForState(DownloadState.Idle);
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            var urlTextBox = (System.Windows.Controls.TextBox?)FindName("UrlTextBox");
            var startButton = (System.Windows.Controls.Button?)FindName("StartButton");

            // Batch parsing will validate individual lines; do not pre-validate the whole textbox here.

            // Toggle Pause/Resume if active
            if (_downloader != null)
            {
                var st = _downloader.GetState();
                if (st == DownloadState.Downloading)
                {
                    _downloader.Pause();
                    return;
                }
                else if (st == DownloadState.Paused || st == DownloadState.Retrying)
                {
                    _downloader.Resume();
                    return;
                }
            }

            if (startButton != null) startButton.IsEnabled = false;

            try
            {
                var pathTextBox = (System.Windows.Controls.TextBox?)FindName("PathTextBox");
                var saveDir = (pathTextBox != null && !string.IsNullOrWhiteSpace(pathTextBox.Text) && Directory.Exists(pathTextBox.Text))
                    ? pathTextBox.Text
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                // Build list of URLs from multi-line textbox
                var urlsRaw = ((System.Windows.Controls.TextBox?)FindName("UrlTextBox"))?.Text ?? string.Empty;
                var lines = urlsRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var list = new System.Collections.Generic.List<string>();
                foreach (var ln in lines)
                {
                    var l = ln.Trim();
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    if (!l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        l = "https://" + l;
                    if (Uri.IsWellFormedUriString(l, UriKind.Absolute))
                        list.Add(l);
                }

                if (list.Count == 0)
                {
                    System.Windows.MessageBox.Show("No valid URLs found.", "DRRipper", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _cts = new CancellationTokenSource();

                // Batch tracking variables
                long batchTotalDownloaded = 0;
                int totalFiles = list.Count;

                for (int i = 0; i < list.Count; i++)
                {
                    if (_cts.IsCancellationRequested) break;
                    var currentUrl = list[i];
                    int index = i; // capture loop index for closures

                    // prepare downloader for this URL
                    // Dispose previous downloader before creating a new one to free timers and handlers
                    try { _downloader?.Dispose(); } catch { }
                    _downloader = new ParallelDownloader();
                    _downloader.StateChanged += st => Dispatcher.Invoke(() => UpdateButtonsForState(st));
                    // track last-known downloaded bytes for this file so we can add to batchTotalDownloaded on completion
                    long lastFileDownloaded = 0;
                    _downloader.ProgressChanged += progress => Dispatcher.Invoke(() =>
                    {
                        var overallBar = (System.Windows.Controls.Primitives.RangeBase?)FindName("OverallProgressBar");
                        var speedLabel = (System.Windows.Controls.TextBlock?)FindName("SpeedLabel");
                        var bytesLabel = (System.Windows.Controls.TextBlock?)FindName("BytesLabel");
                        var stateLbl = (System.Windows.Controls.TextBlock?)FindName("StateLabel");

                        // Combined bytes across completed files + current file
                        var combinedDownloaded = batchTotalDownloaded + progress.TotalBytesDownloaded;

                        // Smooth batch percentage: files completed + fraction of current file
                        var overallPercent = ((double)index / totalFiles * 100.0) + (progress.ProgressPercentage / totalFiles);

                        if (overallBar != null) overallBar.Value = overallPercent;
                        if (speedLabel != null) speedLabel.Text = $"{progress.CurrentSpeedMBps:F2} MB/s";
                        if (bytesLabel != null) bytesLabel.Text = $"{FormatBytes(combinedDownloaded)} / {(progress.TotalBytes>0?FormatBytes(batchTotalDownloaded + progress.TotalBytes):"?")}";
                        if (stateLbl != null)
                        {
                            var displayName = progress.ResolvedFileName ?? new Uri(currentUrl).Segments[^1];
                            stateLbl.Text = $"[{index+1}/{totalFiles}] {progress.StatusMessage} ({displayName})";
                        }

                        // remember last seen downloaded for this file
                        try { lastFileDownloaded = progress.TotalBytesDownloaded; } catch { }
                    });

                    UpdateButtonsForState(DownloadState.Downloading);

                    try
                    {
                        var finalPath = await _downloader.StartAsync(currentUrl, saveDir, 8, _cts.Token);
                        // file completed: add its downloaded amount (or total size) into batchTotalDownloaded
                        try
                        {
                            // prefer reported total bytes for completed file if available
                            batchTotalDownloaded += lastFileDownloaded;
                        }
                        catch { }

                        Dispatcher.Invoke(() =>
                        {
                            var stateLbl = (System.Windows.Controls.TextBlock?)FindName("StateLabel");
                            var overallBar = (System.Windows.Controls.Primitives.RangeBase?)FindName("OverallProgressBar");
                            if (stateLbl != null) stateLbl.Text = $"[{index+1}/{totalFiles}] Completed ({System.IO.Path.GetFileName(finalPath)})";
                            if (overallBar != null) overallBar.Value = (index + 1) * 100.0 / totalFiles;
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                // Only mark as Completed if the operation wasn't cancelled
                if (_cts == null || !_cts.IsCancellationRequested)
                    UpdateButtonsForState(DownloadState.Completed);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    var stateLbl = (System.Windows.Controls.TextBlock?)FindName("StateLabel");
                    if (stateLbl != null) stateLbl.Text = "Cancelled";
                });
                UpdateButtonsForState(DownloadState.Cancelled);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"Download failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                UpdateButtonsForState(DownloadState.Failed);
            }
            finally
            {
                if (startButton != null) startButton.IsEnabled = true;
                // Ensure downloader disposed and resources freed when loop completes
                try { _downloader?.Dispose(); } catch { }
                _downloader = null;
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Select download folder";
                    dlg.UseDescriptionForTitle = true;
                    dlg.ShowNewFolderButton = true;
                    var pathTextBox = (System.Windows.Controls.TextBox?)FindName("PathTextBox");
                    if (pathTextBox != null && !string.IsNullOrWhiteSpace(pathTextBox.Text) && Directory.Exists(pathTextBox.Text))
                        dlg.SelectedPath = pathTextBox.Text;

                    var result = dlg.ShowDialog();
                    if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        if (pathTextBox != null)
                            pathTextBox.Text = dlg.SelectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Folder picker failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            if (bytes >= GB) return string.Format("{0:0.##} GB", (double)bytes / GB);
            if (bytes >= MB) return string.Format("{0:0.##} MB", (double)bytes / MB);
            if (bytes >= KB) return string.Format("{0:0.##} KB", (double)bytes / KB);
            return bytes + " B";
        }

        private void UpdateButtonsForState(DownloadState st)
        {
            var startButton = (System.Windows.Controls.Button?)FindName("StartButton");
            var cancelButton = (System.Windows.Controls.Button?)FindName("CancelButton");
            if (startButton != null)
            {
                switch (st)
                {
                    case DownloadState.Idle:
                    case DownloadState.Completed:
                        startButton.Content = "Start Download";
                        startButton.IsEnabled = true;
                        break;
                    case DownloadState.Downloading:
                        startButton.Content = "Pause";
                        startButton.IsEnabled = true;
                        break;
                    case DownloadState.Paused:
                    case DownloadState.Retrying:
                        startButton.Content = "Resume";
                        startButton.IsEnabled = true;
                        break;
                    case DownloadState.Cancelled:
                    case DownloadState.Failed:
                        startButton.Content = "Start Download";
                        startButton.IsEnabled = true;
                        break;
                }
            }
            if (cancelButton != null)
            {
                cancelButton.IsEnabled = st == DownloadState.Downloading || st == DownloadState.Paused || st == DownloadState.Retrying;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cts?.Cancel();
                _downloader?.Cancel();
                // Dispose downloader immediately to free timers and handlers
                try { _downloader?.Dispose(); } catch { }
                _downloader = null;
            }
            catch { }
            Dispatcher.Invoke(() =>
            {
                var overallBar = (System.Windows.Controls.Primitives.RangeBase?)FindName("OverallProgressBar");
                var speedLabel = (System.Windows.Controls.TextBlock?)FindName("SpeedLabel");
                var bytesLabel = (System.Windows.Controls.TextBlock?)FindName("BytesLabel");
                var stateLbl = (System.Windows.Controls.TextBlock?)FindName("StateLabel");
                if (overallBar != null) overallBar.Value = 0;
                if (speedLabel != null) speedLabel.Text = "0 MB/s";
                if (bytesLabel != null) bytesLabel.Text = "0 / 0";
                if (stateLbl != null) stateLbl.Text = "Idle";
                UpdateButtonsForState(DownloadState.Idle);
            });
        }
    }
}
