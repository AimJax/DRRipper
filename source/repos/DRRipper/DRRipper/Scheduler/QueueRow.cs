using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// UI-friendly projection of a <see cref="DownloadJob"/> (Ticket #005 §23).
    /// WPF-independent (plain INotifyPropertyChanged); MainWindow marshals scheduler
    /// events onto the Dispatcher and updates these rows. Throttled by the
    /// scheduler's own event coalescing — never per-network-read.
    /// </summary>
    public sealed class QueueRow : INotifyPropertyChanged
    {
        public Guid JobId { get; }

        private string _displayName = string.Empty;
        private string _url = string.Empty;
        private string _status = string.Empty;
        private double _progress;
        private string _bytes = string.Empty;
        private string _speed = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public QueueRow(Guid jobId) => JobId = jobId;

        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string Url { get => _url; set => Set(ref _url, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public double Progress { get => _progress; set => Set(ref _progress, value); }
        public string Bytes { get => _bytes; set => Set(ref _bytes, value); }
        public string Speed { get => _speed; set => Set(ref _speed, value); }

        public static QueueRow FromJob(DownloadJob job)
        {
            var row = new QueueRow(job.JobId);
            row.Apply(job, 0.0);
            return row;
        }

        /// <summary>Refreshes fields from a job snapshot. Speed EMA supplied by caller.</summary>
        public void Apply(DownloadJob job, double speedMBps)
        {
            DisplayName = job.ResolvedFileName ?? job.RequestedFileName ?? ShortUrl(job.OriginalUrl);
            Url = job.OriginalUrl;
            Status = job.State.ToString() + (job.State == JobState.Failed && !string.IsNullOrEmpty(job.FailureReason) ? $" — {TrimReason(job.FailureReason)}" : string.Empty);
            Progress = job.TotalBytes > 0 ? Math.Clamp(job.CompletedBytes * 100.0 / job.TotalBytes, 0.0, 100.0) : 0.0;
            Bytes = $"{FormatBytes(job.CompletedBytes)} / {(job.TotalBytes > 0 ? FormatBytes(job.TotalBytes) : "?")}";
            Speed = $"{Math.Max(0.0, speedMBps):F2} MB/s";
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); } catch { }
        }

        private static string ShortUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                var name = Uri.UnescapeDataString(System.IO.Path.GetFileName(u.LocalPath));
                if (!string.IsNullOrWhiteSpace(name)) return name;
                return u.Host + u.LocalPath;
            }
            catch { return url.Length > 60 ? url.Substring(0, 60) + "…" : url; }
        }

        private static string TrimReason(string reason)
            => reason.Length > 80 ? reason.Substring(0, 80) + "…" : reason;

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
            if (bytes >= GB) return $"{(double)bytes / GB:0.##} GB";
            if (bytes >= MB) return $"{(double)bytes / MB:0.##} MB";
            if (bytes >= KB) return $"{(double)bytes / KB:0.##} KB";
            return bytes + " B";
        }
    }
}
