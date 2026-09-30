using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text.Json;

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

        // Win32 power management to prevent system sleep during downloads
        private const uint ES_CONTINUOUS = 0x80000000u;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001u;
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        // Per-request cancellation source used to cancel in-flight HTTP operations (for Pause)
        private readonly object _requestCtsLock = new object();
        private CancellationTokenSource? _currentRequestCts;

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
                    throw new SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
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
                    if (_currentRequestCts != null && !_currentRequestCts.IsCancellationRequested)
                    {
                        try { _currentRequestCts.Cancel(); } catch { }
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
            // reinitialize per-request CTS so subsequent requests start fresh
            try
            {
                lock (_requestCtsLock)
                {
                    try { _currentRequestCts?.Dispose(); } catch { }
                    _currentRequestCts = null;
                }
            }
            catch { }
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
                    // EMA smoothing
                    _smoothedSpeed = (_smoothedSpeed * 0.7) + (rawSpeed * 0.3);
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

                            // Resolve filename from this response (final URI after redirects)
                            resolvedFileName = ResolveFileNameFromResponse(resp, suggestedFileName);
                        }
                    }
                }
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

                // internal CTS for controlling workers
                _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var workerCts = _internalCts;

                var tasks = new List<Task>();
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
                            // mark active chunk
                            _activeChunks[chunk.start] = chunk.end;

                            try
                            {
                                var attempt = 0;
                                var backoff = 1000;
                                long writtenThisAttempt = 0;
                                while (!workerCts.Token.IsCancellationRequested)
                                {
                                    // reset attempt-local counter before each new request
                                    writtenThisAttempt = 0;
                                    // remember original chunk start for active tracking
                                    var originalStart = chunk.start;
                                    CancellationTokenSource? perReq = null;
                                    try
                                    {
                                        perReq = CancellationTokenSource.CreateLinkedTokenSource(workerCts.Token);
                                        lock (_requestCtsLock) { _currentRequestCts = perReq; }

                                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                                        req.Headers.Range = new RangeHeaderValue(chunk.start, chunk.end);
                                        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token);

                                        // Fail fast on client errors (4xx) - do not retry these
                                        var code = (int)resp.StatusCode;
                                        if (code >= 400 && code < 500)
                                        {
                                            var msg = $"HTTP {code} ({resp.StatusCode})";
                                            try { SetState(DownloadState.Failed, msg); } catch { }
                                            throw new NonRetryableHttpException(msg);
                                        }

                                        // Ensure server honored the range request
                                        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                                        {
                                            throw new IOException($"Server did not return Partial Content for range {chunk.start}-{chunk.end}: {resp.StatusCode}");
                                        }

                                        // If we were retrying due to network loss, restore active state now that we have a successful response
                                        try { if (GetState() == DownloadState.Retrying) SetState(DownloadState.Downloading); } catch { }

                                        using var stream = await resp.Content.ReadAsStreamAsync(perReq.Token);

                                        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                                        try
                                        {
                                            long written = 0;
                                            while (true)
                                            {
                                                workerCts.Token.ThrowIfCancellationRequested();
                                                _pauseToken.WaitIfPaused(workerCts.Token);
                                                int read;
                                                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(perReq.Token))
                                                {
                                                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                                                    try
                                                    {
                                                        read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                                                    }
                                                    catch (OperationCanceledException)
                                                    {
                                                        // If the overall worker cancellation was requested, propagate to terminate the worker.
                                                        if (workerCts.Token.IsCancellationRequested) throw;

                                                        // If the per-request CTS was triggered (e.g., Pause() or explicit per-request cancel), and
                                                        // the worker is still active, rethrow so outer logic can handle pause/cleanup.
                                                        if (perReq != null && perReq.IsCancellationRequested && !workerCts.Token.IsCancellationRequested)
                                                        {
                                                            throw;
                                                        }

                                                        // Otherwise this was a read timeout from the socket read (readCts.CancelAfter). Convert to
                                                        // IOException so the retry logic treats it as a transient network error.
                                                        throw new IOException("Socket read timed out after 5 seconds.");
                                                    }
                                                }
                                                if (read <= 0) break;

                                                long writeOffset = chunk.start + written;
                                                await RandomAccess.WriteAsync(handle, new ReadOnlyMemory<byte>(buffer, 0, read), writeOffset, workerCts.Token);
                                                written += read;
                                                Interlocked.Add(ref _totalDownloaded, read);
                                                writtenThisAttempt += read;
                                            }
                                        }
                                        finally
                                        {
                                            ArrayPool<byte>.Shared.Return(buffer);
                                        }

                                        // on success remove active chunk marker
                                        try { _activeChunks.TryRemove(originalStart, out _); } catch { }

                                        // success, break retry loop
                                        writtenThisAttempt = 0;
                                        break;
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        if (workerCts.Token.IsCancellationRequested) throw;
                                        // cancelled due to Pause(); break to let outer loop wait on pause
                                        break;
                                    }
                                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                                    {
                                        // If we wrote some bytes before the failure, resume from that offset rather than re-downloading the whole chunk
                                        try
                                        {
                                            if (writtenThisAttempt > 0)
                                            {
                                                var oldStart = chunk.start;
                                                var newStart = chunk.start + writtenThisAttempt;
                                                chunk.start = newStart;
                                                try { _activeChunks.TryRemove(oldStart, out _); } catch { }
                                                try { _activeChunks[chunk.start] = chunk.end; } catch { }
                                            }
                                        }
                                        catch { }
                                        writtenThisAttempt = 0;

                                        SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                                        attempt++;
                                        await Task.Delay(backoff, workerCts.Token).ContinueWith(_ => { });
                                        backoff = Math.Min(backoff * 2, 2000);
                                        continue; // retry same chunk
                                    }
                                    finally
                                    {
                                        lock (_requestCtsLock) { if (_currentRequestCts == perReq) _currentRequestCts = null; }
                                        try { perReq?.Dispose(); } catch { }
                                    }
                                }
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex)
                            {
                                SetState(DownloadState.Failed, "Segment error: " + ex.Message);
                            }
                        }
                    }, workerCts.Token));
                }

                    // wait for all workers
                    await Task.WhenAll(tasks);

                    // Final report will be performed by timer - set status and force a final tick
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

            // Resilient single-stream with resume on transient failures
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            try
            {
                using (var fs = new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous))
                {
                    var handle = fs.SafeFileHandle;
                    long writeOffset = 0;
                    int attempt = 0;
                    int backoff = 1000;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        long writtenThisAttempt = 0;
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            if (writeOffset > 0)
                                req.Headers.Range = new RangeHeaderValue(writeOffset, null);

                        // create a per-request CTS so Pause() can cancel hung requests
                        CancellationTokenSource? perReq = null;
                        try
                        {
                            perReq = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            lock (_requestCtsLock) { _currentRequestCts = perReq; }
                            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token);

                            // If resuming, require PartialContent
                            if (writeOffset > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                                throw new IOException($"Server did not honor range resume at {writeOffset}: {resp.StatusCode}");

                            // Fail fast on client errors (4xx)
                            var sc = (int)resp.StatusCode;
                            if (sc >= 400 && sc < 500)
                            {
                                var msg = $"HTTP {sc} ({resp.StatusCode})";
                                try { SetState(DownloadState.Failed, msg); } catch { }
                                throw new NonRetryableHttpException(msg);
                            }

                            resp.EnsureSuccessStatusCode();
                            // If we were retrying due to network loss, restore active state now that we have a successful response
                            try { if (GetState() == DownloadState.Retrying) SetState(DownloadState.Downloading); } catch { }
                            var total = resp.Content.Headers.ContentLength ?? -1;
                            progress.TotalBytes = total;
                            // Ensure reporter knows the total size for single-stream downloads
                            try { _currentTotalSize = total; } catch { }

                            using var stream = await resp.Content.ReadAsStreamAsync(perReq.Token);
                            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                            try
                            {
                                while (true)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    _pauseToken.WaitIfPaused(cancellationToken);

                                    int read;
                                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(perReq.Token))
                                    {
                                        readCts.CancelAfter(TimeSpan.FromSeconds(5));
                                        try
                                        {
                                            read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            if (cancellationToken.IsCancellationRequested) throw;
                                            throw new IOException("Read operation timed out due to a stalled socket.");
                                        }
                                    }

                                    if (read <= 0) break;

                                    await System.IO.RandomAccess.WriteAsync(handle, new ReadOnlyMemory<byte>(buffer, 0, read), writeOffset, cancellationToken);
                                    writeOffset += read;
                                    writtenThisAttempt += read;
                                    Interlocked.Add(ref _totalDownloaded, read);
                                }
                            }
                            finally { ArrayPool<byte>.Shared.Return(buffer); }

                            // finished successfully
                            SetState(DownloadState.Completed);
                            _currentResolvedFileName = Path.GetFileName(outputPath);
                            break;
                        }
                        finally { lock (_requestCtsLock) { if (_currentRequestCts == perReq) _currentRequestCts = null; } perReq?.Dispose(); }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                        {
                            // rollback any bytes counted during this attempt (single-stream keeps this behavior)
                            try { if (writtenThisAttempt > 0) Interlocked.Add(ref _totalDownloaded, -writtenThisAttempt); } catch { }
                            writtenThisAttempt = 0;

                            // signal retrying state and backoff
                            SetState(DownloadState.Retrying, "Network Lost - Reconnecting...");
                            attempt++;
                            await Task.Delay(backoff, cancellationToken).ContinueWith(_ => { });
                            backoff = Math.Min(backoff * 2, 2000);

                            // attempt to resume by looping again; if server doesn't support ranges this will fail
                            continue;
                        }
                    }
                }
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
        private class NonRetryableHttpException : Exception
        {
            public NonRetryableHttpException(string message) : base(message) { }
        }
    }
}
