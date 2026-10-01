using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DRRipper
{
    /// <summary>Non-retryable HTTP failure (fatal taxonomy, Ticket #003).</summary>
    internal class NonRetryableHttpException : DownloadFailedException
    {
        public NonRetryableHttpException(string message) : base(message) { }
    }

    /// <summary>Signals that a range request was answered 200: restart as single-stream.</summary>
    internal sealed class RangeNotSupportedException : Exception
    {
        public RangeNotSupportedException(string message) : base(message) { }
    }

    /// <summary>Signals a short clean-EOF chunk that must resume its remainder (transient).</summary>
    internal sealed class IncompleteChunkException : IOException
    {
        public IncompleteChunkException(string message) : base(message) { }
    }

    /// <summary>
    /// Controlled session lifecycle (Ticket #004). The public <see cref="DownloadState"/>
    /// enum is unchanged for UI compatibility; these phases are the internal truth.
    /// Probing/Pausing/Verifying surface publicly as Downloading with a status message.
    /// </summary>
    internal enum SessionPhase { Idle, Probing, Downloading, Pausing, Paused, Verifying, Terminal }

    /// <summary>
    /// Versioned persistent recovery metadata (schema v2).
    /// Describes VERIFIED on-disk bytes (completed ranges / prefix offset), never
    /// queued intentions. Atomic replacement: tmp + flush + move.
    /// </summary>
    internal sealed class RecoveryMetadata
    {
        public int SchemaVersion { get; set; } = 2;
        public string Url { get; set; } = string.Empty;
        public string? ResolvedUrl { get; set; }
        public string FinalFileName { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public string? ETag { get; set; }
        public string? LastModifiedRfc1123 { get; set; }
        public long SegmentSize { get; set; }
        public string Mode { get; set; } = "segmented"; // "segmented" | "prefix"
        public List<long[]> CompletedRanges { get; set; } = new(); // [start,end], merged
        public long PrefixOffset { get; set; }
        public DateTimeOffset CheckpointUtc { get; set; }
        public string Checksum { get; set; } = string.Empty;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        public string ComputeChecksum()
        {
            var clone = MemberwiseClone() as RecoveryMetadata;
            clone!.Checksum = string.Empty;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(clone, JsonOptions);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        public static RecoveryMetadata? TryLoad(string metaPath)
        {
            try
            {
                if (!File.Exists(metaPath)) return null;
                var meta = JsonSerializer.Deserialize<RecoveryMetadata>(File.ReadAllText(metaPath), JsonOptions);
                if (meta == null || meta.SchemaVersion != 2) return null;
                if (string.IsNullOrEmpty(meta.Checksum)) return null;
                if (!string.Equals(meta.ComputeChecksum(), meta.Checksum, StringComparison.OrdinalIgnoreCase)) return null;
                if (meta.TotalSize <= 0 || string.IsNullOrWhiteSpace(meta.Url) || string.IsNullOrWhiteSpace(meta.FinalFileName)) return null;
                if (meta.CompletedRanges == null) return null;
                foreach (var r in meta.CompletedRanges)
                {
                    if (r == null || r.Length != 2 || r[0] < 0 || r[1] < r[0] || r[1] >= meta.TotalSize) return null;
                }
                if (meta.PrefixOffset < 0 || meta.PrefixOffset > meta.TotalSize) return null;
                return meta;
            }
            catch { return null; }
        }

        /// <summary>Atomic snapshot replacement (tmp + flush + move). Same-volume renames on NTFS are atomic.</summary>
        public void WriteAtomically(string metaPath)
        {
            CheckpointUtc = DateTimeOffset.UtcNow;
            Checksum = ComputeChecksum();
            var tmp = metaPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                JsonSerializer.Serialize(fs, this, JsonOptions);
                fs.Flush(true);
            }
            File.Move(tmp, metaPath, overwrite: true);
        }

        public static void DeleteAll(string metaPath)
        {
            try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
            try { if (File.Exists(metaPath + ".tmp")) File.Delete(metaPath + ".tmp"); } catch { }
        }
    }

    /// <summary>
    /// One download job's isolated state (Ticket #004, fixes F-06): URL, paths, validators,
    /// segment map, coverage, pause/cancel machinery, checkpointing and finalization.
    /// The shared <see cref="HttpClient"/> is borrowed, never owned. WPF-independent.
    /// </summary>
    internal sealed class DownloadSession : IDisposable
    {
        private const int CheckpointMinIntervalNote = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(SafeFileHandle hFile);

        public readonly ParallelDownloader Owner;
        public readonly HttpClient Client;

        // Configuration (per StartAsync call).
        public string Url = string.Empty;
        public string TargetDirectory = string.Empty;
        public int Connections = 8;
        public CancellationToken ExternalToken;
        public DownloadRetryPolicy Policy = DownloadRetryPolicy.Default;
        public TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
        public TimeSpan CheckpointInterval = TimeSpan.FromSeconds(2);

        // Resolved identity.
        public string FileName = string.Empty;
        public string FinalPath = string.Empty;
        public string PartPath = string.Empty;
        public string MetaPath = string.Empty;
        public long TotalSize;
        public string? ETag;
        public DateTimeOffset? LastModified;
        public string? ResolvedUrl;
        public bool AcceptRanges;
        public string Mode = "segmented"; // "segmented" | "prefix"

        // Runtime.
        public SessionPhase Phase = SessionPhase.Idle;
        private readonly object _phaseLock = new();
        public CancellationTokenSource? SessionCts;
        public CancellationToken SessionCt;
        public readonly PauseToken PauseToken = new();
        public long PauseGeneration;
        public RangeTracker? Tracker;
        // Work items carry a credit base: bytes [CreditStart, Start) were written
        // by earlier attempts of the same logical chunk (pause splits), so success
        // credits [CreditStart, End] — never recounting, never orphaning (§7).
        public System.Collections.Concurrent.ConcurrentQueue<(long Start, long End, long CreditStart)> WorkQueue = new();
        public System.Collections.Concurrent.ConcurrentDictionary<long, long> ActiveRemainders = new();
        public System.Collections.Concurrent.ConcurrentDictionary<long, CancellationTokenSource> ActiveRequests = new();
        public int ActiveAttempts;
        public long PrefixOffset;
        public bool Recovered;
        public bool AdoptedFinal;
        public string StatusNote = string.Empty;

        private readonly object _faultLock = new();
        private Exception? _fault;
        private bool _fallbackSingleStream;


        // Checkpoint stats / throttling.
        public int CheckpointCount;
        public long CheckpointWriteMs;
        public int CheckpointSkipped;
        public long PauseCheckpointMs;
        private DateTime _lastCheckpoint = DateTime.MinValue;
        // Serializes snapshots: concurrent chunk completions must not share one
        // tmp file (torn replace) nor interleave flush/move.
        private readonly object _checkpointLock = new();
        // Gate diagnostics (Ticket #004): call counts that must reconcile.
        public int CompleteRangeCalls;
        public int RequeueCount;

        // Destination handle for the current run.
        public FileStream? DestStream;
        public SafeFileHandle? DestHandle;

        public DownloadSession(ParallelDownloader owner, HttpClient client)
        {
            Owner = owner;
            Client = client;
        }

        // ---------- fault plumbing (per-session; replaces shared _sessionFault) ----------

        public void RecordFault(Exception ex)
        {
            lock (_faultLock) { _fault ??= ex; }
            try { SessionCts?.Cancel(); } catch { }
        }

        public Exception? TakeFault()
        {
            lock (_faultLock) return _fault;
        }

        public void RequestFallbackSingleStream()
        {
            _fallbackSingleStream = true;
            try { SessionCts?.Cancel(); } catch { }
        }

        public bool TakeFallback()
        {
            if (_fallbackSingleStream && TakeFault() == null)
            {
                _fallbackSingleStream = false;
                return true;
            }
            return false;
        }

        // ---------- identity ----------

        public static bool IsStrongETag(string? tag) =>
            !string.IsNullOrEmpty(tag) && !tag.StartsWith("W/", StringComparison.Ordinal);

        /// <summary>
        /// Conservative cross-run identity agreement (Ticket #004 §6).
        /// Strong ETag match wins. Weak ETags additionally require Last-Modified
        /// agreement. No validators on either side -&gt; NO agreement (full restart).
        /// </summary>
        public static bool IdentityAgrees(RecoveryMetadata meta, string? probeETag, DateTimeOffset? probeLastMod)
        {
            bool metaStrong = IsStrongETag(meta.ETag);
            bool probeStrong = IsStrongETag(probeETag);
            if (metaStrong && probeStrong)
                return string.Equals(meta.ETag, probeETag, StringComparison.Ordinal);
            if (metaStrong || probeStrong)
                return false; // one side upgraded/downgraded validators: unsafe
            DateTimeOffset? metaLm = null;
            if (!string.IsNullOrEmpty(meta.LastModifiedRfc1123) &&
                DateTimeOffset.TryParse(meta.LastModifiedRfc1123, out var parsed)) metaLm = parsed;
            if (metaLm.HasValue && probeLastMod.HasValue)
                return metaLm.Value == probeLastMod.Value;
            return false; // cannot establish identity: conservative restart
        }

        // ---------- recovery (Ticket #004 §5, fixes F-03) ----------

        public sealed record PrepareResult(bool Resume, bool AdoptedFinal, string Reason);

        /// <summary>
        /// Load + validate metadata BEFORE touching the destination in a truncating
        /// mode. Returns whether validated partial data may be reused.
        /// </summary>
        public PrepareResult Prepare()
        {
            // Drop obsolete v1 metadata (remaining-queue format, incompatible).
            try { var legacy = FinalPath + ".drmeta"; if (File.Exists(legacy)) File.Delete(legacy); } catch { }

            PartPath = FinalPath + ".part";
            MetaPath = PartPath + ".drmeta";

            var meta = RecoveryMetadata.TryLoad(MetaPath);
            bool partExists = File.Exists(PartPath);
            long partLength = -1;
            if (partExists) { try { partLength = new FileInfo(PartPath).Length; } catch { partExists = false; } }

            if (meta != null)
            {
                string? problem = ValidateForResume(meta, partExists, partLength);
                if (problem == null)
                {
                    // Validated resume: reconstruct coverage WITHOUT recreating the file.
                    // Cross-mode resumes are supported: segmented snapshots yield a
                    // contiguous prefix for prefix runs and vice versa (§11).
                    Recovered = true;
                    ETag = meta.ETag;
                    if (meta.Mode == "segmented" && Mode == "segmented")
                    {
                        Tracker = new RangeTracker(TotalSize);
                        foreach (var r in meta.CompletedRanges) Tracker.CompleteRange(r[0], r[1]);
                        PrefixOffset = 0;
                        Owner.AddDownloaded(Tracker.CoveredBytes);
                        RebuildMissingQueue();
                    }
                    else if (meta.Mode == "segmented" && Mode == "prefix")
                    {
                        PrefixOffset = ContiguousPrefix(meta);
                        Tracker = null;
                        Owner.AddDownloaded(PrefixOffset);
                    }
                    else if (meta.Mode == "prefix" && Mode == "segmented")
                    {
                        Tracker = new RangeTracker(TotalSize);
                        if (meta.PrefixOffset > 0) Tracker.CompleteRange(0, meta.PrefixOffset - 1);
                        PrefixOffset = meta.PrefixOffset;
                        Owner.AddDownloaded(meta.PrefixOffset);
                        RebuildMissingQueue();
                    }
                    else
                    {
                        PrefixOffset = meta.PrefixOffset;
                        Tracker = null;
                        Owner.AddDownloaded(meta.PrefixOffset);
                    }
                    StatusNote = $"Resumed {Owner.ReadDownloaded()} of {TotalSize} verified bytes.";
                    return new PrepareResult(true, false, StatusNote);
                }

                // Adopt-final: crash between rename and metadata delete.
                if (problem.StartsWith("part-missing", StringComparison.Ordinal) &&
                    File.Exists(FinalPath) && meta.CompletedRanges.Count > 0)
                {
                    try
                    {
                        if (new FileInfo(FinalPath).Length == TotalSize && TrackerCovers(meta, TotalSize))
                        {
                            AdoptedFinal = true;
                            Owner.AddDownloaded(TotalSize);
                            RecoveryMetadata.DeleteAll(MetaPath);
                            return new PrepareResult(true, true, "Adopted completed file after finalization crash.");
                        }
                    }
                    catch { }
                }

                StatusNote = problem;
            }

            // Fresh start: never truncate a legitimate partial download silently here;
            // stale part/meta from an UNRECOVERABLE run are ours to discard, but an
            // unrelated final file is never overwritten (unique name instead).
            if (partExists) { try { File.Delete(PartPath); } catch { } }
            RecoveryMetadata.DeleteAll(MetaPath);
            if (File.Exists(FinalPath)) FinalPath = ResolveUniquePath(FinalPath);
            PartPath = FinalPath + ".part";
            MetaPath = PartPath + ".drmeta";
            Recovered = false;
            return new PrepareResult(false, false, meta == null ? "No valid recovery metadata; starting fresh." : StatusNote);
        }

        private string? ValidateForResume(RecoveryMetadata meta, bool partExists, long partLength)
        {
            if (!string.Equals(meta.Url, Url, StringComparison.Ordinal)) return "url-mismatch: metadata belongs to a different URL.";
            if (!string.Equals(meta.FinalFileName, FileName, StringComparison.Ordinal)) return "identity-mismatch: resolved filename changed.";
            if (meta.TotalSize != TotalSize) return $"size-mismatch: metadata {meta.TotalSize} vs resource {TotalSize}.";
            if (meta.SegmentSize != ParallelDownloader.SegmentSize) return "segment-mismatch: segment size changed.";
            if (!string.IsNullOrEmpty(meta.ResolvedUrl) && !string.IsNullOrEmpty(ResolvedUrl) &&
                !string.Equals(meta.ResolvedUrl, ResolvedUrl, StringComparison.OrdinalIgnoreCase))
                return "redirect-mismatch: resource URL after redirects changed.";
            if (!partExists) return "part-missing: partial destination file is gone.";
            if (partLength != TotalSize) return $"part-length-mismatch: {partLength} vs {TotalSize}; cannot trust offsets.";
            if (!IdentityAgrees(meta, ETag, LastModified)) return "validator-mismatch: resource identity changed or cannot be established; full restart required.";
            if (meta.Mode != "segmented" && meta.Mode != "prefix") return "mode-unknown: unrecognized recovery mode.";
            return null; // resume approved
        }

        /// <summary>Longest verified contiguous prefix [0..k] from merged spans.</summary>
        private static long ContiguousPrefix(RecoveryMetadata meta)
        {
            var spans = meta.CompletedRanges
                .Where(r => r != null && r.Length == 2)
                .Select(r => (Start: r[0], End: r[1]))
                .OrderBy(s => s.Start).ToList();
            long cursor = 0;
            foreach (var (s, e) in spans)
            {
                if (s > cursor) break;
                cursor = Math.Max(cursor, e + 1);
            }
            return Math.Min(cursor, meta.TotalSize);
        }

        private void NoteRangeComplete(long s, long e)
        {
            Tracker!.CompleteRange(s, e);
            Interlocked.Increment(ref CompleteRangeCalls);
        }

        private static bool TrackerCovers(RecoveryMetadata meta, long total)
        {
            try
            {
                var t = new RangeTracker(total);
                foreach (var r in meta.CompletedRanges) t.CompleteRange(r[0], r[1]);
                return t.IsComplete;
            }
            catch { return false; }
        }

        public static string ResolveUniquePath(string finalPath)
        {
            if (!File.Exists(finalPath)) return finalPath;
            var dir = Path.GetDirectoryName(finalPath) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(finalPath);
            var ext = Path.GetExtension(finalPath);
            for (int i = 2; i < 10000; i++)
            {
                var cand = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(cand) && !File.Exists(cand + ".part")) return cand;
            }
            return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
        }

        public void RebuildMissingQueue()
        {
            var q = new System.Collections.Concurrent.ConcurrentQueue<(long Start, long End, long CreditStart)>();
            if (Tracker == null) return;
            var missing = Tracker.MissingRanges();
            foreach (var (s, e) in missing)
            {
                for (long o = s; o <= e; o += ParallelDownloader.SegmentSize)
                {
                    long spanEnd = Math.Min(o + ParallelDownloader.SegmentSize - 1, e);
                    q.Enqueue((o, spanEnd, o));
                }
            }
            WorkQueue = q;
        }

        public void CreateFreshFile()
        {
            ParallelDownloader.EnsureEnoughDiskSpace(TargetDirectory, TotalSize);
            using (var fs = new FileStream(PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                fs.SetLength(TotalSize);
            Tracker = new RangeTracker(TotalSize);
            RebuildMissingQueue();
            PrefixOffset = 0;
        }

        public void OpenExistingFile()
        {
            DestStream = new FileStream(PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            DestHandle = DestStream.SafeFileHandle;
            if (DestHandle == null || DestHandle.IsInvalid)
                throw new DownloadFailedException("Could not open partial destination file.");
        }

        // ---------- checkpointing (Ticket #004 §4/§7) ----------

        /// <summary>
        /// Persist ONLY verified coverage (tracker) or prefix offset, after flushing
        /// file data. Throttled by CheckpointInterval unless forced (pause/final).
        /// Bounded: wedged storage degrades to a skipped checkpoint (or an
        /// explicit failure at finalize) instead of hanging transfer or UI
        /// threads indefinitely (Ticket #004 §9).
        /// </summary>
        public void Checkpoint(bool force) => Checkpoint(force, Timeout.InfiniteTimeSpan);

        public void Checkpoint(bool force, TimeSpan budget)
        {
            if (string.IsNullOrEmpty(MetaPath)) return; // unknown-size direct mode: nothing to persist
            SessionCt.ThrowIfCancellationRequested();
            // NOTE: Timeout.InfiniteTimeSpan is negative (-1ms): only finite
            // budgets take the bounded path below.
            bool bounded = budget >= TimeSpan.Zero;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool entered;
            try
            {
                entered = bounded
                    ? Monitor.TryEnter(_checkpointLock, budget)
                    : Monitor.TryEnter(_checkpointLock);
            }
            catch (ObjectDisposedException) { return; }
            if (!entered)
                throw new TimeoutException("Recovery checkpoint is busy.");
            try
            {
                if (!bounded)
                {
                    CheckpointCore(force);
                    return;
                }
                var remaining = budget - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("Recovery checkpoint budget exhausted.");
                // Run IO off the caller so a wedged store can be abandoned; the
                // previous atomic snapshot stays valid (torn tmp files are ignored).
                var io = Task.Run(() => CheckpointCore(force), CancellationToken.None);
                if (!io.Wait(remaining))
                    throw new TimeoutException("Recovery checkpoint IO timed out.");
                io.GetAwaiter().GetResult();
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException oce)
            {
                throw oce;
            }
            finally { Monitor.Exit(_checkpointLock); }
        }

        private void CheckpointCore(bool force)
        {
            if (string.IsNullOrEmpty(MetaPath)) return; // unknown-size direct mode: nothing to persist
            SessionCt.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            if (!force && (now - _lastCheckpoint) < CheckpointInterval)
            {
                Interlocked.Increment(ref CheckpointSkipped);
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (DestHandle != null && !DestHandle.IsInvalid)
                {
                    try { FlushFileBuffers(DestHandle); } catch { }
                }
                var meta = new RecoveryMetadata
                {
                    Url = Url,
                    ResolvedUrl = ResolvedUrl,
                    FinalFileName = FileName,
                    TotalSize = TotalSize,
                    ETag = ETag,
                    LastModifiedRfc1123 = LastModified?.ToString("R"),
                    SegmentSize = ParallelDownloader.SegmentSize,
                    Mode = Mode,
                    PrefixOffset = PrefixOffset,
                };
                if (Tracker != null)
                {
                    foreach (var (s, e) in Tracker.SnapshotRanges())
                        meta.CompletedRanges.Add(new[] { s, e });
                }
                meta.WriteAtomically(MetaPath);
                CheckpointCount++;
            }
            finally
            {
                sw.Stop();
                CheckpointWriteMs += sw.ElapsedMilliseconds;
                _lastCheckpoint = now;
            }
        }

        // ---------- pause / resume / cancel (Ticket #004 §9) ----------

        // Serializes pause/resume transitions so rapid cycles can't interleave
        // reconcile (requeue/checkpoint/state) mid-flight. Cancel preempts via
        // Terminal phase + CTS and never takes this semaphore.
        private readonly SemaphoreSlim _pauseSem = new(1, 1);

        // Pause-phase telemetry (§14 overhead analysis): last durations in ms.
        public long PauseSemMs;
        public long PauseAckMs;

        public bool RequestPause(TimeSpan ackTimeout)
        {
            PauseSemMs = PauseAckMs = PauseCheckpointMs = -1;
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            if (!_pauseSem.Wait(TimeSpan.FromSeconds(30))) return false;
            PauseSemMs = swTotal.ElapsedMilliseconds;
            try
            {
                return RequestPauseCore(ackTimeout);
            }
            finally { try { _pauseSem.Release(); } catch { } }
        }

        private bool RequestPauseCore(TimeSpan ackTimeout)
        {
            lock (_phaseLock)
            {
                if (Phase == SessionPhase.Paused || Phase == SessionPhase.Pausing) return true;
                if (Phase != SessionPhase.Downloading) return false;
                Phase = SessionPhase.Pausing;
                PauseGeneration++;
            }

            Owner.SetState(DownloadState.Downloading, "Pausing…");

            PauseToken.Pause();


            foreach (var kv in ActiveRequests)
            {
                try { if (!kv.Value.IsCancellationRequested) kv.Value.Cancel(); } catch { }
            }

            // Wait for in-flight attempts to settle (bounded; hung sockets were aborted above).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < ackTimeout)
            {
                if (ActiveAttempts == 0 && ActiveRequests.IsEmpty) break;
                lock (_phaseLock)
                {
                    // Cancel preempts a stalled pause: never finish pausing a dead run.
                    if (Phase == SessionPhase.Terminal) return false;
                }
                Thread.Sleep(50);
            }
            PauseAckMs = sw.ElapsedMilliseconds;
            lock (_phaseLock)
            {
                // A racing Cancel wins: leave state/queue alone for teardown.
                if (Phase != SessionPhase.Pausing) return false;
                // Run already complete (workers drained before pause engaged):
                // back off WITHOUT touching state and let the gate finalize
                // instead of stomping Completed with Paused.
                if (Tracker?.IsComplete == true && WorkQueue.IsEmpty && ActiveAttempts == 0) return false;
            }
            // Reconcile: drops self-requeued exactly once into WorkQueue, so there
            // is nothing to requeue here — just drop stale tracking entries and
            // checkpoint the verified state (bounded: a wedged store degrades to
            // a skipped checkpoint, never a hung Pause).
            try
            {
                ActiveRemainders.Clear();
                var ckSw = System.Diagnostics.Stopwatch.StartNew();
                try { Checkpoint(true, TimeSpan.FromSeconds(15)); }
                finally { PauseCheckpointMs = ckSw.ElapsedMilliseconds; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Owner.SetState(DownloadState.Downloading, "Pause checkpoint failed: " + ex.Message); }
            lock (_phaseLock) { if (Phase == SessionPhase.Pausing) Phase = SessionPhase.Paused; }
            // Cancellation preempts pause (Ticket #004 §9): if Terminal arrived
            // mid-reconcile, leave Cancelled alone — never stomp it with Paused.
            bool paused;
            lock (_phaseLock) { paused = Phase == SessionPhase.Paused; }
            if (paused) Owner.SetState(DownloadState.Paused);
            return paused;
        }

        public bool RequestResume()
        {
            if (!_pauseSem.Wait(TimeSpan.FromSeconds(30))) return false;
            try
            {
                return RequestResumeCore();
            }
            finally { try { _pauseSem.Release(); } catch { } }
        }

        private bool RequestResumeCore()
        {
            lock (_phaseLock)
            {
                if (Phase == SessionPhase.Terminal) return false;
                if (Phase != SessionPhase.Paused && Phase != SessionPhase.Pausing) return false;
            }
            // Validate recovery state before restarting workers (no lock held: IO).
            // If the part is already gone but a validated final exists (finalize
            // won the race with this resume), adopt success instead of failing.
            try
            {
                if (!File.Exists(PartPath))
                {
                    if (File.Exists(FinalPath) && Tracker?.IsComplete == true)
                    {
                        try
                        {
                            if (new FileInfo(FinalPath).Length == TotalSize)
                            {
                                lock (_phaseLock) { Phase = SessionPhase.Terminal; }
                                Owner.SetResolvedFileName(Path.GetFileName(FinalPath));
                                Owner.SetState(DownloadState.Completed);
                                return true;
                            }
                        }
                        catch { }
                    }
                    Fail("Resume failed: partial file is gone."); return false;
                }
                if (new FileInfo(PartPath).Length != TotalSize) { Fail("Resume failed: partial file length changed."); return false; }
            }
            catch (Exception ex) { Fail("Resume validation failed: " + ex.Message); return false; }
            lock (_phaseLock)
            {
                // A racing Cancel wins over resume (Ticket #004 §9).
                if (Phase == SessionPhase.Terminal) return false;
                if (Phase != SessionPhase.Paused && Phase != SessionPhase.Pausing) return false;
                Phase = SessionPhase.Downloading;
            }
            Owner.ResetSpeedWindow();
            PauseToken.Resume();
            Owner.SetState(DownloadState.Downloading);
            return true;
        }

        public void RequestCancel()
        {
            lock (_phaseLock) { Phase = SessionPhase.Terminal; }
            PauseToken.Resume();
            try { SessionCts?.Cancel(); } catch { }
            // Cancel drops the partial download entirely (documented): never resume-cancelled bytes.
            try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
            RecoveryMetadata.DeleteAll(MetaPath);
            Owner.SetState(DownloadState.Cancelled);
        }

        /// <summary>
        /// Stop workers without deleting anything (Dispose / app shutdown):
        /// paused or partial downloads stay recoverable on disk.
        /// </summary>
        public void Teardown()
        {
            TeardownKeepsFiles = true;
            lock (_phaseLock)
            {
                if (Phase != SessionPhase.Terminal) Phase = SessionPhase.Terminal;
            }
            PauseToken.Resume();
            try { SessionCts?.Cancel(); } catch { }
        }

        /// <summary>True when teardown (not cancellation) ended the run: keep files.</summary>
        public bool TeardownKeepsFiles;

        private void Fail(string message)
        {
            lock (_phaseLock)
            {
                if (Phase == SessionPhase.Terminal) return; // Cancel owns the terminal state
            }
            RecordFault(new DownloadFailedException(message));
            Owner.SetState(DownloadState.Failed, message);
        }

        public void Dispose()
        {
            try { DestStream?.Dispose(); } catch { }
            DestStream = null;
            DestHandle = null;
            try { SessionCts?.Dispose(); } catch { }
            SessionCts = null;
        }

        // ---------- run orchestration ----------

        public async Task<string> RunAsync()
        {
            lock (_phaseLock) { Phase = SessionPhase.Probing; }

            if (TotalSize <= 0)
            {
                // Unknown size: direct legacy streaming, no recovery possible.
                FinalPath = ResolveUniquePathIfNeeded(FinalPath);
                PartPath = FinalPath;
                MetaPath = string.Empty;
                Mode = "prefix";
                PrefixOffset = 0;
                Recovered = false;
                return await RunPrefixAsync();
            }

            var prep = Prepare();
            if (prep.AdoptedFinal)
            {
                Owner.SetResolvedFileName(Path.GetFileName(FinalPath));
                Owner.SetState(DownloadState.Completed, "Recovered completed file.");
                return FinalPath;
            }
            if (!prep.Resume)
                CreateFreshFile();

            OpenExistingFile();
            try { Checkpoint(true, TimeSpan.FromSeconds(30)); } catch { } // session-start snapshot (empty or resumed state)
            try { SessionCts?.Dispose(); } catch { }
            SessionCts = CancellationTokenSource.CreateLinkedTokenSource(ExternalToken);
            SessionCt = SessionCts.Token;
            lock (_phaseLock) { Phase = SessionPhase.Downloading; }
            Owner.SetState(DownloadState.Downloading,
                prep.Resume ? $"Resuming… ({StatusNote})" : "Starting");

            try
            {
                if (Mode == "prefix")
                    return await RunPrefixAsync();
                return await RunSegmentedAsync();
            }
            catch (OperationCanceledException)
            {
                // Terminal mapping (F-13): external cancel on paths that manage
                // their own CTS (prefix/fallback) still reports Cancelled, never Completed.
                // Plain external cancel drops partials (consistent with segmented);
                // Teardown (Dispose) preserves them for recovery.
                if (TakeFault() == null)
                {
                    if (!TeardownKeepsFiles)
                    {
                        try { DestStream?.Dispose(); } catch { }
                        DestStream = null; DestHandle = null;
                        try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
                        RecoveryMetadata.DeleteAll(MetaPath);
                    }
                    Owner.SetState(DownloadState.Cancelled);
                }
                throw;
            }
            catch (DownloadFailedException ex)
            {
                Owner.SetState(DownloadState.Failed, ex.Message);
                throw;
            }
        }

        private static string ResolveUniquePathIfNeeded(string finalPath) => finalPath;

        /// <summary>
        /// Cancellable, pause-aware backoff. Returns false when a pause generation
        /// elapsed mid-wait (caller drops its chunk); holds no attempt slot.
        /// </summary>
        private async Task<bool> DelayWithPauseAsync(TimeSpan delay, long gen)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < delay)
            {
                SessionCt.ThrowIfCancellationRequested();
                PauseToken.WaitIfPaused(SessionCt);
                if (PauseGeneration != gen) return false;
                var left = delay - sw.Elapsed;
                await Task.Delay(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250), SessionCt);
            }
            return true;
        }

        private static async Task<bool> SettleReadAsync(Task<int>? readTask)
        {
            // Observe the orphaned read so its exception never goes unobserved and
            // the buffer is provably idle before reuse (Ticket #004 §8).
            // Returns false if the read is still pending after a grace period.
            if (readTask == null) return true;
            try { await readTask.WaitAsync(TimeSpan.FromSeconds(5)); return true; } catch { return readTask.IsCompleted; }
        }

        // ---------- segmented run (Ticket #003 gate preserved, session-owned) ----------

        public async Task<string> RunSegmentedAsync()
        {
            if (Tracker == null) Tracker = new RangeTracker(TotalSize);
            var tasks = new List<Task>();
            for (int i = 0; i < Connections; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    while (!SessionCt.IsCancellationRequested)
                    {
                        // The pause TOKEN is the single source of parking truth
                        // (never spin on phase: a flapped phase + set token would
                        // busy-loop). Terminal exits; drops re-dequeue after resume.
                        PauseToken.WaitIfPaused(SessionCt);
                        lock (_phaseLock)
                        {
                            if (Phase == SessionPhase.Terminal) break;
                        }
                        if (!WorkQueue.TryDequeue(out var chunk)) break;
                        long gen = PauseGeneration;
                        try { ActiveRemainders[chunk.Start] = chunk.End; } catch { }
                        // NOTE: no attempt count here — attempt slots are counted
                        // per-iteration inside DownloadChunkAsync, so parked
                        // workers never stall Pause() acknowledgement.
                        try
                        {
                            bool done = await DownloadChunkAsync(chunk, gen);
                            if (!done)
                            {
                                // Paused-drop (self-requeued): park until resume,
                                // then dequeue again (never exit: exiting would
                                // drain the worker pool mid-run).
                                PauseToken.WaitIfPaused(SessionCt);
                                continue;
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (RangeNotSupportedException)
                        {
                            RequestFallbackSingleStream();
                            break;
                        }
                        catch (Exception ex)
                        {
                            RecordFault(ex is DownloadFailedException dfe ? dfe :
                                new DownloadFailedException($"Segment [{chunk.Start}-{chunk.End}] failed: {ex.Message}", ex));
                            break;
                        }
                    }
                }, SessionCt));
            }

            await Task.WhenAll(tasks);

            // Run-end reconciliation runs under the pause semaphore so pause,
            // resume, gate and finalize are mutually exclusive: no transition
            // can stomp a concurrent terminal state (Ticket #004 §9).
            if (!await _pauseSem.WaitAsync(TimeSpan.FromSeconds(30)))
                throw new DownloadFailedException("Run-end reconciliation timed out waiting for pause machinery.");
            string? finalized;
            try
            {
                finalized = await RunEndDecideLockedAsync();
            }
            finally { try { _pauseSem.Release(); } catch { } }
            if (finalized == null)
            {
                // Long prefix fallback transfer runs OUTSIDE the semaphore so
                // pause stays responsive during single-stream fallback.
                return await RunPrefixAsync();
            }
            return finalized;
        }

        /// <summary>
        /// Terminal decision gate (semaphore held): fallback flag, cancel/fault
        /// mapping, completion-gate verification and finalization. Returns the
        /// final path, or null when the caller must run the prefix fallback
        /// outside the semaphore. Throws on any problem.
        /// </summary>
        /// <returns>Final path, or null to run the prefix fallback outside.</returns>
        private async Task<string?> RunEndDecideLockedAsync()
        {
            // 200-to-range fallback: clean prefix restart inside this session.
            if (TakeFallback() && TakeFault() == null && !ExternalToken.IsCancellationRequested)
            {
                FullRestart("Server ignores ranges; falling back to single-stream.");
                return null;
            }

            if (SessionCt.IsCancellationRequested && TakeFault() == null)
            {
                // External cancel (no RequestCancel): drop partials too, never Completed.
                // Teardown (Dispose) keeps files for later recovery instead.
                if (!TeardownKeepsFiles)
                {
                    try { if (DestStream != null) { try { DestStream.Dispose(); } catch { } DestStream = null; DestHandle = null; } } catch { }
                    try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
                    RecoveryMetadata.DeleteAll(MetaPath);
                }
                Owner.SetState(DownloadState.Cancelled);
                throw new OperationCanceledException(SessionCt);
            }

            var fault = TakeFault();
            if (fault != null)
            {
                Owner.SetState(DownloadState.Failed, fault.Message);
                RecoveryMetadata.DeleteAll(MetaPath);
                ExceptionDispatchInfo.Capture(fault).Throw();
            }

            long accounted = Owner.ReadDownloaded();
            long onDisk = -1;
            Exception? gateError = null;
            try { onDisk = new FileInfo(PartPath).Length; } catch (Exception ex) { gateError = ex; }

            if (Tracker == null || !Tracker.IsComplete || accounted != TotalSize || onDisk != TotalSize)
            {
                var msg = $"Completion gate rejected download: range coverage {Tracker?.CoveredBytes ?? -1}/{TotalSize} bytes, " +
                    $"accounted {accounted}/{TotalSize} bytes, on-disk {onDisk}/{TotalSize} bytes, " +
                    $"completeCalls={CompleteRangeCalls} requeues={RequeueCount} queuedLeft={WorkQueue.Count} checkpoints={CheckpointCount} ckptSkipped={CheckpointSkipped}." +
                    (gateError != null ? $" File check error: {gateError.Message}" : string.Empty);
                Owner.SetState(DownloadState.Failed, msg);
                throw new DownloadFailedException(msg);
            }

            return await FinalizeAsync();
        }

        /// <summary>Discard partial state and restart this session from zero (same paths).</summary>
        public void FullRestart(string reason)
        {
            StatusNote = reason;
            Owner.SetState(DownloadState.Downloading, reason);
            try { DestStream?.Dispose(); } catch { }
            DestStream = null; DestHandle = null;
            try { SessionCts?.Dispose(); } catch { }
            // Fresh CTS: the previous one was cancelled to stop workers (fallback path).
            SessionCts = CancellationTokenSource.CreateLinkedTokenSource(ExternalToken);
            SessionCt = SessionCts.Token;
            try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
            RecoveryMetadata.DeleteAll(MetaPath);
            Recovered = false;

            Tracker = new RangeTracker(TotalSize);
            RebuildMissingQueue();
            PrefixOffset = 0;
            Owner.ResetDownloaded(0);
            CreateFreshFile();
            OpenExistingFile();
        }

        // ---------- strict chunk transfer (moved from Ticket #003, session-owned + settled reads) ----------

        private async Task<bool> DownloadChunkAsync((long Start, long End, long CreditStart) chunk, long gen)
        {
            long origStart = chunk.Start, origEnd = chunk.End, creditStart = chunk.CreditStart;
            lock (_phaseLock)
            {
                // Dequeued during a pause transition: don't start work; hand the
                // entry back to the queue exactly once (Pause() never requeues).
                if (Phase == SessionPhase.Terminal) throw new OperationCanceledException(SessionCt);
                if (Phase == SessionPhase.Pausing || Phase == SessionPhase.Paused)
                {
                    DropWithRequeue(origStart, chunk.Start, chunk.Start);
                    return false;
                }
            }
            var policy = Policy ?? DownloadRetryPolicy.Default;
            int consecutiveFailures = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            bool abandonBuffer = false;
            // Drop protocol (Ticket #004 §7/§9): a paused-drop is requeued EXACTLY
            // ONCE by the worker that owns it (Pause() never requeues, so no
            // duplicate can arise). Returns true when the drop completed the chunk.
            bool DropWithRequeue(long knownKeyA, long knownKeyB, long resumeFrom)
            {
                try { ActiveRemainders.TryRemove(knownKeyA, out _); } catch { }
                if (knownKeyB != knownKeyA) { try { ActiveRemainders.TryRemove(knownKeyB, out _); } catch { } }
                if (resumeFrom > origEnd)
                {
                    NoteRangeComplete(creditStart, origEnd);
                    try { Checkpoint(false, TimeSpan.FromSeconds(5)); } catch { }
                    return true;
                }
                Interlocked.Increment(ref RequeueCount);
                try { WorkQueue.Enqueue((resumeFrom, origEnd, creditStart)); } catch { }
                return false;
            }
            try
            {
                // First request after a validated recovery carries If-Range so a
                // changed resource restarts instead of mixing versions (§6).
                bool useIfRange = Recovered && IsStrongETag(ETag);
                Recovered = false;
                while (true)
                {
                    SessionCt.ThrowIfCancellationRequested();
                    PauseToken.WaitIfPaused(SessionCt);
                    if (PauseGeneration != gen)
                    {
                        // Self-requeue BEFORE Pause() snapshots: Pause() never
                        // requeues, so this single handoff cannot duplicate.
                        DropWithRequeue(origStart, chunk.Start, chunk.Start);
                        return false;
                    }
                    if (abandonBuffer)
                    {
                        // A previous read never settled: its buffer may still be
                        // written by the orphaned operation, so never reuse or
                        // return it — rent fresh (256 KB pathological leak cap).
                        buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                        abandonBuffer = false;
                    }

                    long reqStart = chunk.Start, reqEnd = chunk.End;
                    long expected = reqEnd - reqStart + 1;
                    long writtenThisAttempt = 0;
                    TimeSpan? attemptRetryAfter = null;
                    TimeSpan? backoffDelay = null;
                    CancellationTokenSource? perReq = null;
                    // Attempt slot: held only while transferring or backing off
                    // outside parking, so Pause() acknowledgement always settles.
                    Interlocked.Increment(ref ActiveAttempts);
                    try
                    {
                        perReq = CancellationTokenSource.CreateLinkedTokenSource(SessionCt);
                        try { ActiveRequests.AddOrUpdate(reqStart, perReq, (_, _) => perReq); } catch { }

                        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
                        req.Headers.Range = new RangeHeaderValue(reqStart, reqEnd);
                        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                        if (useIfRange)
                        {
                            useIfRange = false;
                            try { req.Headers.IfRange = new RangeConditionHeaderValue(ETag!); } catch { }
                        }
                        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token);

                        int code = (int)resp.StatusCode;
                        if (resp.StatusCode == HttpStatusCode.OK)
                            throw new RangeNotSupportedException($"Server answered 200 to range [{reqStart}-{reqEnd}]; ranges unsupported.");
                        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable ||
                            resp.StatusCode == HttpStatusCode.PreconditionFailed)
                        {
                            // Stale recovery (changed resource or range support lost): restart cleanly.
                            FullRestart($"Server rejected resumed range [{reqStart}-{reqEnd}] ({code}); restarting.");
                            chunk = (origStart, origEnd, origStart); creditStart = origStart;
                            gen = PauseGeneration;
                            consecutiveFailures = 0;
                            continue;
                        }
                        if (Policy != null && IsRetryableStatus(code))
                        {
                            attemptRetryAfter = ParallelDownloader.GetRetryAfterDelay(resp);
                            throw new HttpRequestException($"HTTP {code} ({resp.StatusCode}) for range [{reqStart}-{reqEnd}].");
                        }
                        if (code >= 400 && code < 500)
                            throw new NonRetryableHttpException($"HTTP {code} ({resp.StatusCode}) for range [{reqStart}-{reqEnd}].");

                        ParallelDownloader.ValidateRangeResponse(resp, reqStart, reqEnd, TotalSize, ETag, LastModified);

                        try { if (Owner.GetState() == DownloadState.Retrying) Owner.SetState(DownloadState.Downloading); } catch { }

                        using var stream = await resp.Content.ReadAsStreamAsync(perReq.Token);
                        long written = 0;
                        while (true)
                        {
                            // NOTE: no WaitIfPaused here (Ticket #004 §9): parking
                            // inside a counted attempt would stall Pause()
                            // acknowledgement. Pause is observed via per-request
                            // cancellation (-> drop) and the gen check below.
                            SessionCt.ThrowIfCancellationRequested();
                            if (PauseGeneration != gen)
                            {
                                if (DropWithRequeue(origStart, reqStart, reqStart + writtenThisAttempt)) return true;
                                return false;
                            }
                            Task<int>? readTask = null;
                            int read;
                            try
                            {
                                // Ticket #004 §8: the read task is settled before the
                                // buffer is ever reused or returned (no orphaned reads).
                                readTask = stream.ReadAsync(buffer.AsMemory(0, buffer.Length), perReq.Token).AsTask();
                                read = await readTask.WaitAsync(ReadTimeout, perReq.Token);
                            }
                            catch (TimeoutException ex)
                            {
                                try { perReq.Cancel(); } catch { }
                                if (!await SettleReadAsync(readTask)) abandonBuffer = true;
                                throw new IncompleteChunkException($"Range [{reqStart}-{reqEnd}]: read stalled after {written} of {expected} bytes ({ex.Message}).");
                            }
                            catch (OperationCanceledException)
                            {
                                if (!await SettleReadAsync(readTask)) abandonBuffer = true;
                                if (SessionCt.IsCancellationRequested) throw;
                                if (DropWithRequeue(origStart, reqStart, reqStart + writtenThisAttempt)) return true;
                                return false; // paused-drop, self-requeued above
                            }

                            if (read <= 0)
                            {
                                if (written >= expected) break;
                                throw new IncompleteChunkException($"Range [{reqStart}-{reqEnd}]: clean EOF after {written} of {expected} bytes.");
                            }

                            long remaining = expected - written;
                            int bytesToWrite = remaining > 0 ? (int)Math.Min(read, remaining) : read;
                            await RandomAccess.WriteAsync(DestHandle!, new ReadOnlyMemory<byte>(buffer, 0, bytesToWrite), reqStart + written, SessionCt);
                            written += bytesToWrite;
                            Owner.AddDownloaded(bytesToWrite);
                            writtenThisAttempt += bytesToWrite;
                            if (written >= expected) break;
                        }

                        // Success credits the FULL logical span including pre-pause
                        // bytes written by earlier attempts (they are on disk).
                        NoteRangeComplete(creditStart, origEnd);
                        try { ActiveRemainders.TryRemove(origStart, out _); } catch { }
                        try { ActiveRemainders.TryRemove(reqStart, out _); } catch { }
                        try { Checkpoint(false, TimeSpan.FromSeconds(5)); } catch { }
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        await SettleReadAsync(null);
                        if (SessionCt.IsCancellationRequested) throw;
                        if (DropWithRequeue(origStart, reqStart, reqStart + writtenThisAttempt)) return true;
                        return false; // paused-drop (or teardown), self-requeued above
                    }
                    catch (Exception ex) when (ParallelDownloader.IsTransientNetworkError(ex))
                    {
                        if (writtenThisAttempt > 0)
                        {
                            long newStart = chunk.Start + writtenThisAttempt;
                            try { ActiveRemainders.TryRemove(reqStart, out _); } catch { }
                            try { ActiveRemainders.TryRemove(chunk.Start, out _); } catch { }
                            chunk = (newStart, chunk.End, chunk.CreditStart);
                            try { ActiveRemainders[newStart] = origEnd; } catch { }
                            consecutiveFailures = 0;
                            Owner.SetState(DownloadState.Retrying, "Resuming range...");
                        }
                        else
                        {
                            consecutiveFailures++;
                            if (consecutiveFailures >= policy.MaxAttempts)
                            {
                                throw new DownloadFailedException(
                                    $"Range [{origStart}-{origEnd}] failed after {consecutiveFailures} attempts without progress. " +
                                    $"Last error: {ex.GetType().Name}: {ex.Message}", ex);
                            }
                            Owner.SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                            backoffDelay = policy.ComputeDelay(consecutiveFailures, attemptRetryAfter);
                        }
                    }
                    finally
                    {
                        try { ActiveRequests.TryRemove(reqStart, out _); } catch { }
                        try { perReq?.Dispose(); } catch { }
                        Interlocked.Decrement(ref ActiveAttempts);
                    }
                    if (backoffDelay is { } backoff)
                    {
                        // Backoff holds no attempt slot: parking here never stalls ack.
                        if (!await DelayWithPauseAsync(backoff, gen))
                        {
                            DropWithRequeue(origStart, chunk.Start, chunk.Start);
                            return false;
                        }
                    }
                }
            }
            finally
            {
                if (!abandonBuffer) ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ---------- prefix (single-stream) run with recovery (Ticket #004 §11) ----------

        public async Task<string> RunPrefixAsync()
        {
            var policy = Policy ?? DownloadRetryPolicy.Default;
            if (DestStream == null) OpenOrCreatePrefixFile();
            int consecutiveFailures = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                bool useIfRange = Recovered && IsStrongETag(ETag);
                Recovered = false;
                while (true)
                {
                    SessionCt.ThrowIfCancellationRequested();
                    PauseToken.WaitIfPaused(SessionCt);
                    long writtenThisAttempt = 0;
                    TimeSpan? attemptRetryAfter = null;
                    TimeSpan? backoffDelay = null;
                    CancellationTokenSource? perReq = null;
                    try
                    {
                        perReq = CancellationTokenSource.CreateLinkedTokenSource(SessionCt);
                        try { ActiveRequests.AddOrUpdate(-1, perReq, (_, _) => perReq); } catch { }

                        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
                        if (PrefixOffset > 0)
                            req.Headers.Range = new RangeHeaderValue(PrefixOffset, null);
                        if (useIfRange)
                        {
                            useIfRange = false;
                            try { req.Headers.IfRange = new RangeConditionHeaderValue(ETag!); } catch { }
                        }

                        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token);
                        int sc = (int)resp.StatusCode;

                        if (PrefixOffset > 0 && (resp.StatusCode == HttpStatusCode.OK ||
                            resp.StatusCode == HttpStatusCode.PreconditionFailed ||
                            resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                        {
                            // §11B / changed resource: cannot reuse the prefix safely.
                            FullRestartPrefix("Server cannot resume prefix; restarting transfer safely.");
                            consecutiveFailures = 0;
                            continue;
                        }
                        if (PrefixOffset > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                            throw new NonRetryableHttpException(
                                $"Server does not support resume at offset {PrefixOffset} (got {sc} {resp.StatusCode}).");
                        if (Policy != null && IsRetryableStatus(sc))
                        {
                            attemptRetryAfter = ParallelDownloader.GetRetryAfterDelay(resp);
                            throw new HttpRequestException($"HTTP {sc} ({resp.StatusCode}) for prefix download.");
                        }
                        if (sc >= 400 && sc < 500)
                            throw new NonRetryableHttpException($"HTTP {sc} ({resp.StatusCode}) for prefix download.");
                        resp.EnsureSuccessStatusCode();

                        long? reported = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength;
                        if (PrefixOffset == 0 && reported.HasValue && TotalSize > 0 && reported.Value != TotalSize)
                            throw new NonRetryableHttpException($"Server total changed ({TotalSize} -> {reported}); refusing to mix versions.");
                        if (PrefixOffset > 0)
                        {
                            var rcr = resp.Content.Headers.ContentRange;
                            if (rcr != null && rcr.HasRange && rcr.From != PrefixOffset)
                                throw new NonRetryableHttpException($"Resume range mismatch: requested from {PrefixOffset}, server sent from {rcr.From}.");
                            if (rcr?.Length is long rl && TotalSize > 0 && rl != TotalSize)
                                throw new NonRetryableHttpException($"Server total changed mid-download ({TotalSize} -> {rl}).");
                        }

                        try { if (Owner.GetState() == DownloadState.Retrying) Owner.SetState(DownloadState.Downloading); } catch { }

                        using var stream = await resp.Content.ReadAsStreamAsync(perReq.Token);
                        while (true)
                        {
                            SessionCt.ThrowIfCancellationRequested();
                            // NOTE: no WaitIfPaused here (Ticket #004 §9): parking
                            // inside a counted attempt (request entry held) would
                            // stall Pause() acknowledgement. Pause is observed via
                            // per-request cancellation (-> drop) below.
                            Task<int>? readTask = null;
                            int read;
                            try
                            {
                                readTask = stream.ReadAsync(buffer.AsMemory(0, buffer.Length), perReq.Token).AsTask();
                                read = await readTask.WaitAsync(ReadTimeout, perReq.Token);
                            }
                            catch (TimeoutException ex)
                            {
                                try { perReq.Cancel(); } catch { }
                                await SettleReadAsync(readTask);
                                throw new IncompleteChunkException($"Prefix read stalled at offset {PrefixOffset} ({ex.Message}).");
                            }
                            catch (OperationCanceledException)
                            {
                                await SettleReadAsync(readTask);
                                if (SessionCt.IsCancellationRequested) throw;
                                // No parking here (request entry still held — the
                                // outer loop parks cleanly after this break).
                                break; // paused: re-request from persisted PrefixOffset
                            }
                            if (read <= 0) break;

                            await RandomAccess.WriteAsync(DestHandle!, new ReadOnlyMemory<byte>(buffer, 0, read), PrefixOffset, SessionCt);
                            PrefixOffset += read;
                            writtenThisAttempt += read;
                            Owner.AddDownloaded(read);
                            try { Checkpoint(false, TimeSpan.FromSeconds(5)); } catch { }
                        }

                        if (TotalSize > 0 && PrefixOffset < TotalSize)
                        {
                            consecutiveFailures = 0;
                            Owner.SetState(DownloadState.Retrying, "Resuming stream...");
                            continue;
                        }
                        if (TotalSize > 0 && PrefixOffset != TotalSize)
                            throw new DownloadFailedException($"Prefix length {PrefixOffset} != expected {TotalSize}.");

                        Owner.SetState(DownloadState.Completed);
                        return await FinalizeAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        if (SessionCt.IsCancellationRequested) throw;
                        // No parking here (request entry still held); the outer
                        // loop top parks with nothing held after continue.
                        continue; // paused: loop re-requests from PrefixOffset
                    }
                    catch (Exception ex) when (ParallelDownloader.IsTransientNetworkError(ex))
                    {
                        if (writtenThisAttempt > 0)
                        {
                            consecutiveFailures = 0;
                            Owner.SetState(DownloadState.Retrying, "Resuming stream...");
                            continue;
                        }
                        consecutiveFailures++;
                        if (consecutiveFailures >= policy.MaxAttempts)
                        {
                            var done = new DownloadFailedException(
                                $"Prefix download failed after {consecutiveFailures} attempts without progress at offset {PrefixOffset}. " +
                                $"Last error: {ex.GetType().Name}: {ex.Message}", ex);
                            Owner.SetState(DownloadState.Failed, done.Message);
                            throw done;
                        }
                        Owner.SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                        // Prefix has no queue: compute the backoff inside, but wait
                        // OUTSIDE the attempt scope so parking never holds the
                        // request entry (ack must always settle).
                        backoffDelay = policy.ComputeDelay(consecutiveFailures, attemptRetryAfter);
                    }
                    finally { try { ActiveRequests.TryRemove(-1, out _); } catch { } try { perReq?.Dispose(); } catch { } }
                    if (backoffDelay is { } backoff)
                    {
                        if (!await DelayWithPauseAsync(backoff, PauseGeneration))
                            PauseToken.WaitIfPaused(SessionCt);
                    }
                    continue;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private void OpenOrCreatePrefixFile()
        {
            if (TotalSize > 0 && Recovered && File.Exists(PartPath))
            {
                OpenExistingFile();
                return;
            }
            using (var fs = new FileStream(PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            if (TotalSize > 0)
            {
                ParallelDownloader.EnsureEnoughDiskSpace(TargetDirectory, TotalSize);
                using (var fs = new FileStream(PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    fs.SetLength(TotalSize);
            }
            OpenExistingFile();
        }

        private void FullRestartPrefix(string reason)
        {
            StatusNote = reason;
            Owner.SetState(DownloadState.Downloading, reason);
            try { DestStream?.Dispose(); } catch { }
            DestStream = null; DestHandle = null;
            try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
            RecoveryMetadata.DeleteAll(MetaPath);
            Recovered = false;
            PrefixOffset = 0;
            Owner.ResetDownloaded(0);
            OpenOrCreatePrefixFile();
        }

        private static bool IsRetryableStatus(int sc) => sc is 408 or 429 or 500 or 502 or 503 or 504 or (>= 500 and <= 599);

        // ---------- finalization (Ticket #004 §12: rename, never copy/concat/hash) ----------

        public async Task<string> FinalizeAsync()
        {
            await Task.Yield();
            lock (_phaseLock) { Phase = SessionPhase.Verifying; }
            Owner.SetState(DownloadState.Downloading, "Verifying…");
            SessionCt.ThrowIfCancellationRequested();
            Checkpoint(true, TimeSpan.FromSeconds(120)); // final flush + snapshot
            try { DestStream?.Dispose(); } catch { }
            DestStream = null; DestHandle = null;

            string target = FinalPath;
            bool moved = false;
            for (int attempt = 0; attempt < 10 && !moved; attempt++)
            {
                SessionCt.ThrowIfCancellationRequested();
                try
                {
                    File.Move(PartPath, target, overwrite: false);
                    FinalPath = target;
                    moved = true;
                }
                catch (IOException) when (File.Exists(target))
                {
                    target = ResolveUniquePath(FinalPath); // never silently overwrite (§12)
                }
            }
            if (!moved)
                throw new DownloadFailedException($"Could not finalize destination '{FinalPath}': rename failed.");
            RecoveryMetadata.DeleteAll(MetaPath);
            lock (_phaseLock) { Phase = SessionPhase.Terminal; }
            Owner.SetResolvedFileName(Path.GetFileName(FinalPath));
            Owner.SetState(DownloadState.Completed);
            return FinalPath;
        }
    }
}
