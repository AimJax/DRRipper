using System;
using System.Threading;
using System.Threading.Tasks;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Runtime context for an actively downloading job (Ticket #005).
    /// Owns the job-scoped ParallelDownloader, job CTS, and progress wiring.
    /// Network permits are acquired per transfer attempt inside DownloadSession
    /// via the shared gate (§13) — this runtime holds no permits itself, so it
    /// cannot leak them. Pause/checkpoint/backoff/finalize never hold permits
    /// by construction (gate scope is SendAsync + body read only).
    /// </summary>
    internal sealed class ActiveJobRuntime : IDisposable
    {
        private DownloadJob _job;
        private readonly ConnectionBudget _budget;
        private readonly SchedulerSettings _settings;
        private readonly Action<Guid, long, long?> _onProgress;
        private readonly Func<DownloadJob, Task> _onTerminal;

        private ParallelDownloader? _downloader;
        private CancellationTokenSource? _jobCts;
        private Task<string>? _runTask;
        private bool _disposed;

        public Guid JobId => _job.JobId;
        public DownloadJob Job => _job;
        public bool IsRunning => _runTask != null && !_runTask.IsCompleted;

        public ActiveJobRuntime(
            DownloadJob job,
            ConnectionBudget budget,
            SchedulerSettings settings,
            Action<Guid, long, long?> onProgress,
            Func<DownloadJob, Task> onTerminal)
        {
            _job = job;
            _budget = budget;
            _settings = settings;
            _onProgress = onProgress;
            _onTerminal = onTerminal;
        }

        /// <summary>Starts the download. Returns immediately; completion flows via _onTerminal.</summary>
        public void Start(CancellationToken schedulerCt, string hostKey, BrowserRequestContext? requestContext = null, int? workerCapOverride = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ActiveJobRuntime));
            if (_runTask != null) throw new InvalidOperationException("Job already started.");

            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(schedulerCt);

            _downloader = new ParallelDownloader
            {
                RetryPolicy = DownloadRetryPolicy.Default,
                ReadTimeout = TimeSpan.FromSeconds(30),
                CheckpointInterval = TimeSpan.FromSeconds(2),
                NetworkGate = _budget,
                NetworkHostKey = hostKey,
                RequestContext = requestContext,
                UseSharedHandler = true,
                AdaptiveConcurrency = _settings.TransferMode == TransferMode.MaximumThroughput,
            };
            _downloader.ProgressChanged += OnProgressChanged;

            var job = _job;
            var dl = _downloader;
            var cts = _jobCts;
            int connections = Math.Clamp(
                workerCapOverride.HasValue
                    ? Math.Min(job.ConnectionsPerFile, workerCapOverride.Value)
                    : job.ConnectionsPerFile, 1, 64);
            _runTask = Task.Run(async () =>
            {
                try
                {
                    var finalPath = await dl.StartAsync(
                        job.OriginalUrl, job.TargetDirectory, connections, cts.Token);
                    return finalPath;
                }
                finally
                {
                    try { dl.ProgressChanged -= OnProgressChanged; } catch { }
                }
            }, CancellationToken.None);
        }

        public Task<string> Completion => _runTask ?? throw new InvalidOperationException("Job not started.");

        public DownloadState CurrentState => _downloader?.GetState() ?? DownloadState.Idle;

        /// <summary>Adaptive snapshot for telemetry/tests (§21). Never throws.</summary>
        internal (bool Active, int Target, int Cap, double PeakMBps, string Decision, long Evals) GetAdaptiveSnapshot()
        {
            try { return _downloader?.GetAdaptiveSnapshot() ?? (false, 0, 0, 0, "no-downloader", 0); }
            catch { return (false, 0, 0, 0, "error", 0); }
        }

        /// <summary>Run-state diagnostics for stuck-transfer triage. Never throws.</summary>
        internal string GetRunDiagnostics()
        {
            try
            {
                var dl = _downloader;
                if (dl == null) return "no-downloader";
                var session = dl.ActiveSession;
                if (session == null) return "no-session";
                return $"attempts={session.ActiveAttempts} requests={session.ActiveRequests.Count} " +
                    $"queue={session.WorkQueue.Count} phase={session.Phase} " +
                    $"faults={Interlocked.Read(ref session.AdaptiveFaults)}";
            }
            catch (Exception ex) { return "diag-error:" + ex.GetType().Name; }
        }

        public void Pause() => _downloader?.Pause();

        public void Resume() => _downloader?.Resume();

        public void Cancel()
        {
            try { _downloader?.Cancel(); } catch { }
            try { _jobCts?.Cancel(); } catch { }
        }

        /// <summary>Teardown preserving files (app shutdown): no partial deletion.</summary>
        public void Teardown()
        {
            try { _jobCts?.Cancel(); } catch { }
            try
            {
                // ParallelDownloader.Dispose calls session.Teardown() (keep files).
                _downloader?.Dispose();
            }
            catch { }
        }

        public void UpdateJob(DownloadJob job) => _job = job;

        private void OnProgressChanged(DownloadProgress progress)
        {
            try { _onProgress(JobId, progress.TotalBytesDownloaded, progress.TotalBytes > 0 ? progress.TotalBytes : null); }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_downloader != null)
                {
                    try { _downloader.ProgressChanged -= OnProgressChanged; } catch { }
                    try { _downloader.Dispose(); } catch { }
                    _downloader = null;
                }
            }
            finally
            {
                try { _jobCts?.Dispose(); } catch { }
                _jobCts = null;
            }
        }
    }
}
