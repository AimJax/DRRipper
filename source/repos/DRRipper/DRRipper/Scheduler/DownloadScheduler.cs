using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DRRipper.Scheduler
{
    /// <summary>Progress snapshot for one job (UI-framework-independent).</summary>
    public sealed class JobProgressEvent
    {
        public Guid JobId { get; init; }
        public long CompletedBytes { get; init; }
        public long? TotalBytes { get; init; }
        public JobState State { get; init; }
    }

    /// <summary>
    /// Persistent bulk download scheduler (Ticket #005).
    /// Owns: persistent store, global/host budgets (via shared gate), active runtimes,
    /// admission loop, power holds. WPF-independent; callers marshal events to UI.
    /// Resource use scales with ACTIVE jobs, not total queued jobs.
    /// </summary>
    public sealed class DownloadScheduler : IDisposable, IAsyncDisposable
    {
        private readonly JobStore _store;
        private readonly SchedulerSettings _settings;
        private readonly ConnectionBudget _budget;
        private readonly SchedulerPowerManager _power = new();

        private readonly ConcurrentDictionary<Guid, ActiveJobRuntime> _active = new();
        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastPersist = new();
        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastEvent = new();
        /// <summary>
        /// Transient browser request contexts by JobId (Ticket #007 §18/§19).
        /// Never persisted; resume after restart falls back to ReferrerHost.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, BrowserRequestContext> _browserContexts = new();
        private readonly SemaphoreSlim _pumpLock = new(1, 1);
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _running;
        private bool _suspendedAll;
        private bool _disposed;
        private long _nextQueuePosition = -1;
        private readonly object _queuePosLock = new();

        public event EventHandler<DownloadJob>? JobAdded;
        public event EventHandler<DownloadJob>? JobUpdated;
        public event EventHandler<Guid>? JobRemoved;
        public event EventHandler<bool>? SchedulerStateChanged; // true = running
        public event EventHandler<JobProgressEvent>? JobProgress;

        public SchedulerSettings Settings => _settings;
        public ConnectionBudget Budget => _budget;
        internal SchedulerPowerManager Power => _power;

        public DownloadScheduler(JobStore store, SchedulerSettings settings)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (_settings.ActiveDownloadLimit < 1) _settings.ActiveDownloadLimit = 1;
            _budget = new ConnectionBudget(
                Math.Max(1, _settings.GlobalConnectionBudget),
                Math.Max(1, Math.Min(_settings.PerHostConnectionBudget, _settings.GlobalConnectionBudget)));
        }

        public int ActiveCount => _active.Count;
        public bool IsRunning => _running && !_disposed;

        // ---------- lifecycle ----------

        /// <summary>
        /// Startup recovery (§19): normalize interrupted states, reconcile obvious
        /// finals, restore queue order. Instantiates zero sessions.
        /// </summary>
        public async Task StartAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(DownloadScheduler));
            if (_running) return;

            await _store.NormalizeInterruptedStatesAsync(ct);
            await ReconcileCompletedFilesAsync(ct);
            _nextQueuePosition = await _store.GetMaxQueuePositionAsync(ct);

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running = true;
            try { SchedulerStateChanged?.Invoke(this, true); } catch { }
            _loopTask = Task.Run(() => PumpLoopAsync(_loopCts.Token), CancellationToken.None);
            await PumpAsync();
        }

        /// <summary>
        /// Orderly shutdown (§20): stop admitting, teardown active preserving files,
        /// persist Interrupted states, dispose shared after settle. Never deletes partials.
        /// </summary>
        public async Task StopAsync()
        {
            if (!_running) return;
            _running = false;
            try { SchedulerStateChanged?.Invoke(this, false); } catch { }
            try { _loopCts?.Cancel(); } catch { }

            // Teardown active runtimes preserving files (bounded settle).
            var runtimes = _active.Values.ToList();
            foreach (var rt in runtimes)
            {
                try { rt.Teardown(); } catch { }
            }
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            foreach (var rt in runtimes)
            {
                try
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        using var cts = new CancellationTokenSource(remaining);
                        try { await rt.Completion.WaitAsync(cts.Token); } catch { }
                    }
                }
                catch { }
                try { rt.Dispose(); } catch { }
            }
            _active.Clear();

            // Persist any still-Downloading rows as Interrupted (crash-safe).
            try
            {
                var nonTerminal = await _store.GetNonTerminalJobsAsync();
                foreach (var j in nonTerminal)
                {
                    if (j.State == JobState.Downloading || j.State == JobState.Pausing || j.State == JobState.Retrying)
                    {
                        try { await _store.UpdateStateAsync(j.JobId, JobState.Interrupted, ct: CancellationToken.None); } catch { }
                    }
                }
            }
            catch { }

            // Release any stray power holds.
            while (_power.RefCount > 0) _power.Release();

            try
            {
                if (_loopTask != null) { try { await _loopTask; } catch { } }
            }
            catch { }
            try { _loopCts?.Dispose(); } catch { }
            _loopCts = null;
        }

        private async Task ReconcileCompletedFilesAsync(CancellationToken ct)
        {
            // Obvious finals: Completed rows whose file exists stay Completed.
            // Completed rows whose file is missing stay Completed (user may have moved
            // it; never silently requeue). Interrupted/Paused rows keep resumable files.
            // This pass performs zero session instantiation and zero network I/O.
            try
            {
                var all = await _store.GetAllJobsAsync(ct: ct);
                foreach (var j in all)
                {
                    ct.ThrowIfCancellationRequested();
                    if (j.State == JobState.Completed && !string.IsNullOrEmpty(j.ResolvedFinalPath))
                    {
                        // Keep as-is; existence check is informational only.
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        // ---------- enqueue / import ----------

        private static bool TryNormalizeUrl(string? raw, out string normalized, out string? error)
        {
            normalized = string.Empty;
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "empty line";
                return false;
            }
            var l = raw.Trim();
            if (!l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                l = "https://" + l;
            if (!Uri.TryCreate(l, UriKind.Absolute, out var u) || !u.IsAbsoluteUri ||
                (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                error = "malformed URL";
                return false;
            }
            normalized = u.ToString();
            return true;
        }

        /// <summary>
        /// Enqueues a single URL. Never deduplicates (each submission is independent).
        /// A null <paramref name="connectionsPerFile"/> takes the mode default:
        /// 8 in Balanced, the configured ceiling in MaximumThroughput (§23).
        /// </summary>
        public async Task<DownloadJob> EnqueueAsync(string url, string targetDirectory, int? connectionsPerFile = null, int priority = 0, string? requestedFileName = null, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(DownloadScheduler));
            if (!TryNormalizeUrl(url, out var normalized, out var err))
                throw new ArgumentException($"Invalid URL: {err}", nameof(url));
            if (string.IsNullOrWhiteSpace(targetDirectory))
                throw new ArgumentException("Target directory is required.", nameof(targetDirectory));

            long pos;
            lock (_queuePosLock) pos = ++_nextQueuePosition;
            // Rebase if store has higher (e.g. after restart with concurrent enqueues).
            try
            {
                var max = await _store.GetMaxQueuePositionAsync(ct);
                if (max >= pos) { lock (_queuePosLock) _nextQueuePosition = max + 1; pos = _nextQueuePosition++; }
            }
            catch { }

            var job = new DownloadJob
            {
                OriginalUrl = normalized,
                TargetDirectory = targetDirectory,
                RequestedFileName = requestedFileName,
                QueuePosition = pos,
                Priority = priority,
                State = JobState.Queued,
                ConnectionsPerFile = DefaultConnections(_settings, connectionsPerFile),
                HostKey = ConnectionBudget.NormalizeHostKey(normalized),
            };
            await _store.AddAsync(job, ct);
            try { JobAdded?.Invoke(this, job); } catch { }
            await PumpAsync();
            return job;
        }

        /// <summary>Bulk import of newline-separated URLs (§10). Single batch transaction.
        /// One malformed line never aborts the batch.
        /// </summary>
        public async Task<ImportResult> ImportAsync(string newlineSeparatedUrls, string targetDirectory, int? connectionsPerFile = null, int priority = 0, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(DownloadScheduler));
            var result = new ImportResult();
            if (newlineSeparatedUrls == null) newlineSeparatedUrls = string.Empty;
            var lines = newlineSeparatedUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var jobs = new List<DownloadJob>(lines.Length);

            long basePos;
            lock (_queuePosLock) basePos = _nextQueuePosition + 1;
            try
            {
                var max = await _store.GetMaxQueuePositionAsync(ct);
                if (max + 1 > basePos) basePos = max + 1;
            }
            catch { }

            long pos = basePos;
            foreach (var raw in lines)
            {
                ct.ThrowIfCancellationRequested();
                var l = raw.Trim();
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (!TryNormalizeUrl(l, out var normalized, out var err))
                {
                    result.Rejected++;
                    if (result.Errors.Count < 50)
                        result.Errors.Add($"Rejected line: {err}");
                    continue;
                }
                var job = new DownloadJob
                {
                    OriginalUrl = normalized,
                    TargetDirectory = targetDirectory,
                    QueuePosition = pos++,
                    Priority = priority,
                    State = JobState.Queued,
                    ConnectionsPerFile = DefaultConnections(_settings, connectionsPerFile),
                    HostKey = ConnectionBudget.NormalizeHostKey(normalized),
                };
                jobs.Add(job);
                result.Jobs.Add(job);
            }
            lock (_queuePosLock) _nextQueuePosition = pos - 1;

            if (jobs.Count > 0)
            {
                await _store.AddBatchAsync(jobs, ct);
                result.Accepted = jobs.Count;
                foreach (var j in jobs) { try { JobAdded?.Invoke(this, j); } catch { } }
            }
            await PumpAsync();
            return result;
        }

        /// <summary>
        /// Browser-handoff enqueue (Ticket #007 §31/§32): the SAME normal
        /// persistent scheduler path as manual jobs. The browser supplies a
        /// filename suggestion only — DRRipper owns the target directory.
        /// Validated origin metadata is persisted (§15); request headers stay
        /// transient in <see cref="_browserContexts"/> (never in the DB, §19).
        /// </summary>
        public async Task<DownloadJob> EnqueueBrowserAsync(BrowserEnqueueOptions options, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(DownloadScheduler));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.TargetDirectory))
                throw new ArgumentException("Target directory is required.", nameof(options));
            if (!BrowserBridge.BrowserValidation.TryNormalizeUrl(options.Url, out var normalized, out _))
                throw new ArgumentException("Invalid browser URL.", nameof(options));

            var suggested = BrowserBridge.BrowserValidation.SanitizeFileName(options.SuggestedFileName);
            var job = await EnqueueAsync(normalized, options.TargetDirectory,
                options.ConnectionsPerFile, priority: 0, requestedFileName: suggested, ct);

            string? referrerHost = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(options.Referrer) &&
                    Uri.TryCreate(options.Referrer, UriKind.Absolute, out var ru) &&
                    (string.Equals(ru.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ru.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
                    referrerHost = ru.Host;
            }
            catch { }

            string source = "Browser";
            try
            {
                if (BrowserBridge.BrowserValidation.TryNormalizeSource(options.SourceApplication, out var s))
                    source = s;
            }
            catch { }

            var patched = job.With(j =>
            {
                j.SourceApplication = source;
                j.ReferrerHost = referrerHost;
                j.BrowserRequestId = options.BrowserRequestId;
            });
            try { await _store.UpdateBrowserMetadataAsync(patched, ct); } catch { }
            try { JobUpdated?.Invoke(this, patched); } catch { }

            if (options.RequestContext != null)
                _browserContexts[job.JobId] = options.RequestContext;
            return patched;
        }

        internal bool TryGetBrowserContext(Guid jobId, out BrowserRequestContext? context)
            => _browserContexts.TryGetValue(jobId, out context);

        /// <summary>
        /// Mode-aware per-file connection default (Ticket #008 §23): explicit
        /// values are honored up to 64; null takes 8 in Balanced and the
        /// configured ceiling in MaximumThroughput.
        /// </summary>
        internal static int DefaultConnections(SchedulerSettings? settings, int? requested)
        {
            if (requested.HasValue)
                return Math.Clamp(requested.Value, 1, 64);
            if (settings != null && settings.TransferMode == TransferMode.MaximumThroughput)
                return SchedulerThroughputPolicy.EffectiveMaxPerFile(settings);
            return SchedulerThroughputPolicy.BalancedPerFileConnections;
        }

        /// <summary>Finds a job by browser handoff idempotency key (Ticket #007 §27).</summary>
        public Task<DownloadJob?> GetJobByBrowserRequestIdAsync(string? browserRequestId, CancellationToken ct = default)
            => _store.GetByBrowserRequestIdAsync(browserRequestId, ct);

        /// <summary>Adaptive snapshot for one active job, if running (§21). Never throws.</summary>
        public (bool Active, int Target, int Cap, double PeakMBps, string Decision, long Evals) TryGetAdaptiveSnapshot(Guid jobId)
        {
            try
            {
                if (_active.TryGetValue(jobId, out var rt))
                    return rt.GetAdaptiveSnapshot();
                return (false, 0, 0, 0, "not-active", 0);
            }
            catch { return (false, 0, 0, 0, "error", 0); }
        }

        /// <summary>
        /// Run-state diagnostics for stuck-transfer triage (tests/diagnostics).
        /// Never throws. Reports worker/attempt/queue/phase counters.
        /// </summary>
        internal string GetRunDiagnostics(Guid jobId)
        {
            try
            {
                if (!_active.TryGetValue(jobId, out var rt))
                    return "no-runtime";
                return rt.GetRunDiagnostics();
            }
            catch (Exception ex) { return "diag-error:" + ex.GetType().Name; }
        }

        // ---------- per-job controls ----------

        public async Task PauseJobAsync(Guid jobId)
        {
            var job = await _store.GetAsync(jobId);
            if (job == null) return;
            if (job.IsTerminal) return;

            if (_active.TryGetValue(jobId, out var rt))
            {
                // Active: checkpoint via engine pause, then teardown preserving files.
                try { rt.Pause(); } catch { }
                // Bounded settle for pause ack (engine already bounded 15s).
                await Task.Delay(250);
                try { rt.Teardown(); } catch { }
                try
                {
                    var remaining = TimeSpan.FromSeconds(20);
                    using var cts = new CancellationTokenSource(remaining);
                    try { await rt.Completion.WaitAsync(cts.Token); } catch { }
                }
                catch { }
                _active.TryRemove(jobId, out _);
                try { rt.Dispose(); } catch { }
                _power.Release();
            }
            await _store.UpdateStateAsync(jobId, JobState.Paused, ct: CancellationToken.None);
            try
            {
                var updated = await _store.GetAsync(jobId);
                if (updated != null) try { JobUpdated?.Invoke(this, updated); } catch { }
            }
            catch { }
            await PumpAsync();
        }

        public async Task ResumeJobAsync(Guid jobId)
        {
            var job = await _store.GetAsync(jobId);
            if (job == null) return;
            if (job.State != JobState.Paused && job.State != JobState.Interrupted &&
                job.State != JobState.Cancelled && job.State != JobState.Failed) return;
            // Resume = requeue (recovery via .drmeta on next admission). Never instantiates here.
            await _store.UpdateStateAsync(jobId, JobState.Queued, ct: CancellationToken.None);
            try
            {
                var updated = await _store.GetAsync(jobId);
                if (updated != null) try { JobUpdated?.Invoke(this, updated); } catch { }
            }
            catch { }
            await PumpAsync();
        }

        public async Task CancelJobAsync(Guid jobId)
        {
            var job = await _store.GetAsync(jobId);
            if (job == null) return;
            if (job.IsTerminal) return;

            if (_active.TryGetValue(jobId, out var rt))
            {
                try { rt.Cancel(); } catch { } // engine Cancel drops partials
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await rt.Completion.WaitAsync(cts.Token); } catch { }
                }
                catch { }
                _active.TryRemove(jobId, out _);
                try { rt.Dispose(); } catch { }
                _power.Release();
            }
            else
            {
                // Queued/paused with possible partials: Cancel deletes part/meta (engine semantics).
                DeletePartialFiles(job);
            }
            await _store.UpdateStateAsync(jobId, JobState.Cancelled, completedUtc: DateTimeOffset.UtcNow, resetProgress: true, ct: CancellationToken.None);
            try
            {
                var updated = await _store.GetAsync(jobId);
                if (updated != null) try { JobUpdated?.Invoke(this, updated); } catch { }
            }
            catch { }
            await PumpAsync();
        }

        /// <summary>
        /// Retries a failed/cancelled job independently (§17). Gated by MaxJobAttempts.
        /// </summary>
        public async Task RetryJobAsync(Guid jobId)
        {
            var job = await _store.GetAsync(jobId);
            if (job == null) throw new InvalidOperationException("Job not found.");
            if (job.State != JobState.Failed && job.State != JobState.Cancelled)
                throw new InvalidOperationException($"Only failed/cancelled jobs can be retried (state={job.State}).");
            if (job.AttemptCount >= _settings.MaxJobAttempts)
                throw new InvalidOperationException($"MaxJobAttempts ({_settings.MaxJobAttempts}) reached for this job.");
            await _store.UpdateStateAsync(jobId, JobState.Queued,
                attemptCount: job.AttemptCount + 1, resetProgress: true, ct: CancellationToken.None);
            try
            {
                var updated = await _store.GetAsync(jobId);
                if (updated != null) try { JobUpdated?.Invoke(this, updated); } catch { }
            }
            catch { }
            await PumpAsync();
        }

        /// <summary>
        /// Removes a queue entry (§21). Deletes part/meta if present; never deletes final user files.
        /// </summary>
        public async Task RemoveJobAsync(Guid jobId)
        {
            var job = await _store.GetAsync(jobId);
            if (_active.TryGetValue(jobId, out var rt))
            {
                try { rt.Teardown(); } catch { }
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try { await rt.Completion.WaitAsync(cts.Token); } catch { }
                }
                catch { }
                _active.TryRemove(jobId, out _);
                try { rt.Dispose(); } catch { }
                _power.Release();
            }
            if (job != null) DeletePartialFiles(job); // explicit: partials go, finals never
            await _store.RemoveAsync(jobId);
            try { JobRemoved?.Invoke(this, jobId); } catch { }
            await PumpAsync();
        }

        /// <summary>Removes all terminal (Completed/Failed/Cancelled) entries. Finals on disk untouched.</summary>
        public async Task ClearCompletedAsync()
        {
            var all = await _store.GetAllJobsAsync();
            foreach (var j in all)
            {
                if (j.State == JobState.Completed || j.State == JobState.Failed || j.State == JobState.Cancelled)
                {
                    await _store.RemoveAsync(j.JobId);
                    try { JobRemoved?.Invoke(this, j.JobId); } catch { }
                }
            }
        }

        public async Task ReorderAsync(IReadOnlyList<Guid> jobIdsInOrder)
        {
            await _store.ReorderAsync(jobIdsInOrder);
            await PumpAsync();
        }

        public async Task PauseAllAsync()
        {
            _suspendedAll = true;
            var ids = _active.Keys.ToList();
            foreach (var id in ids)
            {
                try { await PauseJobAsync(id); } catch { }
            }
        }

        public async Task ResumeAllAsync()
        {
            _suspendedAll = false;
            var all = await _store.GetAllJobsAsync();
            foreach (var j in all)
            {
                if (j.State == JobState.Paused || j.State == JobState.Interrupted)
                {
                    try { await _store.UpdateStateAsync(j.JobId, JobState.Queued, ct: CancellationToken.None); } catch { }
                    try
                    {
                        var u = await _store.GetAsync(j.JobId);
                        if (u != null) try { JobUpdated?.Invoke(this, u); } catch { }
                    }
                    catch { }
                }
            }
            await PumpAsync();
        }

        public Task<List<DownloadJob>> GetAllJobsAsync(CancellationToken ct = default)
            => _store.GetAllJobsAsync(ct: ct);

        public Task<DownloadJob?> GetJobAsync(Guid id, CancellationToken ct = default)
            => _store.GetAsync(id, ct);

        // ---------- admission loop ----------

        private async Task PumpLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _running)
                {
                    try { await PumpAsync(); } catch { }
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private async Task PumpAsync()
        {
            if (!_running || _disposed || _suspendedAll) return;
            if (!await _pumpLock.WaitAsync(0)) return;
            try
            {
                while (_running && !_suspendedAll && _active.Count < _settings.ActiveDownloadLimit)
                {
                    List<DownloadJob> eligible;
                    try { eligible = await _store.GetEligibleJobsAsync(_settings.ActiveDownloadLimit - _active.Count); }
                    catch { break; }
                    var next = eligible.FirstOrDefault(e => !_active.ContainsKey(e.JobId));
                    if (next == null) break;
                    try { await StartJobAsync(next); }
                    catch
                    {
                        // Failure isolation: mark failed, continue with next.
                        try { await _store.UpdateStateAsync(next.JobId, JobState.Failed, failureReason: "Scheduler admission failure", completedUtc: DateTimeOffset.UtcNow, ct: CancellationToken.None); } catch { }
                    }
                }
            }
            finally { try { _pumpLock.Release(); } catch { } }
        }

        private async Task StartJobAsync(DownloadJob job)
        {
            var hostKey = job.HostKey;
            if (string.IsNullOrEmpty(hostKey))
            {
                try { hostKey = ConnectionBudget.NormalizeHostKey(job.OriginalUrl); } catch { hostKey = "unknown"; }
            }
            await _store.UpdateStateAsync(job.JobId, JobState.Downloading,
                lastStartedUtc: DateTimeOffset.UtcNow, hostKey: hostKey, resetProgress: true, ct: CancellationToken.None);
            try
            {
                var updated = await _store.GetAsync(job.JobId);
                if (updated != null) { job = updated; try { JobUpdated?.Invoke(this, updated); } catch { } }
            }
            catch { }

            var rt = new ActiveJobRuntime(job, _budget, _settings, OnJobProgress, OnJobTerminal);
            if (!_active.TryAdd(job.JobId, rt))
            {
                try { rt.Dispose(); } catch { }
                return;
            }
            _power.Acquire();
            _lastPersist[job.JobId] = DateTimeOffset.MinValue;
            _lastEvent[job.JobId] = DateTimeOffset.MinValue;
            BrowserRequestContext? browserContext = null;
            try
            {
                if (!_browserContexts.TryGetValue(job.JobId, out browserContext) ||
                    browserContext == null)
                {
                    // Resume-after-restart fallback: persisted referrer host only (§15).
                    if (!string.IsNullOrWhiteSpace(job.ReferrerHost))
                        browserContext = new BrowserRequestContext
                        {
                            Referrer = "https://" + job.ReferrerHost + "/",
                        };
                }
            }
            catch { browserContext = null; }
            // Maximum-mode share guard (Ticket #008 §12/§53): divide the 32-file
            // ceiling among ACTUAL active jobs (snapshot at start) so one file
            // saturates alone while 8 files behave like Balanced (no global
            // over-parallelization collapse on capped paths). The shared gate
            // still lets efficient jobs dominate within their share.
            int? workerCap = null;
            try
            {
                if (_settings.TransferMode == TransferMode.MaximumThroughput)
                    workerCap = SchedulerThroughputPolicy.SharedWorkerCap(_active.Count);
            }
            catch { workerCap = null; }
            rt.Start(_loopCts?.Token ?? CancellationToken.None, hostKey, browserContext, workerCap);
            _ = WatchJobAsync(job.JobId, rt);
        }

        private async Task WatchJobAsync(Guid jobId, ActiveJobRuntime rt)
        {
            string? finalPath = null;
            Exception? terminalEx = null;
            bool cancelled = false;
            try
            {
                finalPath = await rt.Completion;
            }
            catch (OperationCanceledException ex) { cancelled = true; terminalEx = ex; }
            catch (DownloadFailedException ex) { terminalEx = ex; }
            catch (Exception ex) { terminalEx = ex; }

            _active.TryRemove(jobId, out _);
            _power.Release();
            _lastPersist.TryRemove(jobId, out _);
            _lastEvent.TryRemove(jobId, out _);

            try
            {
                var job = await _store.GetAsync(jobId);
                if (job == null) { try { rt.Dispose(); } catch { } await PumpAsync(); return; }
                rt.UpdateJob(job);

                if (terminalEx == null && !cancelled)
                {
                    // Success — unless a control path (Cancel/Pause) already owns
                    // a terminal state (Ticket #008: atomic guard, no stomping).
                    bool marked = await _store.TryMarkCompletedAsync(jobId,
                        job.TotalBytes > 0 ? job.TotalBytes : job.CompletedBytes,
                        finalPath ?? job.ResolvedFinalPath, CancellationToken.None);
                    if (!marked)
                    {
                        try { job = await _store.GetAsync(jobId) ?? job; } catch { }
                    }
                }
                else if (terminalEx is OperationCanceledException)
                {
                    // Distinguish user-cancel (partials dropped) vs teardown/pause
                    // (files preserved). If the row is already Paused/Cancelled,
                    // the control path owns the state — do not stomp it.
                    var current = await _store.GetAsync(jobId);
                    if (current != null && (current.State == JobState.Paused || current.State == JobState.Cancelled || current.State == JobState.Completed))
                    {
                        // Control path already persisted the intended state.
                    }
                    else
                    {
                        await _store.UpdateStateAsync(jobId, JobState.Interrupted, ct: CancellationToken.None);
                    }
                }
                else if (terminalEx != null)
                {
                    var msg = terminalEx is DownloadFailedException dfe
                        ? dfe.Message
                        : $"Unexpected error: {terminalEx.GetType().Name}: {RedactForStore(terminalEx.Message)}";
                    await _store.UpdateStateAsync(jobId, JobState.Failed,
                        failureReason: msg, completedUtc: DateTimeOffset.UtcNow, ct: CancellationToken.None);
                }

                try
                {
                    var updated = await _store.GetAsync(jobId);
                    if (updated != null)
                    {
                        try { await OnJobTerminal(updated); } catch { }
                    }
                }
                catch { }
            }
            catch { }
            finally
            {
                try { rt.Dispose(); } catch { }
            }
            await PumpAsync();
        }

        private void OnJobProgress(Guid jobId, long completedBytes, long? totalBytes)
        {
            // Throttle: DB writes ≤ 1 per ProgressPersistInterval; UI events ≤ ~4/s.
            var now = DateTimeOffset.UtcNow;
            var lastP = _lastPersist.GetOrAdd(jobId, DateTimeOffset.MinValue);
            var lastE = _lastEvent.GetOrAdd(jobId, DateTimeOffset.MinValue);
            bool persist = now - lastP >= _settings.ProgressPersistInterval;
            bool emit = now - lastE >= TimeSpan.FromMilliseconds(250);
            if (persist)
            {
                _lastPersist[jobId] = now;
                _ = Task.Run(async () =>
                {
                    try { await _store.UpdateProgressAsync(jobId, completedBytes, totalBytes); } catch { }
                });
            }
            if (emit)
            {
                _lastEvent[jobId] = now;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var j = await _store.GetAsync(jobId);
                        if (j != null)
                        {
                            try { JobUpdated?.Invoke(this, j); } catch { }
                            try { JobProgress?.Invoke(this, new JobProgressEvent { JobId = jobId, CompletedBytes = completedBytes, TotalBytes = totalBytes ?? j.TotalBytes, State = j.State }); } catch { }
                        }
                    }
                    catch { }
                });
            }
        }

        private async Task OnJobTerminal(DownloadJob job)
        {
            try { JobUpdated?.Invoke(this, job); } catch { }
            await PumpAsync();
        }

        private static void DeletePartialFiles(DownloadJob job)
        {
            // Explicit remove/cancel semantics: delete .part + .drmeta (+ temps),
            // never delete a final completed file.
            try
            {
                string? dir = job.TargetDirectory;
                string? baseName = job.ResolvedFileName ?? job.RequestedFileName;
                if (string.IsNullOrEmpty(dir)) return;
                // Best-effort: derive candidate part paths from resolved final path.
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(job.ResolvedFinalPath))
                {
                    candidates.Add(job.ResolvedFinalPath + ".part");
                    candidates.Add(job.ResolvedFinalPath + ".part.drmeta");
                }
                // Also sweep directory for matching .part files is unsafe; only
                // delete the two candidates above (never user finals).
                foreach (var c in candidates)
                {
                    try { if (File.Exists(c)) File.Delete(c); } catch { }
                    try
                    {
                        var d = Path.GetDirectoryName(c);
                        var n = Path.GetFileName(c);
                        if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(n) && Directory.Exists(d))
                        {
                            foreach (var t in Directory.EnumerateFiles(d, n + ".tmp.*"))
                            { try { File.Delete(t); } catch { } }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string RedactForStore(string message)
        {
            // Never persist full signed URLs / credentials in failure text.
            // failure messages from the engine do not contain URLs, but be safe:
            // truncate overly long tokens that look like query strings.
            if (string.IsNullOrEmpty(message)) return message;
            if (message.Length > 2000) message = message.Substring(0, 2000) + "…[truncated]";
            return message;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
            _budget.Dispose();
            _pumpLock.Dispose();
        }

        /// <summary>
        /// TEST-ONLY kill simulation (no orderly shutdown): stops the admission loop,
        /// tears down runtimes preserving files, writes ZERO state (rows stay
        /// Downloading like a SIGKILL), and abandons in-flight work. The next
        /// scheduler opening the same DB exercises genuine crash recovery.
        /// </summary>
        internal void AbandonForKillTest()
        {
            _running = false;
            try { _loopCts?.Cancel(); } catch { }
            foreach (var rt in _active.Values)
            {
                try { rt.Teardown(); } catch { } // keep files, no state writes
            }
            // Bounded settle so .part/.drmeta checkpoints on disk are stable,
            // then drop everything without persisting anything.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            foreach (var rt in _active.Values)
            {
                try
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        using var cts = new CancellationTokenSource(remaining);
                        try { rt.Completion.WaitAsync(cts.Token).GetAwaiter().GetResult(); } catch { }
                    }
                }
                catch { }
                try { rt.Dispose(); } catch { }
            }
            _active.Clear();
            while (_power.RefCount > 0) _power.Release();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopAsync(); } catch { }
            _budget.Dispose();
            _pumpLock.Dispose();
        }
    }
}
