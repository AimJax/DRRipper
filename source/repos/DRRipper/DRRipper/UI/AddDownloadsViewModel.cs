using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DRRipper.Scheduler;

namespace DRRipper.UI
{
    /// <summary>
    /// Add-downloads dialog model (Ticket #006 §16). Validates locally for live
    /// counts (same rules as the scheduler import); Submit re-validates
    /// authoritatively via <see cref="DownloadScheduler.ImportAsync"/>.
    /// Cap of surfaced errors keeps 10k-line imports cheap (§16: no per-line visuals).
    /// </summary>
    public sealed class AddDownloadsViewModel : ObservableObject
    {
        public const int MaxSurfacedErrors = 50;

        private readonly DownloadScheduler _scheduler;
        private string _urlsText = string.Empty;
        private string _targetDirectory = string.Empty;
        private int _connections;
        private int _priority;
        private int _accepted;
        private int _rejected;
        private string _resultSummary = string.Empty;
        private readonly List<string> _errors = new();

        public AddDownloadsViewModel(DownloadScheduler scheduler, string? targetDirectory = null, int connections = 8)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _targetDirectory = targetDirectory ?? string.Empty;
            _connections = Math.Clamp(connections, 1, 16);
        }

        public string UrlsText { get => _urlsText; set { if (Set(ref _urlsText, value ?? string.Empty)) ValidatePreview(); } }
        public string TargetDirectory { get => _targetDirectory; set => Set(ref _targetDirectory, value ?? string.Empty); }
        public int Connections { get => _connections; set => Set(ref _connections, Math.Clamp(value, 1, 16)); }
        public int Priority { get => _priority; set => Set(ref _priority, value); }
        public int Accepted { get => _accepted; private set => Set(ref _accepted, value); }
        public int Rejected { get => _rejected; private set => Set(ref _rejected, value); }
        public string ResultSummary { get => _resultSummary; private set => Set(ref _resultSummary, value); }
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>Set by the dialog's "Add and Start" button (ensures admission is flowing).</summary>
        public bool StartAfterAdd { get; set; }

        /// <summary>Live preview counts using the same normalization as the scheduler.</summary>
        public void ValidatePreview()
        {
            int accepted = 0, rejected = 0;
            _errors.Clear();
            foreach (var raw in SplitLines(_urlsText))
            {
                if (TryNormalizeUrl(raw, out _)) accepted++;
                else
                {
                    rejected++;
                    if (_errors.Count < MaxSurfacedErrors)
                        _errors.Add($"Rejected: {TrimLine(raw)}");
                }
            }
            Accepted = accepted;
            Rejected = rejected;
            Raise(nameof(Errors));
        }

        /// <summary>Submits the batch. Returns the authoritative import summary.</summary>
        public async Task<ImportResult> SubmitAsync()
        {
            var result = await _scheduler.ImportAsync(_urlsText, _targetDirectory,
                connectionsPerFile: Connections, priority: Priority).ConfigureAwait(false);
            Accepted = result.Accepted;
            Rejected = result.Rejected;
            _errors.Clear();
            foreach (var e in result.Errors.Take(MaxSurfacedErrors)) _errors.Add(e);
            ResultSummary = $"Added {result.Accepted}, rejected {result.Rejected}.";
            Raise(nameof(Errors));
            Raise(nameof(ResultSummary));
            return result;
        }

        internal static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (var ln in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var l = ln.Trim();
                if (l.Length == 0) continue;
                yield return l;
            }
        }

        /// <summary>Mirrors the scheduler's import normalization (scheme default + absolute http/https).</summary>
        internal static bool TryNormalizeUrl(string? raw, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var l = raw.Trim();
            if (!l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                l = "https://" + l;
            if (!Uri.TryCreate(l, UriKind.Absolute, out var u) || !u.IsAbsoluteUri ||
                (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                return false;
            normalized = u.ToString();
            return true;
        }

        private static string TrimLine(string line)
            => line.Length > 80 ? line.Substring(0, 77) + "…" : line;
    }
}
