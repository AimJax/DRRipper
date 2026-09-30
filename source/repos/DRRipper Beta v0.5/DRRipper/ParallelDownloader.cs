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

    public class ParallelDownloader
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

        public event Action<DownloadProgress>? ProgressChanged;
        public event Action<DownloadState>? StateChanged;
        private const int CHUNK_SIZE = 8 * 1024 * 1024; // 8MB chunks for dynamic stealing (reduce requests to avoid CDN tarpitting)

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
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (s, e) =>
            {
                if (e.IsAvailable)
                {
                    // network returned
                    if (_state == DownloadState.Retrying)
                        Resume();
                }
                else
                {
                    // network lost
                    if (_state == DownloadState.Downloading)
                        SetState(DownloadState.Retrying);
                }
            };
        }

        private void SetState(DownloadState s)
        {
            lock (_stateLock) { _state = s; }
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
            // persist remaining chunks metadata if available
            try
            {
                if (!string.IsNullOrWhiteSpace(_currentMetaPath) && !string.IsNullOrWhiteSpace(_currentUrl) && _currentChunks != null)
                {
                    SaveMetadata(_currentMetaPath, _currentUrl, _currentResolvedFileName ?? string.Empty, _currentTotalSize, _currentChunks);
                }
            }
            catch { }
        }

        public void Resume()
        {
            _pauseToken.Resume();
            SetState(DownloadState.Downloading);
            // do not recreate internalCts here; resume is cooperative
        }

        public void Cancel()
        {
            _pauseToken.Resume();
            _internalCts?.Cancel();
            // remove metadata
            try { if (!string.IsNullOrWhiteSpace(_currentMetaPath) && File.Exists(_currentMetaPath)) File.Delete(_currentMetaPath); } catch { }
            SetState(DownloadState.Cancelled);
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
            var outputPath = Path.Combine(targetDirectory, resolvedFileName!);

            if (totalSize <= 0)
            {
                // Unknown size: fall back to single-stream download (ensure file created/truncated)
                using (var fs0 = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                await SingleStreamDownload(url, outputPath, cancellationToken);
                return outputPath;
            }

            // Pre-allocate file to the known size
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                fs.SetLength(totalSize);
            }

            var progress = new DownloadProgress { TotalBytes = totalSize, StatusMessage = "Starting", ResolvedFileName = resolvedFileName };
            ProgressChanged?.Invoke(progress);

            if (!acceptRanges || connections == 1)
            {
                await SingleStreamDownload(url, outputPath, cancellationToken);
                progress.StatusMessage = "Completed";
                progress.ResolvedFileName = resolvedFileName;
                ProgressChanged?.Invoke(progress);
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
            using (var destStream = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
            {
                var handle = destStream.SafeFileHandle;

                long totalDownloaded = 0;
                long lastReportedBytes = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // internal CTS for controlling workers
                _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var workerCts = _internalCts;

                var tasks = new List<Task>();
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
                                var attempt = 0;
                                var backoff = 1000;
                                while (!workerCts.Token.IsCancellationRequested)
                                {
                                    try
                                    {
                                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                                        req.Headers.Range = new RangeHeaderValue(chunk.start, chunk.end);
                                        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, workerCts.Token);
                                        resp.EnsureSuccessStatusCode();
                                        using var stream = await resp.Content.ReadAsStreamAsync(workerCts.Token);

                                        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                                        try
                                        {
                                            long written = 0;
                                            while (true)
                                            {
                                                workerCts.Token.ThrowIfCancellationRequested();
                                                _pauseToken.WaitIfPaused(workerCts.Token);
                                            int read;
                                            // Apply a per-read timeout to detect stalled sockets
                                            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(workerCts.Token))
                                            {
                                                readCts.CancelAfter(TimeSpan.FromSeconds(15));
                                                try
                                                {
                                                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                                                }
                                                catch (OperationCanceledException)
                                                {
                                                    // If the worker cancellation requested, propagate; otherwise treat as a read timeout
                                                    if (workerCts.Token.IsCancellationRequested)
                                                        throw;
                                                    throw new IOException("Read operation timed out due to a stalled socket.");
                                                }
                                            }
                                            if (read <= 0) break;

                                                long writeOffset = chunk.start + written;
                                                await RandomAccess.WriteAsync(handle, new ReadOnlyMemory<byte>(buffer, 0, read), writeOffset, workerCts.Token);
                                                written += read;
                                                Interlocked.Add(ref totalDownloaded, read);

                                                if (Interlocked.Read(ref totalDownloaded) - Interlocked.Read(ref lastReportedBytes) >= 256 * 1024)
                                                {
                                                    var now = sw.Elapsed.TotalSeconds;
                                                    var bytes = Interlocked.Read(ref totalDownloaded);
                                                    var speed = bytes / 1024.0 / 1024.0 / Math.Max(1e-6, now);
                                                    lastReportedBytes = bytes;
                                                    var percent = totalSize > 0 ? (bytes * 100.0 / totalSize) : 0.0;
                                                    progress.TotalBytesDownloaded = bytes;
                                                    progress.CurrentSpeedMBps = speed;
                                                    progress.ProgressPercentage = percent;
                                                    progress.StatusMessage = "Downloading";
                                                    progress.ResolvedFileName = resolvedFileName;
                                                    ProgressChanged?.Invoke(progress);
                                                }
                                            }
                                        }
                                        finally { ArrayPool<byte>.Shared.Return(buffer); }

                                        // success, break retry loop
                                        break;
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is SocketException)
                                    {
                                        // network issue: go into retrying state
                                        SetState(DownloadState.Retrying);
                                        progress.StatusMessage = "Network Lost - Reconnecting...";
                                        ProgressChanged?.Invoke(progress);
                                        attempt++;
                                        await Task.Delay(backoff, workerCts.Token).ContinueWith(_ => { });
                                        backoff = Math.Min(backoff * 2, 30_000);
                                        continue; // retry same chunk
                                    }
                                }
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex)
                            {
                                progress.StatusMessage = "Segment error: " + ex.Message;
                                ProgressChanged?.Invoke(progress);
                            }
                        }
                    }, workerCts.Token));
                }

                // wait for all workers
                await Task.WhenAll(tasks);

                // Final report
                var finalBytes = Interlocked.Read(ref totalDownloaded);
                progress.TotalBytesDownloaded = finalBytes;
                progress.ProgressPercentage = totalSize > 0 ? (finalBytes * 100.0 / totalSize) : 100.0;
                progress.CurrentSpeedMBps = finalBytes / 1024.0 / 1024.0 / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                progress.StatusMessage = "Completed";
                progress.ResolvedFileName = resolvedFileName;
                ProgressChanged?.Invoke(progress);
                try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
            }

            return outputPath;
        }

        private async Task SingleStreamDownload(string url, string outputPath, CancellationToken cancellationToken)
        {
            var progress = new DownloadProgress { StatusMessage = "Downloading" };
            long totalDownloaded = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using (var resp = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                progress.TotalBytes = total;

                // Ensure file exists
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous))
                using (var stream = await resp.Content.ReadAsStreamAsync(cancellationToken))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    try
                    {
                        long writeOffset = 0;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                            if (read <= 0) break;
                            // write raw bytes at the current offset
                            await System.IO.RandomAccess.WriteAsync(fs.SafeFileHandle, new ReadOnlyMemory<byte>(buffer, 0, read), writeOffset, cancellationToken);
                            writeOffset += read;
                            totalDownloaded += read;

                            var speed = totalDownloaded / 1024.0 / 1024.0 / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                            progress.TotalBytesDownloaded = totalDownloaded;
                            progress.CurrentSpeedMBps = speed;
                            progress.ProgressPercentage = total > 0 ? (totalDownloaded * 100.0 / total) : 0;
                            ProgressChanged?.Invoke(progress);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }

            progress.StatusMessage = "Completed";
            ProgressChanged?.Invoke(progress);
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
    }
}
