using System;
using System.Text.Json.Serialization;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Persistent job state. Only these states are stored in the database.
    /// Internal transient states (e.g. Starting) are not persisted.
    /// </summary>
    public enum JobState
    {
        /// <summary>Job is waiting to be started.</summary>
        Queued = 0,

        /// <summary>Job is actively downloading.</summary>
        Downloading = 1,

        /// <summary>Job is being paused (worker settlement in progress).</summary>
        Pausing = 2,

        /// <summary>Job is paused and resumable.</summary>
        Paused = 3,

        /// <summary>Job is in retry backoff waiting to retry.</summary>
        Retrying = 4,

        /// <summary>Job completed successfully.</summary>
        Completed = 5,

        /// <summary>Job failed permanently (or exceeded max attempts).</summary>
        Failed = 6,

        /// <summary>Job was explicitly cancelled by user.</summary>
        Cancelled = 7,

        /// <summary>Job was interrupted by crash/shutdown and needs recovery.</summary>
        Interrupted = 8,
    }

    /// <summary>
    /// A download job persisted in the queue database.
    /// All fields are serializable and persistable.
    /// Runtime objects (HttpClient, CancellationTokenSource, DownloadSession, etc.) are never stored here.
    /// </summary>
    public sealed class DownloadJob
    {
        /// <summary>Stable unique identifier for this job.</summary>
        public Guid JobId { get; init; } = Guid.NewGuid();

        /// <summary>The original URL as submitted by the user.</summary>
        public string OriginalUrl { get; set; } = string.Empty;

        /// <summary>Target directory for the downloaded file.</summary>
        public string TargetDirectory { get; set; } = string.Empty;

        /// <summary>User-suggested filename (if any).</summary>
        public string? RequestedFileName { get; set; }

        /// <summary>Filename resolved from server headers (after probe).</summary>
        public string? ResolvedFileName { get; set; }

        /// <summary>Final resolved full path (after collision resolution).</summary>
        public string? ResolvedFinalPath { get; set; }

        /// <summary>UTC time when the job was enqueued.</summary>
        public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>UTC time of last state change or progress update.</summary>
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Queue position for FIFO ordering within priority.</summary>
        public long QueuePosition { get; set; }

        /// <summary>Priority (lower = higher priority). Default 0.</summary>
        public int Priority { get; set; } = 0;

        /// <summary>Current persisted state.</summary>
        public JobState State { get; set; } = JobState.Queued;

        /// <summary>Failure reason if State == Failed.</summary>
        public string? FailureReason { get; set; }

        /// <summary>Number of scheduler-level attempts made for this job.</summary>
        public int AttemptCount { get; set; } = 0;

        /// <summary>Configured connections per file for this job.</summary>
        public int ConnectionsPerFile { get; set; } = 8;

        /// <summary>Total file size in bytes (if known from probe).</summary>
        public long TotalBytes { get; set; } = -1;

        /// <summary>Completed bytes (informational progress summary from last checkpoint).</summary>
        public long CompletedBytes { get; set; } = 0;

        /// <summary>Normalized host key for per-host budgeting (scheme+host+port).</summary>
        public string? HostKey { get; set; }

        /// <summary>UTC time when this job was last started (for debugging/analytics).</summary>
        public DateTimeOffset? LastStartedUtc { get; set; }

        /// <summary>UTC time when this job reached a terminal state (Completed/Failed/Cancelled).</summary>
        public DateTimeOffset? CompletedUtc { get; set; }

        /// <summary>
        /// Returns true if the job is in a terminal state.
        /// </summary>
        [JsonIgnore]
        public bool IsTerminal =>
            State == JobState.Completed ||
            State == JobState.Failed ||
            State == JobState.Cancelled;

        /// <summary>
        /// Returns true if the job was active (downloading/pausing/retrying) when interrupted.
        /// </summary>
        [JsonIgnore]
        public bool WasActive =>
            State == JobState.Downloading ||
            State == JobState.Pausing ||
            State == JobState.Retrying;

        /// <summary>
        /// Returns true if the job can be resumed (has partial data on disk).
        /// </summary>
        [JsonIgnore]
        public bool CanResume =>
            State == JobState.Paused ||
            State == JobState.Interrupted ||
            State == JobState.Retrying;

        /// <summary>
        /// Creates a shallow copy for immutable-style updates.
        /// </summary>
        public DownloadJob With(Action<DownloadJob> mutate)
        {
            var copy = (DownloadJob)MemberwiseClone();
            mutate(copy);
            copy.UpdatedUtc = DateTimeOffset.UtcNow;
            return copy;
        }
    }

    /// <summary>
    /// Result of a bulk URL import operation.
    /// </summary>
    public sealed class ImportResult
    {
        public int Accepted { get; set; }
        public int Rejected { get; set; }
        public int Duplicates { get; set; }
        public System.Collections.Generic.List<string> Errors { get; } = new();
        public System.Collections.Generic.List<DownloadJob> Jobs { get; } = new();

        public override string ToString() =>
            $"ImportResult: {Accepted} accepted, {Rejected} rejected, {Duplicates} duplicates, {Errors.Count} errors";
    }

    /// <summary>
    /// Scheduler configuration (persisted or configurable).
    /// </summary>
    public sealed class SchedulerSettings
    {
        /// <summary>Maximum number of simultaneously active file downloads.</summary>
        public int ActiveDownloadLimit { get; set; } = 3;

        /// <summary>Global maximum simultaneous HTTP transfer operations (segments) across all active jobs.</summary>
        public int GlobalConnectionBudget { get; set; } = 16;

        /// <summary>Per-host maximum simultaneous HTTP transfer operations.</summary>
        public int PerHostConnectionBudget { get; set; } = 8;

        /// <summary>Maximum scheduler-level retry attempts for a failed job.</summary>
        public int MaxJobAttempts { get; set; } = 3;

        /// <summary>Base delay for scheduler-level job retry with exponential backoff.</summary>
        public TimeSpan JobRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Maximum delay for scheduler-level job retry.</summary>
        public TimeSpan JobRetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Progress persistence interval.</summary>
        public TimeSpan ProgressPersistInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Database path (relative to LocalAppData or absolute).</summary>
        public string DatabasePath { get; set; } = string.Empty;

        public static SchedulerSettings Default => new();
    }
}