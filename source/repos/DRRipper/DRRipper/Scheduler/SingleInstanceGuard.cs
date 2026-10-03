using System.IO.Pipes;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Launch options parsed from the application command line (Ticket #007 §29/§30).
    /// Only explicit flags are honored; arbitrary URL execution is never performed.
    /// </summary>
    public sealed class AppLaunchOptions
    {
        public bool Background { get; set; }
        public bool BrowserBridge { get; set; }

        public static AppLaunchOptions Parse(string[] args)
        {
            var options = new AppLaunchOptions();
            if (args == null)
                return options;
            foreach (var raw in args)
            {
                if (raw == null)
                    continue;
                var a = raw.Trim();
                if (string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-background", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/background", StringComparison.OrdinalIgnoreCase))
                    options.Background = true;
                else if (string.Equals(a, "--browser-bridge", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-browser-bridge", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/browser-bridge", StringComparison.OrdinalIgnoreCase))
                    options.BrowserBridge = true;
                // Everything else is ignored (no arbitrary URL/file execution).
            }
            return options;
        }
    }

    /// <summary>
    /// Single-instance coordination (Ticket #007 §12): a session-local named
    /// mutex plus the bridge pipe. The first desktop process holds the mutex
    /// and serves the pipe; later processes (or the native host's launch
    /// handshake) reuse it instead of starting parallel queues.
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        /// <summary>Session-local mutex name: per-session, current-user, no admin.</summary>
        public static string MutexName => @"Local\DRRipper.SingleInstance";

        private Mutex? _mutex;
        private bool _acquired;
        private bool _disposed;
        // In-process reentrancy latch: same-process double acquisition (tests,
        // double-click races) is rejected deterministically without relying on
        // OS handle timing. Cross-process exclusion still uses the OS mutex.
        private static int s_held;

        public bool IsFirstInstance => _acquired;

        public bool TryAcquire()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SingleInstanceGuard));
            if (_acquired)
                return true;
            if (Interlocked.CompareExchange(ref s_held, 1, 0) != 0)
                return false;
            bool osAcquired = false;
            try
            {
                _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
                try
                {
                    osAcquired = _mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    osAcquired = true; // previous holder died; we own it now
                }
                if (!osAcquired)
                {
                    try { _mutex.Dispose(); } catch { }
                    _mutex = null;
                    Interlocked.Exchange(ref s_held, 0);
                    return false;
                }
                _acquired = true;
                return true;
            }
            catch
            {
                try { _mutex?.Dispose(); } catch { }
                _mutex = null;
                Interlocked.Exchange(ref s_held, 0);
                return false;
            }
        }

        /// <summary>
        /// Pings an already-running desktop bridge. Returns true when a live
        /// bridge answered (same process will serve the handoff, §44).
        /// </summary>
        public static async Task<bool> PingBridgeAsync(string pipeName, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                client.ReadMode = PipeTransmissionMode.Byte;
                await BrowserBridge.NativeMessageFraming.WriteAsync(client,
                    new BrowserBridge.BrowserEnqueueRequest
                    {
                        Version = BrowserBridge.BrowserBridgeProtocol.CurrentVersion,
                        Type = BrowserBridge.BrowserBridgeProtocol.MessageTypePing,
                        RequestId = Guid.NewGuid().ToString("N"),
                        Url = "https://localhost/",
                    }, cts.Token).ConfigureAwait(false);
                var response = await BrowserBridge.NativeMessageFraming.ReadAsync<BrowserBridge.BrowserEnqueueResponse>(
                    client, cts.Token).ConfigureAwait(false);
                return response != null && response.Accepted;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_acquired)
                {
                    try { _mutex?.ReleaseMutex(); } catch { }
                    Interlocked.Exchange(ref s_held, 0);
                }
            }
            catch { }
            finally
            {
                try { _mutex?.Dispose(); } catch { }
                _mutex = null;
                _acquired = false;
            }
        }
    }
}
