using System;
using DRRipper.Scheduler;

namespace DRRipper.UI
{
    /// <summary>
    /// Per-row presentation model (Ticket #006). Wraps a <see cref="DownloadJob"/>
    /// snapshot plus smoothed speed/ETA. No timers, no subscriptions, no engine or
    /// DB access — the parent MainViewModel pushes all updates. Lightweight by
    /// design: 10k instances are plain data + one event each.
    /// </summary>
    public sealed class DownloadJobViewModel : ObservableObject
    {
        public Guid JobId { get; }

        private string _fileName = string.Empty;
        private string _url = string.Empty;
        private string _host = string.Empty;
        private JobState _state;
        private string _statusText = string.Empty;
        private string _statusGlyph = string.Empty;
        private double _progress;
        private long _completedBytes;
        private long _totalBytes = -1;
        private double _speedMBps;
        private string _speedText = string.Empty;
        private string _etaText = "—";
        private string _bytesText = string.Empty;
        private string _failureReason = string.Empty;
        private int _connections;
        private DateTimeOffset _added;
        private string _targetPath = string.Empty;

        private long _lastBytes = -1;
        private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

        public DownloadJobViewModel(Guid jobId) => JobId = jobId;

        public string FileName { get => _fileName; private set => Set(ref _fileName, value); }
        public string Url { get => _url; private set => Set(ref _url, value); }
        public string Host { get => _host; private set => Set(ref _host, value); }
        public JobState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusGlyph)); Raise(nameof(IsActive)); Raise(nameof(IsTerminal)); } } }
        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
        public string StatusGlyph { get => _statusGlyph; private set => Set(ref _statusGlyph, value); }
        public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Raise(nameof(ProgressText)); } }
        public long CompletedBytes { get => _completedBytes; private set => Set(ref _completedBytes, value); }
        public long TotalBytes { get => _totalBytes; private set => Set(ref _totalBytes, value); }
        public double SpeedMBps { get => _speedMBps; private set => Set(ref _speedMBps, value); }
        public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
        public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
        public string BytesText { get => _bytesText; private set => Set(ref _bytesText, value); }
        public string ProgressText => TotalBytes > 0 ? $"{Progress:0.0}%" : "—";
        public string FailureReason { get => _failureReason; private set => Set(ref _failureReason, value); }
        public int Connections { get => _connections; private set => Set(ref _connections, value); }
        public DateTimeOffset Added { get => _added; private set => Set(ref _added, value); }
        public string TargetPath { get => _targetPath; private set => Set(ref _targetPath, value); }

        public bool IsActive => State == JobState.Downloading || State == JobState.Pausing || State == JobState.Retrying;
        public bool IsTerminal => State == JobState.Completed || State == JobState.Failed || State == JobState.Cancelled;

        public bool CanPause => !IsTerminal && State != JobState.Paused;
        public bool CanResume => State == JobState.Paused || State == JobState.Interrupted;
        public bool CanCancel => !IsTerminal;
        public bool CanRetry => State == JobState.Failed || State == JobState.Cancelled;

        /// <summary>Full snapshot refresh (state changes, restarts, initial load).</summary>
        public void Apply(DownloadJob job)
        {
            FileName = job.ResolvedFileName ?? job.RequestedFileName ?? FallbackName(job.OriginalUrl);
            Url = job.OriginalUrl;
            Host = HostOf(job);
            Connections = job.ConnectionsPerFile;
            Added = job.CreatedUtc;
            TargetPath = job.ResolvedFinalPath ?? job.TargetDirectory;
            FailureReason = job.State == JobState.Failed ? (job.FailureReason ?? "Failed") : string.Empty;
            TotalBytes = job.TotalBytes;
            CompletedBytes = job.CompletedBytes;
            Progress = job.TotalBytes > 0
                ? Math.Clamp(job.CompletedBytes * 100.0 / job.TotalBytes, 0.0, 100.0) : 0.0;
            BytesText = $"{Formatting.FormatBytes(job.CompletedBytes)} / {(job.TotalBytes > 0 ? Formatting.FormatBytes(job.TotalBytes) : "?")}";
            var (text, glyph) = MapStatus(job);
            StatusText = text;
            StatusGlyph = glyph;
            State = job.State; // raises derived (IsActive/Can*) notifications
            if (!IsActive)
            {
                SpeedMBps = 0;
                SpeedText = IsTerminal && State == JobState.Completed ? Formatting.FormatRate(0) : "—";
                RefreshEta();
            }
            Raise(nameof(CanPause));
            Raise(nameof(CanResume));
            Raise(nameof(CanCancel));
            Raise(nameof(CanRetry));
        }

        /// <summary>Hot-path progress update: bytes/speed/ETA only (no rebuild).</summary>
        public void ApplyProgress(long completedBytes, long? totalBytes, DateTimeOffset timestamp)
        {
            if (totalBytes.HasValue && totalBytes.Value > 0) TotalBytes = totalBytes.Value;
            CompletedBytes = completedBytes;
            Progress = TotalBytes > 0
                ? Math.Clamp(completedBytes * 100.0 / TotalBytes, 0.0, 100.0) : 0.0;
            BytesText = $"{Formatting.FormatBytes(completedBytes)} / {(TotalBytes > 0 ? Formatting.FormatBytes(TotalBytes) : "?")}";

            if (_lastBytes >= 0 && completedBytes >= _lastBytes)
            {
                double dt = (timestamp - _lastAt).TotalSeconds;
                if (dt > 0.2)
                {
                    double inst = (completedBytes - _lastBytes) / 1024.0 / 1024.0 / dt;
                    SpeedMBps = SpeedMBps * 0.7 + Math.Max(0.0, inst) * 0.3;
                }
            }
            else if (_lastBytes < 0)
            {
                SpeedMBps = 0;
            }
            // A restart resets counters: drop stale EMA rather than showing a spike.
            if (completedBytes < _lastBytes) SpeedMBps = 0;
            _lastBytes = completedBytes;
            _lastAt = timestamp;
            SpeedText = Formatting.FormatRate(SpeedMBps * 1024.0 * 1024.0);
            RefreshEta();
        }

        /// <summary>1 Hz derived refresh (ETA decay, loud only for active rows — caller filters).</summary>
        public void RefreshDerived()
        {
            RefreshEta();
        }

        private void RefreshEta()
        {
            EtaText = IsActive
                ? Formatting.FormatEta(TotalBytes > 0 ? TotalBytes : null, CompletedBytes, SpeedMBps * 1024.0 * 1024.0)
                : (State == JobState.Completed ? "0s" : "—");
        }

        public static (string Text, string Glyph) MapStatus(DownloadJob job)
        {
            string reason = job.State == JobState.Failed && !string.IsNullOrEmpty(job.FailureReason)
                ? " — " + Formatting.FormatError(job.FailureReason) : string.Empty;
            return job.State switch
            {
                JobState.Queued => ("Queued", "○"),
                JobState.Downloading => ("Downloading", "▼"),
                JobState.Pausing => ("Pausing…", "◌"),
                JobState.Paused => ("Paused", "❚❚"),
                JobState.Retrying => ("Retrying…", "↻"),
                JobState.Completed => ("Completed", "✓"),
                JobState.Failed => ("Failed" + reason, "✕"),
                JobState.Cancelled => ("Cancelled", "⊘"),
                JobState.Interrupted => ("Interrupted", "⚠"),
                _ => (job.State.ToString(), "•"),
            };
        }

        private static string HostOf(DownloadJob job)
        {
            try
            {
                if (!string.IsNullOrEmpty(job.HostKey)) return job.HostKey;
                return new Uri(job.OriginalUrl).Host;
            }
            catch { return string.Empty; }
        }

        private static string FallbackName(string url)
        {
            try
            {
                var name = Uri.UnescapeDataString(System.IO.Path.GetFileName(new Uri(url).LocalPath));
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return url.Length > 60 ? url.Substring(0, 60) + "…" : url;
        }
    }
}
