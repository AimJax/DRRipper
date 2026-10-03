using System.Diagnostics;
using System.IO.Pipes;
using DRRipper.BrowserBridge;

namespace DRRipper.NativeHost
{
    /// <summary>
    /// Desktop forwarder used by the native host: connects to the running
    /// DRRipper bridge pipe, optionally launching the desktop app hidden and
    /// retrying for a bounded period (§12/§29). One desktop process serves all
    /// handoffs — never a process per download (§44).
    /// </summary>
    public sealed class PipeDesktopForwarder : IDesktopForwarder
    {
        private readonly string _pipeName;
        private readonly string _desktopExePath;
        private readonly TimeSpan _launchWait;

        public PipeDesktopForwarder(
            string? pipeName = null,
            string? desktopExePath = null,
            TimeSpan? launchWait = null)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName)
                ? BrowserBridgeProtocol.PipeName
                : pipeName;
            _desktopExePath = desktopExePath ?? DiscoverDesktopExePath();
            _launchWait = launchWait ?? TimeSpan.FromSeconds(45);
        }

        public static string DefaultPipeName => BrowserBridgeProtocol.PipeName;

        public async Task<BrowserEnqueueResponse> ForwardAsync(BrowserEnqueueRequest request, CancellationToken ct)
        {
            var lastError = string.Empty;
            // 1. Fast path: desktop already running.
            var direct = await TryForwardOnceAsync(request, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (direct != null)
                return direct;

            // 2. Launch hidden and retry bounded (§29). No window flash: the app
            // starts with --background straight into the tray.
            lastError = "desktop bridge unreachable";
            try { LaunchDesktopHidden(); } catch (Exception ex) { lastError = Trim(ex.Message); }
            var deadline = DateTimeOffset.UtcNow + _launchWait;
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var attempt = await TryForwardOnceAsync(request, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (attempt != null)
                    return attempt;
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
            }
            throw new InvalidOperationException("Desktop unavailable (" + lastError + "). Start DRRipper and retry.");
        }

        private async Task<BrowserEnqueueResponse?> TryForwardOnceAsync(
            BrowserEnqueueRequest request, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                using var client = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                client.ReadMode = PipeTransmissionMode.Byte;
                await NativeMessageFraming.WriteAsync(client, request, cts.Token).ConfigureAwait(false);
                var response = await NativeMessageFraming.ReadAsync<BrowserEnqueueResponse>(
                    client, cts.Token).ConfigureAwait(false);
                return response;
            }
            catch
            {
                return null;
            }
        }

        private void LaunchDesktopHidden()
        {
            var exe = _desktopExePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                throw new InvalidOperationException("DRRipper.exe not found next to the native host.");
            var start = new ProcessStartInfo(exe, "--background --browser-bridge")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            };
            Process.Start(start);
        }

        internal static string DiscoverDesktopExePath()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var candidate = Path.Combine(baseDir, "DRRipper.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }
            return string.Empty;
        }

        private static string Trim(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "unknown error";
            message = message.Replace('\r', ' ').Replace('\n', ' ');
            return message.Length > 160 ? message.Substring(0, 160) : message;
        }
    }
}
