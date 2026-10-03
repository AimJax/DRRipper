using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace DRRipper
{
    /// <summary>
    /// Process-wide HTTP stack for scheduler-owned transfers (Ticket #008 §15):
    /// one <see cref="SocketsHttpHandler"/> shared by all jobs so connections,
    /// DNS, and TLS sessions are pooled across files instead of rebuilt per
    /// job. Per-job <see cref="HttpClient"/> instances still own their
    /// DefaultRequestHeaders (never shared mutable headers); per-request
    /// headers stay fully isolated as before. The experiment toggles are public
    /// so the benchmark harness can drive A/B comparisons.
    /// </summary>
    public static class SharedHttpStack
    {
        /// <summary>
        /// Experiment seam (§17): false uses the stock handler path (no custom
        /// ConnectCallback) for A/B measurement. Default true (current behavior).
        /// Public so the benchmark harness can toggle it.
        /// </summary>
        public static bool UseCustomConnectCallback = true;

        /// <summary>
        /// Experiment seam (§18): 0 = OS-default socket buffers (stock
        /// autotuning); 1 = 2 MB receive / 1 MB send (current behavior).
        /// Default 1. Public so the benchmark harness can toggle it.
        /// </summary>
        public static int SocketBufferScale = 1;

        private static readonly Lazy<SocketsHttpHandler> s_handler =
            new(BuildHandler, LazyThreadSafetyMode.ExecutionAndPublication);

        public static SocketsHttpHandler Handler => s_handler.Value;

        /// <summary>Single factory for handler options (shared + private stacks).</summary>
        public static SocketsHttpHandler BuildHandler()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = int.MaxValue,
                // Accept-Encoding: identity is forced per ranged request; the
                // handler-level decompression setting is retained for
                // non-range (probe/fallback) responses as before (§16).
                AutomaticDecompression = DecompressionMethods.All,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            };

            if (UseCustomConnectCallback)
                handler.ConnectCallback = CustomConnectAsync;
            return handler;
        }

        /// <summary>Per-job client over the shared handler (never disposes it).</summary>
        public static HttpClient CreateClient()
        {
            var client = new HttpClient(Handler, disposeHandler: false);
            ApplyDefaultHeaders(client);
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        internal static void ApplyDefaultHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }

        private static async ValueTask<Stream> CustomConnectAsync(
            SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var dns = context.DnsEndPoint;
            Exception? lastEx = null;
            var host = dns.Host;
            var port = dns.Port;
            IPAddress[] addrs;
            try
            {
                addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            }
            catch
            {
                // Preserve original DNS resolution exception for accurate diagnostics
                throw;
            }

            foreach (var ip in addrs)
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
                    if (SocketBufferScale > 0)
                    {
                        try { socket.ReceiveBufferSize = 2 * 1024 * 1024; } catch { }
                        try { socket.SendBufferSize = 1 * 1024 * 1024; } catch { }
                    }
                    socket.NoDelay = true;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    try { socket.Dispose(); } catch { }
                }
            }

            if (lastEx != null) throw lastEx;
            throw new SocketException((int)SocketError.HostNotFound);
        }
    }
}
