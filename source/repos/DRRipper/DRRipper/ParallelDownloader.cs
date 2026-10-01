using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DRRipper
{
    public class DownloadProgress
    {
        public long TotalBytesDownloaded { get; set; }
        public double ProgressPercentage { get; set; }
        public double CurrentSpeedMBps { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public string? ResolvedFileName { get; set; }
    }

    public enum DownloadState { Idle, Downloading, Paused, Retrying, Completed, Cancelled, Failed }

    /// <summary>
    /// Failure propagated to StartAsync callers when a download cannot complete.
    /// Cancellation is reported via OperationCanceledException, never this type.
    /// </summary>
    public class DownloadFailedException : IOException
    {
        public DownloadFailedException(string message) : base(message) { }
        public DownloadFailedException(string message, Exception? inner) : base(message, inner) { }
    }

    /// <summary>
    /// Bounded retry policy. MaxAttempts counts CONSECUTIVE failed attempts that
    /// made no forward progress; any received byte resets the streak (chunk
    /// remainder-resume). This keeps resumable transfers alive while bounding
    /// truly stuck ones. Compatible with a future per-session policy object.
    /// </summary>
    public sealed class DownloadRetryPolicy
    {
        public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
        public int MaxAttempts { get; init; } = 5;

        public static DownloadRetryPolicy Default { get; } = new();

        internal TimeSpan ComputeDelay(int consecutiveFailures, TimeSpan? retryAfterHint)
        {
            if (retryAfterHint is { } hint && hint > TimeSpan.Zero)
                return hint > MaxDelay ? MaxDelay : hint;
            double exp = InitialDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, consecutiveFailures - 1));
            double capped = Math.Min(exp, MaxDelay.TotalMilliseconds);
            double jittered = capped * (0.8 + 0.4 * Random.Shared.NextDouble());
            return TimeSpan.FromMilliseconds(Math.Min(jittered, MaxDelay.TotalMilliseconds));
        }
    }

    /// <summary>
    /// In-memory exact-coverage bookkeeping for the completion gate.
    /// Merges completed intervals; duplicates/overlaps never inflate coverage.
    /// A future download-session object can own one of these per job.
    /// </summary>
    internal sealed class RangeTracker
    {
        private readonly long _totalSize;
        private readonly List<(long Start, long End)> _spans = new();
        private readonly object _lock = new();
        private long _coveredBytes;

        public RangeTracker(long totalSize)
        {
            if (totalSize <= 0) throw new ArgumentOutOfRangeException(nameof(totalSize));
            _totalSize = totalSize;
        }

        public long TotalSize => _totalSize;
        public long CoveredBytes { get { lock (_lock) return _coveredBytes; } }
        public bool IsComplete { get { lock (_lock) return _coveredBytes == _totalSize; } }

        public void CompleteRange(long start, long end)
        {
            if (start < 0 || end < start || end >= _totalSize)
                throw new ArgumentOutOfRangeException($"Range [{start},{end}] is outside [0,{_totalSize}).");
            lock (_lock)
            {
                long ns = start, ne = end;
                for (int i = _spans.Count - 1; i >= 0; i--)
                {
                    var (s, e) = _spans[i];
                    if (e + 1 < ns || s > ne + 1) continue; // disjoint (with adjacency merge)
                    ns = Math.Min(ns, s);
                    ne = Math.Max(ne, e);
                    _coveredBytes -= (e - s + 1);
                    _spans.RemoveAt(i);
                }
                _spans.Add((ns, ne));
                _coveredBytes += (ne - ns + 1);
            }
        }

        /// <summary>Ordered snapshot of merged completed spans (for checkpoints).</summary>
        public List<(long Start, long End)> SnapshotRanges()
        {
            lock (_lock)
            {
                var copy = _spans.ToList();
                copy.Sort((a, b) => a.Start.CompareTo(b.Start));
                return copy;
            }
        }

        /// <summary>Complement of coverage over [0,total): ranges still to download.</summary>
        public List<(long Start, long End)> MissingRanges()
        {
            var missing = new List<(long Start, long End)>();
            long cursor = 0;
            foreach (var (s, e) in SnapshotRanges())
            {
                if (s > cursor) missing.Add((cursor, s - 1));
                cursor = Math.Max(cursor, e + 1);
            }
            if (cursor < _totalSize) missing.Add((cursor, _totalSize - 1));
            return missing;
        }
    }

    // Simple pause token using ManualResetEventSlim
    public class PauseToken
    {
        private readonly ManualResetEventSlim _mres = new(true);
        public bool IsPaused => !_mres.IsSet;
        public void Pause() => _mres.Reset();
        public void Resume() => _mres.Set();
        public void WaitIfPaused(CancellationToken ct)
        {
            while (!_mres.IsSet)
            {
                ct.ThrowIfCancellationRequested();
                _mres.Wait(200, ct);
            }
        }
    }

    public class DownloadMetadata
    {
        // Obsolete v1 remaining-queue format (Ticket #004): superseded by the
        // versioned RecoveryMetadata (completed-range snapshots). Kept only so
        // external references still compile; never written or trusted anymore.
        public string Url { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public List<long> ChunkOffsets { get; set; } = new();
        public List<long> ChunkLengths { get; set; } = new();
        public List<long> ChunkDownloaded { get; set; } = new();
    }

    public class ParallelDownloader : IDisposable
    {
        private readonly SocketsHttpHandler _handler;
        private readonly HttpClient _client;
        internal HttpClient Client => _client;
        private DownloadState _state = DownloadState.Idle;
        private readonly object _stateLock = new();
        // The active download session (Ticket #004): all per-run mutable state
        // lives on the session, never on this facade (fixes F-06 staleness).
        private DownloadSession? _session;
        private readonly object _sessionLock = new();
        private string? _currentResolvedFileName;
        private long _currentTotalSize;
        private long _totalDownloaded = 0;
        private long _reportBytes = 0;
        private DateTime _reportTime = DateTime.MinValue;
        private readonly object _reportLock = new();
        private System.Threading.Timer? _reportTimer;
        private long _lastReportBytes = 0;
        private DateTime _lastReportTime = DateTime.MinValue;
        private double _smoothedSpeed = 0.0;
        private readonly object _timerLock = new();
        private string _statusMessage = string.Empty;
        // network availability handler reference for proper unsubscribe
        private System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler? _networkAvailabilityHandler;

        /// <summary>Bounded retry policy applied to transient failures (defaults: 1s/30s/5 attempts).</summary>
        public DownloadRetryPolicy RetryPolicy { get; set; } = DownloadRetryPolicy.Default;

        /// <summary>Per-read stall timeout (Ticket #004 §8: testable, default 30s).</summary>
        public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Minimum interval between recovery checkpoints (Ticket #004 §4; pause/final always checkpoint).</summary>
        public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Fixed segment size for the dynamic chunk queue (unchanged default).</summary>
        internal const long SegmentSize = 8 * 1024 * 1024;

        internal void AddDownloaded(long bytes) => Interlocked.Add(ref _totalDownloaded, bytes);
        internal long ReadDownloaded() => Interlocked.Read(ref _totalDownloaded);
        internal void ResetDownloaded(long value) => Interlocked.Exchange(ref _totalDownloaded, value);

        // Win32 power management to prevent system sleep during downloads
        private const uint ES_CONTINUOUS = 0x80000000u;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001u;
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        public event Action<DownloadProgress>? ProgressChanged;
        public event Action<DownloadState>? StateChanged;
        private const int CHUNK_SIZE = 8 * 1024 * 1024; // 8MB chunks for dynamic stealing (reduce requests to avoid CDN tarpitting)

        // Filename sanitization to prevent invalid chars and path traversal
        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "download.bin";
            fileName = fileName.Replace("..", "_");
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
                fileName = fileName.Replace(c, '_');
            fileName = fileName.Replace('/', '_').Replace('\\', '_');
            if (string.IsNullOrWhiteSpace(fileName)) return "download.bin";
            return fileName;
        }

        // Ensure target drive has enough free space before pre-allocating file
        internal static void EnsureEnoughDiskSpace(string targetDirectory, long requiredBytes)
        {
            try
            {
                var root = Path.GetPathRoot(targetDirectory);
                if (string.IsNullOrEmpty(root)) return;
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < requiredBytes)
                    throw new IOException($"Insufficient disk space in {root}. Required {requiredBytes} bytes.");
            }
            catch (IOException)
            {
                throw;
            }
            catch
            {
                // if DriveInfo fails (e.g., network path), allow operation to continue and let SetLength fail later
            }
        }

        public ParallelDownloader()
        {
                _handler = new SocketsHttpHandler
            {
                // Max connections large to allow many concurrent streams
                MaxConnectionsPerServer = int.MaxValue,
                // Allow automatic decompression of gzip/deflate/brotli
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                EnableMultipleHttp2Connections = true,
                // Recycle pooled connections periodically to avoid stuck/throttled sockets
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
            };

            // Configure socket options via ConnectCallback to set TcpNoDelay
            _handler.ConnectCallback = async (context, ct) =>
            {
                var dns = context.DnsEndPoint;
                System.Exception? lastEx = null;
                // Resolve host to IP addresses first to avoid AddressFamily.Unspecified issues
                var host = dns.Host;
                var port = dns.Port;
                System.Net.IPAddress[] addrs;
                try
                {
                    addrs = await System.Net.Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Preserve original DNS resolution exception for accurate diagnostics
                    throw;
                }

                foreach (var ip in addrs)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new System.Net.IPEndPoint(ip, port), ct).ConfigureAwait(false);
                        // increase socket buffers to accelerate initial throughput and avoid slow-start penalties
                        try { socket.ReceiveBufferSize = 2 * 1024 * 1024; } catch { }
                        try { socket.SendBufferSize = 1 * 1024 * 1024; } catch { }
                        socket.NoDelay = true;
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { socket.Dispose(); } catch { }
                        // try next address
                    }
                }

                if (lastEx != null) throw lastEx;
                throw new SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
            };

            _client = new HttpClient(_handler, disposeHandler: false);
            // Use a standard browser-like User-Agent and common headers to avoid 403 responses
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            _client.Timeout = Timeout.InfiniteTimeSpan;
            // Listen for network availability changes: surface Retrying while the
            // active session self-recovers (workers own retry). Never auto-resume
            // a user-paused session (Ticket #004 §9).
            _networkAvailabilityHandler = (s, e) =>
            {
                DownloadSession? session;
                lock (_sessionLock) session = _session;
                if (session == null) return;
                lock (_stateLock)
                {
                    if (!e.IsAvailable && _state == DownloadState.Downloading)
                        SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                    else if (e.IsAvailable && _state == DownloadState.Retrying &&
                        session.Phase == SessionPhase.Downloading)
                        SetState(DownloadState.Downloading);
                }
            };
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += _networkAvailabilityHandler;
        }

        internal void SetState(DownloadState s, string? customMessage = null)
        {
            lock (_stateLock) { _state = s; }

            // Update status message unless a custom message is provided
            if (!string.IsNullOrWhiteSpace(customMessage))
            {
                _statusMessage = customMessage!;
            }
            else
            {
                _statusMessage = s switch
                {
                    DownloadState.Idle => "Idle",
                    DownloadState.Downloading => "Downloading",
                    DownloadState.Paused => "Paused",
                    DownloadState.Retrying => "Retrying...",
                    DownloadState.Completed => "Completed",
                    DownloadState.Cancelled => "Cancelled",
                    DownloadState.Failed => "Failed",
                    _ => s.ToString()
                };
            }

            // Manage system sleep prevention when entering/exiting download states
            try
            {
                if (s == DownloadState.Downloading)
                    SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_CONTINUOUS);
                else if (s == DownloadState.Completed || s == DownloadState.Paused || s == DownloadState.Cancelled || s == DownloadState.Failed)
                    SetThreadExecutionState(ES_CONTINUOUS);
            }
            catch { }

            // Immediately report the new state to the UI
            try { ReportNow(_currentTotalSize); } catch { }

            StateChanged?.Invoke(s);
        }

        public DownloadState GetState()
        {
            lock (_stateLock) return _state;
        }

        public void Pause()
        {
            DownloadSession? session;
            lock (_sessionLock) session = _session;
            if (session == null) return;
            // Bounded acknowledgement wait (15s): hung sockets were aborted, so
            // workers settle fast; never block the UI thread indefinitely.
            try { session.RequestPause(TimeSpan.FromSeconds(15)); } catch { }
        }

        public void Resume()
        {
            DownloadSession? session;
            lock (_sessionLock) session = _session;
            if (session == null) return;
            try { session.RequestResume(); } catch { }
        }

        public void Cancel()
        {
            DownloadSession? session;
            lock (_sessionLock) session = _session;
            // Cancellation takes precedence over pause (Ticket #004 §9) and drops
            // the partial download; with no active session just report Cancelled.
            try { session?.RequestCancel(); } catch { }
            if (session == null) SetState(DownloadState.Cancelled);
        }

        public void Dispose()
        {
            // Stop the active session WITHOUT deleting partial files, so teardown
            // never races in-flight requests and paused downloads stay recoverable.
            // Cancellation is observed before the client/handler go away.
            try { lock (_sessionLock) _session?.Teardown(); } catch { }
            try
            {
                if (_networkAvailabilityHandler != null)
                    System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= _networkAvailabilityHandler;
            }
            catch { }

            try { _reportTimer?.Dispose(); } catch { }
            _reportTimer = null;

            // Dispose HttpClient and handler to immediately tear down connections/sockets
            try
            {
                try { _client?.Dispose(); } catch { }
            }
            catch { }
            try
            {
                try { _handler?.Dispose(); } catch { }
            }
            catch { }

            GC.SuppressFinalize(this);
        }

        internal void ResetSpeedWindow()
        {
            // Set report bytes to current total and reset report time to now
            var cur = Interlocked.Read(ref _totalDownloaded);
            Interlocked.Exchange(ref _reportBytes, cur);
            lock (_reportLock) { _reportTime = DateTime.UtcNow; }
        }

        private void ReportTimerTick(object? state)
        {
            ReportNow(_currentTotalSize);
        }

        private void ReportNow(long totalSize)
        {
            // prevent concurrent timer executions
            lock (_timerLock)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var bytesNow = Interlocked.Read(ref _totalDownloaded);
                    var deltaBytes = bytesNow - Interlocked.Read(ref _lastReportBytes);
                    var deltaSecs = (now - (_lastReportTime == DateTime.MinValue ? now : _lastReportTime)).TotalSeconds;
                    if (deltaSecs <= 0) deltaSecs = 0.001;
                    var rawSpeed = deltaBytes / 1024.0 / 1024.0 / deltaSecs;
                    // EMA smoothing; ensure speed never goes negative due to rollbacks or timing anomalies
                    _smoothedSpeed = (_smoothedSpeed * 0.7) + (Math.Max(0.0, rawSpeed) * 0.3);
                    _lastReportBytes = bytesNow;
                    _lastReportTime = now;
                    var percent = totalSize > 0 ? Math.Clamp((bytesNow * 100.0 / totalSize), 0.0, 100.0) : 0.0;
                    var prog = new DownloadProgress
                    {
                        TotalBytesDownloaded = bytesNow,
                        TotalBytes = totalSize,
                        ProgressPercentage = percent,
                        CurrentSpeedMBps = _smoothedSpeed,
                        StatusMessage = _statusMessage,
                        ResolvedFileName = _currentResolvedFileName
                    };

                    ProgressChanged?.Invoke(prog);
                }
                catch { }
            }
        }



        /// <summary>Begin per-run progress reporting for a transfer of known total.</summary>
        internal void StartReporting(long totalSize, string resolvedFileName)
        {
            _currentResolvedFileName = resolvedFileName;
            _currentTotalSize = totalSize;
            Interlocked.Exchange(ref _totalDownloaded, 0);
            Interlocked.Exchange(ref _reportBytes, 0);
            lock (_reportLock) { _reportTime = DateTime.MinValue; }
            Interlocked.Exchange(ref _lastReportBytes, Interlocked.Read(ref _totalDownloaded));
            lock (_timerLock)
            {
                _lastReportTime = DateTime.UtcNow;
                _smoothedSpeed = 0.0;
                try { _reportTimer?.Dispose(); } catch { }
                _reportTimer = new System.Threading.Timer(ReportTimerTick, null, 250, 250);
            }
        }

        internal void StopReporting()
        {
            lock (_timerLock)
            {
                try { _reportTimer?.Dispose(); } catch { }
                _reportTimer = null;
            }
        }

        // Now accepts targetDirectory and returns the final resolved full file path
        public async Task<string> StartAsync(string url, string targetDirectory, int connections = 8, CancellationToken cancellationToken = default)
        {
            // Normalize and validate URL
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is empty", nameof(url));
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                throw new ArgumentException("The provided URL is not a valid absolute URI.", nameof(url));

            if (connections < 1) connections = 1;

            // Set a default Referrer header using scheme+host to help with servers that hotlink-protect
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                {
                    var refUri = new Uri(u.GetLeftPart(UriPartial.Authority));
                    try { _client.DefaultRequestHeaders.Referrer = refUri; } catch { }
                }
            }
            catch { }

            long totalSize = -1;
            bool acceptRanges = false;
            string? suggestedFileName = Path.GetFileName(new Uri(url).LocalPath);
            string? resolvedFileName = null;
            string? mediaType = null;
            // Session resource identity: every 206 response must agree with these
            // when both sides present values (strict range validation, §4).
            string? sessionETag = null;
            DateTimeOffset? sessionLastModified = null;

            HttpResponseMessage? headResp = null;
            try
            {
                // Try HEAD first
                var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                headResp = await _client.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (headResp.IsSuccessStatusCode)
                {
                    if (headResp.Content.Headers.ContentLength.HasValue)
                        totalSize = headResp.Content.Headers.ContentLength.Value;

                    mediaType = headResp.Content.Headers.ContentType?.MediaType;
                    sessionETag ??= headResp.Headers.ETag?.Tag;
                    sessionLastModified ??= headResp.Content.Headers.LastModified;

                    // Content-Disposition parsing
                    var cd = headResp.Content.Headers.ContentDisposition;
                    if (cd != null)
                    {
                        // Prefer FileNameStar (RFC5987) if present
                        if (!string.IsNullOrWhiteSpace(cd.FileNameStar))
                            suggestedFileName = cd.FileNameStar.Trim('"');
                        else if (!string.IsNullOrWhiteSpace(cd.FileName))
                            suggestedFileName = cd.FileName.Trim('"');
                    }

                    if (headResp.Headers.Contains("Accept-Ranges"))
                    {
                        var v = headResp.Headers.GetValues("Accept-Ranges");
                        foreach (var s in v)
                        {
                            if (s.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) >= 0)
                                acceptRanges = true;
                        }
                    }
                }
            }
            catch
            {
                // ignore and fallback to range-get
            }

            // If we couldn't get size or accept-ranges, try a small range GET
            if (totalSize <= 0 || !acceptRanges)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new RangeHeaderValue(0, 0);
                    using (var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500)
                        {
                            // client error during probe - fail fast
                            var msg = $"HTTP {(int)resp.StatusCode} ({resp.StatusCode})";
                            SetState(DownloadState.Failed, msg);
                            throw new NonRetryableHttpException(msg);
                        }

                        if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
                        {
                            if (resp.Content.Headers.ContentLength.HasValue)
                                totalSize = resp.Content.Headers.ContentLength.Value;

                            mediaType = resp.Content.Headers.ContentType?.MediaType;

                            var cd = resp.Content.Headers.ContentDisposition;
                            if (cd != null)
                            {
                                if (!string.IsNullOrWhiteSpace(cd.FileNameStar))
                                    suggestedFileName = cd.FileNameStar.Trim('"');
                                else if (!string.IsNullOrWhiteSpace(cd.FileName))
                                    suggestedFileName = cd.FileName.Trim('"');
                            }

                            if (resp.Headers.Contains("Accept-Ranges"))
                            {
                                var v = resp.Headers.GetValues("Accept-Ranges");
                                foreach (var s in v)
                                {
                                    if (s.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) >= 0)
                                        acceptRanges = true;
                                }
                            }

                            // Some servers respond with Content-Range header giving total size
                            if (resp.Content.Headers.ContentRange != null && resp.Content.Headers.ContentRange.Length.HasValue)
                                totalSize = resp.Content.Headers.ContentRange.Length.Value;

                            sessionETag ??= resp.Headers.ETag?.Tag;
                            sessionLastModified ??= resp.Content.Headers.LastModified;

                            // Resolve filename from this response (final URI after redirects)
                            resolvedFileName = ResolveFileNameFromResponse(resp, suggestedFileName);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // ignore
                }
            }

            // If not yet resolved, try to use headResp if present
            if (string.IsNullOrWhiteSpace(resolvedFileName) && headResp != null)
            {
                resolvedFileName = ResolveFileNameFromResponse(headResp, suggestedFileName);
                mediaType ??= headResp.Content.Headers.ContentType?.MediaType;
            }

            // Fallback to suggestedFileName from URL
            if (string.IsNullOrWhiteSpace(resolvedFileName) && !string.IsNullOrWhiteSpace(suggestedFileName))
                resolvedFileName = System.Net.WebUtility.UrlDecode(suggestedFileName!);

            // If still no filename, derive from URL
            if (string.IsNullOrWhiteSpace(resolvedFileName))
            {
                try
                {
                    var u = new Uri(url);
                    resolvedFileName = System.Net.WebUtility.UrlDecode(Path.GetFileName(u.LocalPath));
                    if (string.IsNullOrWhiteSpace(resolvedFileName)) resolvedFileName = "download.bin";
                }
                catch
                {
                    resolvedFileName = "download.bin";
                }
            }

            // Ensure extension: if missing, use media type mapping
            if (string.IsNullOrWhiteSpace(Path.GetExtension(resolvedFileName)))
            {
                var ext = MapMediaTypeToExtension(mediaType);
                if (!string.IsNullOrWhiteSpace(ext))
                {
                    resolvedFileName = resolvedFileName + ext;
                }
            }
            // Prepare output path and ensure directory exists (Ticket #004 §5:
            // recovery metadata is loaded BEFORE any truncating open happens).
            Directory.CreateDirectory(targetDirectory);
            var safeName = SanitizeFileName(resolvedFileName ?? "download.bin");
            var outputPath = Path.Combine(targetDirectory, safeName);
            string? resolvedUrl = null;
            try { resolvedUrl = headResp?.RequestMessage?.RequestUri?.ToString() ?? url; } catch { resolvedUrl = url; }

            // set initial state and status
            SetState(DownloadState.Downloading, "Starting");

            // Each StartAsync call owns an isolated DownloadSession (Ticket #004 §3):
            // no per-run mutable state is shared across batch files (fixes F-06).
            var session = new DownloadSession(this, _client)
            {
                Url = url,
                TargetDirectory = targetDirectory,
                Connections = connections,
                ExternalToken = cancellationToken,
                Policy = RetryPolicy ?? DownloadRetryPolicy.Default,
                ReadTimeout = ReadTimeout,
                CheckpointInterval = CheckpointInterval,
                FileName = safeName,
                FinalPath = outputPath,
                TotalSize = totalSize,
                ETag = sessionETag,
                LastModified = sessionLastModified,
                ResolvedUrl = resolvedUrl,
                AcceptRanges = acceptRanges,
                Mode = (acceptRanges && connections > 1 && totalSize > 0) ? "segmented" : "prefix",
            };
            lock (_sessionLock)
            {
                try { _session?.Teardown(); } catch { }
                _session = session;
            }
            StartReporting(totalSize, resolvedFileName ?? safeName);
            try
            {
                return await session.RunAsync();
            }
            finally
            {
                StopReporting();
                try { session.Dispose(); } catch { }
            }
        }


        /// <summary>
        /// Strict HTTP range-response validation (§4). Rejects before any body byte
        /// is trusted: 206 status, well-formed Content-Range matching the REQUESTED
        /// span and session total, and stable resource identity. Never trusts
        /// Content-Length for offsets. Throws NonRetryableHttpException (fatal).
        /// </summary>
        internal static void ValidateRangeResponse(
            HttpResponseMessage resp, long reqStart, long reqEnd, long totalSize,
            string? sessionETag, DateTimeOffset? sessionLastModified)
        {
            if (resp.StatusCode != HttpStatusCode.PartialContent)
                throw new NonRetryableHttpException(
                    $"Expected 206 Partial Content for range [{reqStart}-{reqEnd}] but got {(int)resp.StatusCode} ({resp.StatusCode}).");

            var cr = resp.Content.Headers.ContentRange;
            if (cr == null || !cr.HasRange)
                throw new NonRetryableHttpException($"Range [{reqStart}-{reqEnd}]: missing Content-Range header.");
            if (!string.Equals(cr.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
                throw new NonRetryableHttpException($"Range [{reqStart}-{reqEnd}]: unsupported Content-Range unit '{cr.Unit}'.");
            if (cr.From != reqStart || cr.To != reqEnd)
                throw new NonRetryableHttpException(
                    $"Range [{reqStart}-{reqEnd}]: server Content-Range '{cr.Unit} {cr.From}-{cr.To}/{cr.Length}' does not match the requested span.");
            if (cr.Length.HasValue && cr.Length.Value != totalSize)
                throw new NonRetryableHttpException(
                    $"Range [{reqStart}-{reqEnd}]: server total {cr.Length.Value} differs from session total {totalSize}.");

            var etag = resp.Headers.ETag?.Tag;
            if (!string.IsNullOrEmpty(etag) && !string.IsNullOrEmpty(sessionETag) &&
                !string.Equals(etag, sessionETag, StringComparison.Ordinal))
                throw new NonRetryableHttpException(
                    $"Resource identity changed mid-download (ETag '{sessionETag}' -> '{etag}'); refusing to mix versions.");

            var lm = resp.Content.Headers.LastModified;
            if (lm.HasValue && sessionLastModified.HasValue && lm.Value != sessionLastModified.Value)
                throw new NonRetryableHttpException(
                    $"Resource identity changed mid-download (Last-Modified '{sessionLastModified}' -> '{lm}'); refusing to mix versions.");
        }


        private static string? ResolveFileNameFromResponse(HttpResponseMessage resp, string? suggested)
        {
            try
            {
                // Prefer content-disposition headers
                var cd = resp.Content.Headers.ContentDisposition;
                if (cd != null)
                {
                    if (!string.IsNullOrWhiteSpace(cd.FileNameStar))
                        return System.Net.WebUtility.UrlDecode(cd.FileNameStar.Trim('"'));
                    if (!string.IsNullOrWhiteSpace(cd.FileName))
                        return System.Net.WebUtility.UrlDecode(cd.FileName.Trim('"'));
                }

                // Fallback to final request URI
                var reqUri = resp.RequestMessage?.RequestUri;
                if (reqUri != null)
                {
                    var name = Path.GetFileName(reqUri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(name))
                        return System.Net.WebUtility.UrlDecode(name);
                }

                if (!string.IsNullOrWhiteSpace(suggested))
                    return System.Net.WebUtility.UrlDecode(suggested);
            }
            catch
            {
                // ignore
            }
            return null;
        }

        private static string? MapMediaTypeToExtension(string? mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType)) return null;
            mediaType = mediaType.ToLowerInvariant();
            return mediaType switch
            {
                "application/x-rar-compressed" => ".rar",
                "application/zip" => ".zip",
                "application/x-7z-compressed" => ".7z",
                "application/x-iso9660-image" => ".iso",
                "application/x-tar" => ".tar",
                "application/gzip" => ".gz",
                "application/octet-stream" => null,
                _ => null,
            };
        }

        // Shared engine exception taxonomy lives at namespace level in
        // DownloadSession.cs (internal): NonRetryableHttpException,
        // RangeNotSupportedException, IncompleteChunkException.

        private static bool IsRetryableStatus(int statusCode) => statusCode switch
        {
            408 or 429 or 500 or 502 or 503 or 504 => true,
            >= 500 and <= 599 => true, // unknown 5xx: transient, but bounded
            _ => false,
        };

        /// <summary>
        /// True for filesystem failures that retry can never fix (disk full, quota,
        /// access denied, path problems). Unit-tested via synthetic HResults (test N).
        /// </summary>
        internal static bool IsUnrecoverableFilesystemError(Exception ex)
        {
            int hr = ex.HResult & 0xFFFF;
            return hr switch
            {
                0x0027 => true, // ERROR_HANDLE_DISK_FULL
                0x0070 => true, // ERROR_DISK_FULL
                0x0055 => true, // ERROR_QUOTA_EXCEEDED / local quota
                0x03E6 => true, // ERROR_NO_SYSTEM_RESOURCES (quota-ish, non-transient here)
                0x0005 => true, // ERROR_ACCESS_DENIED
                0x0020 => false, // ERROR_SHARING_VIOLATION: likely transient lock; bounded retry applies
                _ => ex is UnauthorizedAccessException,
            };
        }

        internal static bool IsTransientNetworkError(Exception ex) => ex switch
        {
            OperationCanceledException => false, // cancellation is never "transient"
            DownloadFailedException => false,    // already classified (fatal or wrapped)
            RangeNotSupportedException => false,
            HttpRequestException => true,
            SocketException => true,
            TimeoutException => true, // read-timeout via WaitAsync
            IOException io => !IsUnrecoverableFilesystemError(io),
            ObjectDisposedException => false, // teardown: stop, let the gate decide
            _ => false,
        };

        internal static TimeSpan? GetRetryAfterDelay(HttpResponseMessage resp)
        {
            try
            {
                var ra = resp.Headers.RetryAfter;
                if (ra == null) return null;
                if (ra.Delta is { } d && d > TimeSpan.Zero) return d;
                if (ra.Date is { } date)
                {
                    var delta = date - DateTimeOffset.UtcNow;
                    return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
                }
            }
            catch { }
            return null;
        }

        // Per-run fault propagation moved to DownloadSession (Ticket #004).

        internal void SetResolvedFileName(string name) => _currentResolvedFileName = name;

        /// <summary>Last pause-phase latencies in ms (sem, ack, checkpoint). Test/diagnostics only.</summary>
        internal (long SemMs, long AckMs, long CheckpointMs) GetPauseTelemetry()
        {
            lock (_sessionLock)
            {
                var s = _session;
                if (s == null) return (-1, -1, -1);
                return (s.PauseSemMs, s.PauseAckMs, s.PauseCheckpointMs);
            }
        }
    }
}
