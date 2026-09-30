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
                if (PathTextBox != null && string.IsNullOrWhiteSpace(PathTextBox.Text))
                    PathTextBox.Text = defaultDownloads;
            }
            catch { }

            UpdateButtonsForState(DownloadState.Idle);
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // direct XAML field references (strongly-typed)
            var urlTextBox = UrlTextBox;
            var startButton = StartButton;

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
                var saveDir = (PathTextBox != null && !string.IsNullOrWhiteSpace(PathTextBox.Text) && Directory.Exists(PathTextBox.Text))
                    ? PathTextBox.Text
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                // Build list of URLs from multi-line textbox
                var urlsRaw = UrlTextBox?.Text ?? string.Empty;
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
                long completedFilesTotalSize = 0;
                int totalFiles = list.Count;

                // Create a single downloader for the entire batch to preserve connection pooling, DNS caching, and TLS sessions
                try { _downloader?.Dispose(); } catch { }
                _downloader = new ParallelDownloader();
                _downloader.StateChanged += st => Dispatcher.Invoke(() => UpdateButtonsForState(st));

                int currentIndex = 0;
                string currentUrl = string.Empty;

                _downloader.ProgressChanged += progress => Dispatcher.Invoke(() =>
                {
                    // Per-file progress: show current file percentage and bytes
                    if (OverallProgressBar != null) OverallProgressBar.Value = progress.ProgressPercentage;
                    if (SpeedLabel != null) SpeedLabel.Text = $"{progress.CurrentSpeedMBps:F2} MB/s";
                    if (BytesLabel != null) BytesLabel.Text = $"{FormatBytes(progress.TotalBytesDownloaded)} / {(progress.TotalBytes>0?FormatBytes(progress.TotalBytes):"?")}";
                    if (StateLabel != null)
                    {
                        var displayName = progress.ResolvedFileName ?? new Uri(currentUrl).Segments[^1];
                        StateLabel.Text = $"[{currentIndex+1}/{totalFiles}] {progress.StatusMessage} ({displayName})";
                    }
                });

                for (int i = 0; i < list.Count; i++)
                {
                    if (_cts.IsCancellationRequested) break;
                    currentUrl = list[i];
                    currentIndex = i;


                    UpdateButtonsForState(DownloadState.Downloading);

                    try
                    {
                        var finalPath = await _downloader.StartAsync(currentUrl, saveDir, 8, _cts.Token);
                        // file completed: add its final file size into completedFilesTotalSize
                        try
                        {
                            long finalSize = 0;
                            try { finalSize = new FileInfo(finalPath).Length; } catch { }
                            completedFilesTotalSize += finalSize;
                        }
                        catch { }

                        Dispatcher.Invoke(() =>
                        {
                            if (StateLabel != null) StateLabel.Text = $"[{currentIndex+1}/{totalFiles}] Completed ({System.IO.Path.GetFileName(finalPath)})";
                            if (OverallProgressBar != null) OverallProgressBar.Value = 100.0;
                            if (BytesLabel != null)
                            {
                                try { BytesLabel.Text = $"{FormatBytes(new FileInfo(finalPath).Length)} / {FormatBytes(new FileInfo(finalPath).Length)}"; } catch { }
                            }
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
                    if (StateLabel != null) StateLabel.Text = "Cancelled";
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
                    if (PathTextBox != null && !string.IsNullOrWhiteSpace(PathTextBox.Text) && Directory.Exists(PathTextBox.Text))
                        dlg.SelectedPath = PathTextBox.Text;

                    var result = dlg.ShowDialog();
                    if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        if (PathTextBox != null)
                            PathTextBox.Text = dlg.SelectedPath;
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
            var startButton = StartButton;
            var cancelButton = CancelButton;
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
                if (OverallProgressBar != null) OverallProgressBar.Value = 0;
                if (SpeedLabel != null) SpeedLabel.Text = "0 MB/s";
                if (BytesLabel != null) BytesLabel.Text = "0 / 0";
                if (StateLabel != null) StateLabel.Text = "Idle";
                UpdateButtonsForState(DownloadState.Idle);
            });
        }
    }
}
