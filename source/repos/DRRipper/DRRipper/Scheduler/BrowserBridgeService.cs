using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.IO;
using DRRipper.BrowserBridge;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Desktop named-pipe bridge (Ticket #007 §11/§13): owns the pipe server,
    /// accepts validated local clients, enqueues through
    /// <see cref="DownloadScheduler.EnqueueBrowserAsync"/>, and returns the
    /// JobId + acceptance status. UI-framework-independent; MainWindow only
    /// surfaces notifications. No engine internals are exposed.
    /// </summary>
    public sealed class BrowserBridgeService : IDisposable
    {
        /// <summary>Production pipe name (§11).</summary>
        public const string DefaultPipeName = BrowserBridgeProtocol.PipeName;

        /// <summary>Idempotency window for retried handoffs (§27).</summary>
        public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromMinutes(10);

        private readonly DownloadScheduler _scheduler;
        private readonly Func<string> _getDefaultDirectory;
        private readonly string _pipeName;
        private readonly ConcurrentIdempotencyCache _recent = new();
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _disposed;
        private int _acceptedCount;
        private int _rejectedCount;

        public BrowserBridgeService(
            DownloadScheduler scheduler,
            Func<string> getDefaultDirectory,
            string? pipeName = null)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _getDefaultDirectory = getDefaultDirectory ?? throw new ArgumentNullException(nameof(getDefaultDirectory));
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        }

        public string PipeName => _pipeName;
        public bool IsRunning => _loopTask != null && !_loopTask.IsCompleted;
        public int AcceptedCount => Volatile.Read(ref _acceptedCount);
        public int RejectedCount => Volatile.Read(ref _rejectedCount);

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(BrowserBridgeService));
            if (_loopTask != null && !_loopTask.IsCompleted)
                return;
            _loopCts = new CancellationTokenSource();
            var ct = _loopCts.Token;
            _loopTask = Task.Run(() => AcceptLoopAsync(ct), CancellationToken.None);
        }

        public async Task StopAsync()
        {
            try { _loopCts?.Cancel(); } catch { }
            var loop = _loopTask;
            if (loop != null)
            {
                try { await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }
            _loopTask = null;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = CreateServer();
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var owned = server;
                    server = null;
                    _ = Task.Run(() => HandleClientAsync(owned, ct), CancellationToken.None);
                }
                catch (OperationCanceledException) { }
                catch { await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); }
                finally
                {
                    try { server?.Dispose(); } catch { }
                }
            }
        }

        private NamedPipeServerStream CreateServer()
        {
            // Local-machine pipe: clients connect to "." only (never remote),
            // and both ends re-validate the versioned protocol (§28). The pipe
            // name is per-process-unique in tests and fixed in production; the
            // native host runs as the same interactive user.
            return new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serverCt)
        {
            using (pipe)
            {
                BrowserEnqueueResponse response;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    var request = await NativeMessageFraming.ReadAsync<BrowserEnqueueRequest>(
                        pipe, cts.Token).ConfigureAwait(false);
                    response = await HandleEnqueueAsync(request, cts.Token).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return; // ping-style probe disconnect; nothing to answer
                }
                catch (InvalidDataException ex)
                {
                    response = Fail(string.Empty,
                        ex.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
                            ? BrowserErrorCodes.MessageTooLarge : BrowserErrorCodes.MalformedMessage,
                        TrimError(ex.Message));
                }
                catch (JsonException)
                {
                    response = Fail(string.Empty, BrowserErrorCodes.MalformedMessage, "Malformed JSON payload.");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    response = Fail(string.Empty, BrowserErrorCodes.InternalError, TrimError(ex.Message));
                }
                try
                {
                    using var wcts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await NativeMessageFraming.WriteAsync(pipe, response, wcts.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(wcts.Token).ConfigureAwait(false);
                }
                catch { }
            }
        }

        /// <summary>
        /// Core enqueue path, also used directly by tests (T-BRIDGE-01..05/08).
        /// Never throws: every failure maps to a categorized response.
        /// </summary>
        public async Task<BrowserEnqueueResponse> HandleEnqueueAsync(
            BrowserEnqueueRequest? request, CancellationToken ct = default)
        {
            if (request == null)
                return Fail(string.Empty, BrowserErrorCodes.MalformedMessage, "Empty message.");
            var requestId = request.RequestId ?? string.Empty;
            if (!BrowserValidation.IsValidRequestId(requestId))
                return Fail(string.Empty, BrowserErrorCodes.MalformedMessage, "Missing requestId.");
            if (request.Version != BrowserBridgeProtocol.CurrentVersion)
                return Fail(requestId, BrowserErrorCodes.UnsupportedVersion,
                    $"Unsupported protocol version {request.Version}; bridge speaks {BrowserBridgeProtocol.CurrentVersion}.");
            if (!string.Equals(request.Type, BrowserBridgeProtocol.MessageTypeEnqueue, StringComparison.Ordinal))
            {
                if (string.Equals(request.Type, BrowserBridgeProtocol.MessageTypePing, StringComparison.Ordinal))
                    return new BrowserEnqueueResponse { RequestId = requestId, Accepted = true, Message = "pong" };
                return Fail(requestId, BrowserErrorCodes.MalformedMessage,
                    "Unexpected message type: " + (request.Type ?? "(null)") + ".");
            }

            // Idempotency: same requestId within the window replays the same
            // acceptance without a duplicate job (§27). Distinct ids for the
            // same URL always create independent jobs (T-BRIDGE-04).
            if (_recent.TryGet(requestId, out var cachedJobId))
            {
                return new BrowserEnqueueResponse
                {
                    RequestId = requestId,
                    Accepted = true,
                    JobId = cachedJobId.ToString(),
                    Duplicate = true,
                    Message = "Already accepted.",
                };
            }
            try
            {
                var existing = await _scheduler.GetJobByBrowserRequestIdAsync(requestId, ct).ConfigureAwait(false);
                if (existing != null)
                {
                    _recent.Remember(requestId, existing.JobId);
                    return new BrowserEnqueueResponse
                    {
                        RequestId = requestId,
                        Accepted = true,
                        JobId = existing.JobId.ToString(),
                        Duplicate = true,
                        Message = "Already accepted.",
                    };
                }
            }
            catch { }

            if (!BrowserValidation.TryNormalizeUrl(request.Url, out var url, out var urlError))
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, urlError, "URL rejected: " + urlError + ".");
            }
            if (!BrowserValidation.TryNormalizeSource(request.Source, out var source))
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, BrowserErrorCodes.MalformedMessage, "Unknown source label.");
            }
            string? referrerHost = null;
            if (!BrowserValidation.TryNormalizeReferrer(request.Referrer, out referrerHost, out var refError))
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, refError, "Referrer rejected.");
            }
            if (!BrowserValidation.TryFilterHeaders(request.Headers, out var allowedHeaders, out _, out var hdrError))
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, hdrError, "Headers rejected.");
            }
            if (!BrowserValidation.TryValidateCookies(request.Cookies, out var cookieCount, out var cookieError))
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, cookieError, "Cookies rejected.");
            }
            if (request.SuggestedFileName != null &&
                request.SuggestedFileName.Length > BrowserBridgeProtocol.MaxRawFileNameLength)
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, BrowserErrorCodes.InvalidFileName, "Suggested filename too long.");
            }

            string targetDir;
            try
            {
                targetDir = _getDefaultDirectory();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, BrowserErrorCodes.InternalError, "No target directory: " + TrimError(ex.Message));
            }
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, BrowserErrorCodes.InternalError, "No target directory configured.");
            }

            var warnings = new List<string>();
            BrowserRequestContext? context = null;
            try
            {
                context = new BrowserRequestContext
                {
                    Referrer = request.Referrer,
                    UserAgent = allowedHeaders.TryGetValue("User-Agent", out var ua) ? ua : null,
                };
                foreach (var kv in allowedHeaders)
                {
                    if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                        continue; // carried on UserAgent, applied once
                    context.Headers[kv.Key] = kv.Value;
                }
                if (cookieCount > 0)
                    warnings.Add("cookies-deferred-to-012"); // accepted structurally, never applied (§17)
            }
            catch { context = null; }

            DownloadJob job;
            try
            {
                job = await _scheduler.EnqueueBrowserAsync(new BrowserEnqueueOptions
                {
                    Url = url,
                    TargetDirectory = targetDir,
                    SuggestedFileName = request.SuggestedFileName,
                    Referrer = request.Referrer,
                    SourceApplication = source,
                    BrowserRequestId = requestId,
                    RequestContext = context,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _rejectedCount);
                return Fail(requestId, BrowserErrorCodes.InternalError, TrimError(ex.Message));
            }

            _recent.Remember(requestId, job.JobId);
            Interlocked.Increment(ref _acceptedCount);
            return new BrowserEnqueueResponse
            {
                RequestId = requestId,
                Accepted = true,
                JobId = job.JobId.ToString(),
                Message = "Queued.",
                Warnings = warnings.Count > 0 ? warnings : null,
            };
        }

        private static BrowserEnqueueResponse Fail(string requestId, string code, string message)
            => new() { RequestId = requestId, Accepted = false, ErrorCode = code, Message = message };

        private static string TrimError(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return "unknown error";
            // Redact: cap length, strip newlines; caller never passes secrets
            // (URLs go through BrowserValidation.RedactUrl first where needed).
            message = message.Replace('\r', ' ').Replace('\n', ' ');
            return message.Length > 200 ? message.Substring(0, 200) : message;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _loopCts?.Cancel(); } catch { }
            try { _loopCts?.Dispose(); } catch { }
            _loopCts = null;
        }

        private sealed class ConcurrentIdempotencyCache
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Guid JobId, DateTimeOffset Ts)> _map =
                new(StringComparer.Ordinal);
            private long _sweepCounter;

            public bool TryGet(string requestId, out Guid jobId)
            {
                jobId = Guid.Empty;
                if (!_map.TryGetValue(requestId, out var entry))
                    return false;
                if (DateTimeOffset.UtcNow - entry.Ts > IdempotencyWindow)
                {
                    _map.TryRemove(requestId, out _);
                    return false;
                }
                jobId = entry.JobId;
                return true;
            }

            public void Remember(string requestId, Guid jobId)
            {
                _map[requestId] = (jobId, DateTimeOffset.UtcNow);
                if (Interlocked.Increment(ref _sweepCounter) % 64 == 0)
                {
                    var cutoff = DateTimeOffset.UtcNow - IdempotencyWindow;
                    foreach (var kv in _map)
                    {
                        if (kv.Value.Ts < cutoff)
                            _map.TryRemove(kv.Key, out _);
                    }
                }
            }
        }
    }
}
