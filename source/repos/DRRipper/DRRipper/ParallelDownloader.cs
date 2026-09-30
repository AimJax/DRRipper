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
        private readonly PauseToken _pauseToken = new();
        private CancellationTokenSource? _internalCts;
        private DownloadState _state = DownloadState.Idle;
        private readonly object _stateLock = new();
        private string? _currentMetaPath;
        private string? _currentUrl;
        private string? _currentResolvedFileName;
        private long _currentTotalSize;
        private ConcurrentQueue<(long start, long end)>? _currentChunks;
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
        // track active in-flight chunks so Pause() can recover them
        private readonly ConcurrentDictionary<long, long> _activeChunks = new();
        // network availability handler reference for proper unsubscribe
        private System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler? _networkAvailabilityHandler;

        /// <summary>Bounded retry policy applied to transient failures (defaults: 1s/30s/5 attempts).</summary>
        public DownloadRetryPolicy RetryPolicy { get; set; } = DownloadRetryPolicy.Default;

        // Controlled fault propagation for the current StartAsync run.
        private readonly object _faultLock = new();
        private Exception? _sessionFault;
        private bool _fallbackSingleStream;

        // Win32 power management to prevent system sleep during downloads
        private const uint ES_CONTINUOUS = 0x80000000u;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001u;
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        // Per-request cancellation sources tracked by chunk start so Pause can cancel all in-flight requests
        private readonly object _requestCtsLock = new object();
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _activeRequestCts = new();

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
        private static void EnsureEnoughDiskSpace(string targetDirectory, long requiredBytes)
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
            // Listen for network availability changes to trigger retry/resume
            _networkAvailabilityHandler = (s, e) =>
            {
                if (e.IsAvailable)
                {
                    if (_state == DownloadState.Retrying)
                        Resume();
                }
                else
                {
                    if (_state == DownloadState.Downloading)
                        SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                }
            };
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += _networkAvailabilityHandler;
        }

        private void SetState(DownloadState s, string? customMessage = null)
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
            _pauseToken.Pause();
            SetState(DownloadState.Paused);
            // actively cancel any in-flight HTTP requests so hung sockets are aborted
            try
            {
                lock (_requestCtsLock)
                {
                    // Cancel all active per-request tokens
                    foreach (var kv in _activeRequestCts)
                    {
                        try
                        {
                            var cts = kv.Value;
                            if (cts != null && !cts.IsCancellationRequested)
                            {
                                try { cts.Cancel(); } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            // persist remaining chunks metadata if available
            try
            {
                if (!string.IsNullOrWhiteSpace(_currentMetaPath) && !string.IsNullOrWhiteSpace(_currentUrl) && _currentChunks != null)
                {
                    // Re-queue any active in-flight chunks so they are not lost when pausing
                    try
                    {
                        foreach (var kv in _activeChunks)
                        {
                            _currentChunks.Enqueue((kv.Key, kv.Value));
                        }
                    }
                    catch { }

                    SaveMetadata(_currentMetaPath, _currentUrl, _currentResolvedFileName ?? string.Empty, _currentTotalSize, _currentChunks);
                    // Clear active chunk tracking to avoid duplicates if Pause is called multiple times
                    try { _activeChunks.Clear(); } catch { }
                }
            }
            catch { }
        }

        public void Resume()
        {
            _pauseToken.Resume();
            SetState(DownloadState.Downloading);
            // No explicit per-request CTS disposal here; workers will create fresh per-request tokens as needed on resume
            // do not recreate internalCts here; resume is cooperative
            // Reset speed window so instantaneous speed isn't diluted by paused time
            ResetSpeedWindow();
        }

        public void Cancel()
        {
            _pauseToken.Resume();
            _internalCts?.Cancel();
            // remove metadata
            try { if (!string.IsNullOrWhiteSpace(_currentMetaPath) && File.Exists(_currentMetaPath)) File.Delete(_currentMetaPath); } catch { }
            SetState(DownloadState.Cancelled);
        }

        public void Dispose()
        {
            // Signal workers first so teardown never races in-flight requests:
            // cancellation is observed before the client/handler go away.
            try { _internalCts?.Cancel(); } catch { }
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

        private void ResetSpeedWindow()
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



        private void SaveMetadata(string metaPath, string url, string fileName, long totalSize, ConcurrentQueue<(long start, long end)> chunks)
        {
            try
            {
                var arr = chunks.ToArray();
                var meta = new DownloadMetadata
                {
                    Url = url,
                    FileName = fileName,
                    TotalSize = totalSize,
                    ChunkOffsets = new List<long>(),
                    ChunkLengths = new List<long>(),
                    ChunkDownloaded = new List<long>()
                };
                foreach (var (s, e) in arr)
                {
                    meta.ChunkOffsets.Add(s);
                    meta.ChunkLengths.Add(e - s + 1);
                    meta.ChunkDownloaded.Add(0);
                }

                File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
            }
            catch { }
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
            // Prepare output path and ensure directory exists
            Directory.CreateDirectory(targetDirectory);
            var safeName = SanitizeFileName(resolvedFileName ?? "download.bin");
            var outputPath = Path.Combine(targetDirectory, safeName);

            if (totalSize <= 0)
            {
                // Unknown size: fall back to single-stream download (ensure file created/truncated)
                using (var fs0 = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                await SingleStreamDownload(url, outputPath, cancellationToken);
                return outputPath;
            }

            // Pre-allocate file to the known size (ensure disk space)
            EnsureEnoughDiskSpace(targetDirectory, totalSize);
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                fs.SetLength(totalSize);
            }

            // set initial state and status
            SetState(DownloadState.Downloading, "Starting");

            if (!acceptRanges || connections == 1)
            {
                await SingleStreamDownload(url, outputPath, cancellationToken);
                SetState(DownloadState.Completed);
                _currentResolvedFileName = resolvedFileName;
                return outputPath;
            }

            // Build dynamic chunk queue for work-stealing
            var chunks = new ConcurrentQueue<(long start, long end)>();
            for (long offset = 0; offset < totalSize; offset += CHUNK_SIZE)
            {
                long end = Math.Min(offset + CHUNK_SIZE - 1, totalSize - 1);
                chunks.Enqueue((offset, end));
            }

            // metadata file for resume
            var metaPath = outputPath + ".drmeta";
            DownloadMetadata? meta = null;
            if (File.Exists(metaPath))
            {
                try { meta = JsonSerializer.Deserialize<DownloadMetadata>(File.ReadAllText(metaPath)); } catch { meta = null; }
            }

            // If metadata exists, prefer enqueuing remaining chunks from metadata
            if (meta != null && meta.ChunkOffsets != null && meta.ChunkOffsets.Count > 0)
            {
                // rebuild queue from metadata remaining offsets
                var q = new ConcurrentQueue<(long start, long end)>();
                for (int i = 0; i < meta.ChunkOffsets.Count; i++)
                {
                    var s = meta.ChunkOffsets[i];
                    var len = meta.ChunkLengths != null && i < meta.ChunkLengths.Count ? meta.ChunkLengths[i] : CHUNK_SIZE;
                    var e = s + len - 1;
                    q.Enqueue((s, e));
                }
                chunks = q;
            }

            // Open shared FileStream to hold SafeFileHandle alive
            _currentMetaPath = metaPath;
            _currentUrl = url;
            _currentResolvedFileName = resolvedFileName;
            _currentTotalSize = totalSize;
            _currentChunks = chunks;
            SetState(DownloadState.Downloading);
            // Initialize reporting baseline and start periodic reporter
            Interlocked.Exchange(ref _lastReportBytes, Interlocked.Read(ref _totalDownloaded));
            lock (_timerLock)
            {
                _lastReportTime = DateTime.UtcNow;
                _smoothedSpeed = 0.0;
                _reportTimer = new System.Threading.Timer(ReportTimerTick, null, 250, 250);
            }
            using (var destStream = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
            {
                var handle = destStream.SafeFileHandle;
                // initialize global counters for this download
                Interlocked.Exchange(ref _totalDownloaded, 0);
                Interlocked.Exchange(ref _reportBytes, 0);
                lock (_reportLock) { _reportTime = DateTime.MinValue; }
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // internal CTS for controlling workers (dispose the previous run's source: F-09)
                try { _internalCts?.Dispose(); } catch { }
                _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var workerCts = _internalCts;
                lock (_faultLock) { _sessionFault = null; }
                _fallbackSingleStream = false;

                var tasks = new List<Task>();
                // Exact-coverage bookkeeping for the completion gate (F-12).
                var tracker = new RangeTracker(totalSize);
                try
                {
                for (int i = 0; i < connections; i++)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        while (!workerCts.Token.IsCancellationRequested)
                        {
                            // respect pause
                            _pauseToken.WaitIfPaused(workerCts.Token);

                            if (!chunks.TryDequeue(out var chunk))
                            {
                                // no work, exit
                                break;
                            }

                            try
                            {
                                await DownloadChunkStrictAsync(
                                    url, chunk, handle, totalSize,
                                    sessionETag, sessionLastModified, tracker, workerCts.Token);
                            }
                            catch (OperationCanceledException) { break; }
                            catch (RangeNotSupportedException)
                            {
                                // Server answered 200 to a range request: stop all
                                // workers and restart the whole file as single-stream.
                                _fallbackSingleStream = true;
                                try { _internalCts?.Cancel(); } catch { }
                                break;
                            }
                            catch (Exception ex)
                            {
                                // Fault propagation (F-02): preserve the reason,
                                // stop siblings, never swallow, never continue.
                                RecordFault(ex is DownloadFailedException dfe
                                    ? dfe
                                    : new DownloadFailedException($"Segment [{chunk.start}-{chunk.end}] failed: {ex.Message}", ex));
                                break;
                            }
                        }
                    }, workerCts.Token));
                }

                    // wait for all workers
                    await Task.WhenAll(tasks);

                    // 200-to-range fallback: truncate any partial segments and stream whole file.
                    // The session CTS was cancelled to stop workers; issue a fresh one
                    // linked to the caller's token for the single-stream phase.
                    if (_fallbackSingleStream && TakeFault() == null && !cancellationToken.IsCancellationRequested)
                    {
                        using (var trunc = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { }
                        Interlocked.Exchange(ref _totalDownloaded, 0);
                        try { _internalCts?.Dispose(); } catch { }
                        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        await SingleStreamDownload(url, outputPath, cancellationToken);
                        SetState(DownloadState.Completed);
                        _currentResolvedFileName = resolvedFileName;
                        try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
                        return outputPath;
                    }

                    // Explicit completion gate (F-12): Completed requires ALL of:
                    // no fault, no cancellation, exact range coverage, exact byte
                    // accounting, and exact on-disk length. No file reread needed.
                    if (workerCts.Token.IsCancellationRequested && TakeFault() == null)
                    {
                        // Cancelled (F-13): never report Completed, never return a path.
                        SetState(DownloadState.Cancelled);
                        try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
                        throw new OperationCanceledException(workerCts.Token);
                    }

                    var fault = TakeFault();
                    if (fault != null)
                    {
                        SetState(DownloadState.Failed, fault.Message);
                        try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
                        ExceptionDispatchInfo.Capture(fault).Throw();
                    }

                    long accounted = Interlocked.Read(ref _totalDownloaded);
                    long onDisk = -1;
                    Exception? gateError = null;
                    try { onDisk = new FileInfo(outputPath).Length; } catch (Exception ex) { gateError = ex; }

                    if (!tracker.IsComplete || accounted != totalSize || onDisk != totalSize)
                    {
                        var msg = $"Completion gate rejected download: range coverage {tracker.CoveredBytes}/{totalSize} bytes, " +
                            $"accounted {accounted}/{totalSize} bytes, on-disk {onDisk}/{totalSize} bytes." +
                            (gateError != null ? $" File check error: {gateError.Message}" : string.Empty);
                        var gateFault = new DownloadFailedException(msg);
                        SetState(DownloadState.Failed, msg);
                        try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
                        throw gateFault;
                    }

                    // All gate conditions satisfied: enter Completed and report.
                    SetState(DownloadState.Completed);
                    _currentResolvedFileName = resolvedFileName;
                    try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
                }
                finally
                {
                    // always stop and dispose timer used for reporting
                    lock (_timerLock)
                    {
                        try { _reportTimer?.Dispose(); } catch { }
                        _reportTimer = null;
                    }
                }
            }

            return outputPath;
        }

        /// <summary>
        /// Strict per-chunk transfer (F-01): identity encoding, 206 + Content-Range
        /// validation against the REQUESTED span, exact byte completion, bounded
        /// remainder-resume. Throws on cancellation (OCE), RangeNotSupportedException
        /// for 200-to-range (fallback), DownloadFailedException for fatal faults.
        /// Transient stalls resume the unwritten remainder; attempts without forward
        /// progress are bounded by RetryPolicy (final reason recorded).
        /// </summary>
        private async Task DownloadChunkStrictAsync(
            string url, (long start, long end) chunk, SafeFileHandle handle,
            long totalSize, string? sessionETag, DateTimeOffset? sessionLastModified,
            RangeTracker tracker, CancellationToken sessionCt)
        {
            long origStart = chunk.start, origEnd = chunk.end;
            var policy = RetryPolicy ?? DownloadRetryPolicy.Default;
            int consecutiveFailures = 0;
            try { _activeChunks[origStart] = origEnd; } catch { }

            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                while (true)
                {
                    sessionCt.ThrowIfCancellationRequested();
                    _pauseToken.WaitIfPaused(sessionCt);

                    long reqStart = chunk.start, reqEnd = chunk.end;
                    long expected = reqEnd - reqStart + 1;
                    long writtenThisAttempt = 0;
                    TimeSpan? attemptRetryAfter = null;
                    CancellationTokenSource? perReq = null;
                    try
                    {
                        perReq = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
                        // AddOrUpdate (not TryAdd): retries reuse keys and Pause() must
                        // always reach the live request (refines P-01).
                        try { _activeRequestCts.AddOrUpdate(reqStart, perReq, (_, _) => perReq); } catch { }

                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Range = new RangeHeaderValue(reqStart, reqEnd);
                        // Identity transfer encoding for ranges: byte offsets must keep
                        // their meaning regardless of server compression support (F-08).
                        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token);

                        int code = (int)resp.StatusCode;
                        if (resp.StatusCode == HttpStatusCode.OK)
                            throw new RangeNotSupportedException($"Server answered 200 to range [{reqStart}-{reqEnd}]; ranges unsupported.");
                        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                            throw new NonRetryableHttpException($"Server rejected range [{reqStart}-{reqEnd}] with 416.");
                        if (IsRetryableStatus(code))
                        {
                            attemptRetryAfter = GetRetryAfterDelay(resp);
                            throw new HttpRequestException($"HTTP {code} ({resp.StatusCode}) for range [{reqStart}-{reqEnd}].");
                        }
                        if (code >= 400 && code < 500)
                            throw new NonRetryableHttpException($"HTTP {code} ({resp.StatusCode}) for range [{reqStart}-{reqEnd}].");

                        ValidateRangeResponse(resp, reqStart, reqEnd, totalSize, sessionETag, sessionLastModified);

                        try { if (GetState() == DownloadState.Retrying) SetState(DownloadState.Downloading); } catch { }

                        using var stream = await resp.Content.ReadAsStreamAsync(perReq.Token);
                        long written = 0;
                        while (true)
                        {
                            sessionCt.ThrowIfCancellationRequested();
                            _pauseToken.WaitIfPaused(sessionCt);
                            int read;
                            try
                            {
                                // Single WaitAsync timeout instead of a linked CTS per
                                // read: TimeoutException = stall, OCE = pause/cancel.
                                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), perReq.Token).AsTask()
                                    .WaitAsync(TimeSpan.FromSeconds(30), perReq.Token);
                            }
                            catch (TimeoutException ex)
                            {
                                throw new IncompleteChunkException($"Range [{reqStart}-{reqEnd}]: read stalled after {written} of {expected} bytes ({ex.Message}).");
                            }

                            if (read <= 0)
                            {
                                // Strict completion (F-01): clean EOF counts only when
                                // every expected byte arrived; otherwise resume remainder.
                                if (written >= expected) break;
                                throw new IncompleteChunkException($"Range [{reqStart}-{reqEnd}]: clean EOF after {written} of {expected} bytes.");
                            }

                            long remaining = expected - written;
                            int bytesToWrite = remaining > 0 ? (int)Math.Min(read, remaining) : read;
                            await RandomAccess.WriteAsync(handle, new ReadOnlyMemory<byte>(buffer, 0, bytesToWrite), reqStart + written, sessionCt);
                            written += bytesToWrite;
                            Interlocked.Add(ref _totalDownloaded, bytesToWrite);
                            writtenThisAttempt += bytesToWrite;
                            if (written >= expected) break;
                        }

                        // Full remainder received and written: exact coverage, no duplicates.
                        tracker.CompleteRange(origStart, origEnd);
                        try { _activeChunks.TryRemove(origStart, out _); } catch { }
                        try { _activeChunks.TryRemove(reqStart, out _); } catch { }
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        if (sessionCt.IsCancellationRequested) throw;
                        if (_pauseToken.IsPaused) break; // Pause(): outer loop waits; Pause() requeues remainder
                        // Per-request abort with no pause/cancel signal: transient stall.
                        throw new IOException("In-flight range request aborted without pause or cancellation.");
                    }
                    catch (Exception ex) when (IsTransientNetworkError(ex))
                    {
                        if (writtenThisAttempt > 0)
                        {
                            // Forward progress: advance to the unwritten remainder and
                            // resume immediately; the no-progress streak resets.
                            long newStart = chunk.start + writtenThisAttempt;
                            try { _activeChunks.TryRemove(reqStart, out _); } catch { }
                            try { _activeChunks.TryRemove(chunk.start, out _); } catch { }
                            chunk.start = newStart;
                            try { _activeChunks[newStart] = origEnd; } catch { }
                            consecutiveFailures = 0;
                            SetState(DownloadState.Retrying, "Resuming range...");
                            continue;
                        }
                        consecutiveFailures++;
                        if (consecutiveFailures >= policy.MaxAttempts)
                        {
                            throw new DownloadFailedException(
                                $"Range [{origStart}-{origEnd}] failed after {consecutiveFailures} attempts without progress. " +
                                $"Last error: {ex.GetType().Name}: {ex.Message}", ex);
                        }
                        SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                        var delay = policy.ComputeDelay(consecutiveFailures, attemptRetryAfter);
                        await Task.Delay(delay, sessionCt); // cancellable: no ContinueWith suppression
                        continue;
                    }
                    finally
                    {
                        try { _activeRequestCts.TryRemove(reqStart, out _); } catch { }
                        try { perReq?.Dispose(); } catch { }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Strict HTTP range-response validation (§4). Rejects before any body byte
        /// is trusted: 206 status, well-formed Content-Range matching the REQUESTED
        /// span and session total, and stable resource identity. Never trusts
        /// Content-Length for offsets. Throws NonRetryableHttpException (fatal).
        /// </summary>
        private static void ValidateRangeResponse(
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

        private async Task SingleStreamDownload(string url, string outputPath, CancellationToken cancellationToken)
        {
            var progress = new DownloadProgress { StatusMessage = "Downloading" };
            // reset global counters for this single-stream download and start reporter
            Interlocked.Exchange(ref _totalDownloaded, 0);
            Interlocked.Exchange(ref _reportBytes, 0);
            lock (_reportLock) { _reportTime = DateTime.MinValue; }
            SetState(DownloadState.Downloading, "Downloading");
            _currentResolvedFileName = Path.GetFileName(outputPath);
            lock (_timerLock)
            {
                _lastReportBytes = Interlocked.Read(ref _totalDownloaded);
                _lastReportTime = DateTime.UtcNow;
                _smoothedSpeed = 0.0;
                _reportTimer = new System.Threading.Timer(ReportTimerTick, null, 250, 250);
            }

            // Unify cancellation: observe the external token AND application Cancel().
            using var sessionCts = _internalCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _internalCts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sessionCt = sessionCts.Token;
            var policy = RetryPolicy ?? DownloadRetryPolicy.Default;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            try
            {
                using (var fs = new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous))
                {
                    var handle = fs.SafeFileHandle;
                    long writeOffset = 0;
                    long? expectedTotal = null;
                    int consecutiveFailures = 0;

                    while (true)
                    {
                        sessionCt.ThrowIfCancellationRequested();
                        _pauseToken.WaitIfPaused(sessionCt);
                        long writtenThisAttempt = 0;
                        TimeSpan? attemptRetryAfter = null;

                        CancellationTokenSource? perReq = null;
                        try
                        {
                            perReq = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
                            try { _activeRequestCts.AddOrUpdate(-1, perReq, (_, _) => perReq); } catch { }

                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            if (writeOffset > 0)
                                req.Headers.Range = new RangeHeaderValue(writeOffset, null);

                            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token);
                            int sc = (int)resp.StatusCode;

                            if (writeOffset > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                                throw new NonRetryableHttpException(
                                    $"Server does not support resume at offset {writeOffset} (got {(int)resp.StatusCode} {resp.StatusCode}); cannot continue without retransmission.");
                            if (IsRetryableStatus(sc))
                            {
                                attemptRetryAfter = GetRetryAfterDelay(resp);
                                throw new HttpRequestException($"HTTP {sc} ({resp.StatusCode}) for single-stream download.");
                            }
                            if (sc >= 400 && sc < 500)
                                throw new NonRetryableHttpException($"HTTP {sc} ({resp.StatusCode}) for single-stream download.");
                            resp.EnsureSuccessStatusCode();

                            // Adopt the total once; any later disagreement is a fatal identity change.
                            long? reported = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength;
                            if (writeOffset == 0 && reported.HasValue)
                                expectedTotal = reported.Value;
                            else if (reported.HasValue && expectedTotal.HasValue && reported.Value != expectedTotal.Value)
                                throw new NonRetryableHttpException(
                                    $"Server total changed mid-download ({expectedTotal} -> {reported}); refusing to mix versions.");
                            else if (reported.HasValue)
                                expectedTotal ??= reported.Value;

                            if (writeOffset > 0)
                            {
                                var rcr = resp.Content.Headers.ContentRange;
                                if (rcr != null && rcr.HasRange && rcr.From != writeOffset)
                                    throw new NonRetryableHttpException(
                                        $"Resume range mismatch: requested from {writeOffset}, server sent from {rcr.From}.");
                            }

                            try { if (GetState() == DownloadState.Retrying) SetState(DownloadState.Downloading); } catch { }
                            progress.TotalBytes = expectedTotal ?? -1;
                            try { _currentTotalSize = expectedTotal ?? -1; } catch { }

                            using var stream = await resp.Content.ReadAsStreamAsync(perReq.Token);
                            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                            try
                            {
                                while (true)
                                {
                                    sessionCt.ThrowIfCancellationRequested();
                                    _pauseToken.WaitIfPaused(sessionCt);
                                    int read;
                                    try
                                    {
                                        read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), perReq.Token).AsTask()
                                            .WaitAsync(TimeSpan.FromSeconds(45), perReq.Token);
                                    }
                                    catch (TimeoutException ex)
                                    {
                                        throw new IncompleteChunkException($"Single-stream read stalled at offset {writeOffset} ({ex.Message}).");
                                    }
                                    if (read <= 0) break;

                                    await System.IO.RandomAccess.WriteAsync(handle, new ReadOnlyMemory<byte>(buffer, 0, read), writeOffset, sessionCt);
                                    writeOffset += read;
                                    writtenThisAttempt += read;
                                    Interlocked.Add(ref _totalDownloaded, read);
                                }
                            }
                            finally { ArrayPool<byte>.Shared.Return(buffer); }

                            // Stream ended. With a known total, a shortfall is an
                            // incomplete transfer -> resume remainder (never false success).
                            if (expectedTotal.HasValue && writeOffset < expectedTotal.Value)
                            {
                                consecutiveFailures = 0; // progress was made; resume immediately
                                SetState(DownloadState.Retrying, "Resuming stream...");
                                continue;
                            }

                            SetState(DownloadState.Completed);
                            _currentResolvedFileName = Path.GetFileName(outputPath);
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            // External Cancel(), linked internal cancel, or Dispose:
                            // propagate so callers observe cancellation, never Completed.
                            if (sessionCt.IsCancellationRequested) throw;
                            if (_pauseToken.IsPaused) { _pauseToken.WaitIfPaused(sessionCt); continue; }
                            throw new IOException("In-flight single-stream request aborted without pause or cancellation.");
                        }
                        catch (Exception ex) when (IsTransientNetworkError(ex))
                        {
                            if (writtenThisAttempt > 0)
                            {
                                consecutiveFailures = 0;
                                SetState(DownloadState.Retrying, "Resuming stream...");
                                continue;
                            }
                            consecutiveFailures++;
                            if (consecutiveFailures >= policy.MaxAttempts)
                            {
                                var done = new DownloadFailedException(
                                    $"Single-stream download failed after {consecutiveFailures} attempts without progress at offset {writeOffset}. " +
                                    $"Last error: {ex.GetType().Name}: {ex.Message}", ex);
                                SetState(DownloadState.Failed, done.Message);
                                throw done;
                            }
                            SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                            await Task.Delay(policy.ComputeDelay(consecutiveFailures, attemptRetryAfter), sessionCt);
                            continue;
                        }
                        finally { try { _activeRequestCts.TryRemove(-1, out _); } catch { } try { perReq?.Dispose(); } catch { } }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Terminal state mapping for external/linking cancellation (F-13):
                // never leave a stale Downloading behind; never report Completed.
                SetState(DownloadState.Cancelled);
                throw;
            }
            catch (DownloadFailedException ex)
            {
                SetState(DownloadState.Failed, ex.Message);
                throw;
            }
            finally
            {
                // stop and dispose timer used for single-stream
                lock (_timerLock)
                {
                    try { _reportTimer?.Dispose(); } catch { }
                    _reportTimer = null;
                }
            }
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

        // Exception used to indicate a non-retryable 4xx HTTP error
        private class NonRetryableHttpException : DownloadFailedException
        {
            public NonRetryableHttpException(string message) : base(message) { }
        }

        /// <summary>Signals that a range request was answered 200: restart as single-stream.</summary>
        private sealed class RangeNotSupportedException : Exception
        {
            public RangeNotSupportedException(string message) : base(message) { }
        }

        /// <summary>Signals a short clean-EOF chunk that must resume its remainder (transient).</summary>
        private sealed class IncompleteChunkException : IOException
        {
            public IncompleteChunkException(string message) : base(message) { }
        }

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

        private static bool IsTransientNetworkError(Exception ex) => ex switch
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

        private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage resp)
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

        private void RecordFault(Exception ex)
        {
            lock (_faultLock) { _sessionFault ??= ex; }
            try { _internalCts?.Cancel(); } catch { }
        }

        private Exception? TakeFault()
        {
            lock (_faultLock) return _sessionFault;
        }
    }
}
