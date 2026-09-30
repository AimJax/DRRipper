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

            var url = urlTextBox?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                System.Windows.MessageBox.Show("Please enter a valid URL (include protocol or leave blank to auto-prefix https).", "DRRipper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(url))
            {
                System.Windows.MessageBox.Show("Please enter a URL.", "DRRipper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

                _cts = new CancellationTokenSource();
                _downloader = new ParallelDownloader();
                _downloader.StateChanged += st => Dispatcher.Invoke(() => UpdateButtonsForState(st));
                _downloader.ProgressChanged += progress => Dispatcher.Invoke(() =>
                {
                    var overallBar = (System.Windows.Controls.Primitives.RangeBase?)FindName("OverallProgressBar");
                    var speedLabel = (System.Windows.Controls.TextBlock?)FindName("SpeedLabel");
                    var bytesLabel = (System.Windows.Controls.TextBlock?)FindName("BytesLabel");
                    var stateLbl = (System.Windows.Controls.TextBlock?)FindName("StateLabel");

                    if (overallBar != null) overallBar.Value = progress.ProgressPercentage;
                    if (speedLabel != null) speedLabel.Text = $"{progress.CurrentSpeedMBps:F2} MB/s";
                    if (bytesLabel != null) bytesLabel.Text = $"{FormatBytes(progress.TotalBytesDownloaded)} / {(progress.TotalBytes>0?FormatBytes(progress.TotalBytes):"?")}";
                    if (stateLbl != null)
                    {
                        if (!string.IsNullOrWhiteSpace(progress.ResolvedFileName))
                            stateLbl.Text = $"{progress.StatusMessage} ({progress.ResolvedFileName})";
                        else
                            stateLbl.Text = progress.StatusMessage;
                    }
                });

                UpdateButtonsForState(DownloadState.Downloading);

                var finalPath = await _downloader.StartAsync(url, saveDir, 32, _cts.Token);

                Dispatcher.Invoke(() =>
                {
                    var stateLbl = (System.Windows.Controls.TextBlock?)FindName("StateLabel");
                    var overallBar = (System.Windows.Controls.Primitives.RangeBase?)FindName("OverallProgressBar");
                    if (stateLbl != null) stateLbl.Text = $"Completed ({System.IO.Path.GetFileName(finalPath)})";
                    if (overallBar != null) overallBar.Value = 100;
                });

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
                _downloader?.Cancel();
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
