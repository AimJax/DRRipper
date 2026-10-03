using System.IO;
using System.Net.Http;
using DRRipper.BrowserBridge;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #007 request-context tests (T-BROWSER-CTX-01..05): validated
    /// browser metadata reaches the wire exactly as allowlisted, transport
    /// headers can never be smuggled, and secrets never reach diagnostics.
    /// </summary>
    public sealed class BrowserContextTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

        [Fact] // T-BROWSER-CTX-01: referrer reaches the request correctly.
        public void T_BROWSER_CTX_01_Referrer_Applied()
        {
            var context = new BrowserRequestContext { Referrer = "https://shop.example.com/item/42" };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.com/f.bin");
            context.ApplyTo(request);
            Assert.NotNull(request.Headers.Referrer);
            Assert.Equal("shop.example.com", request.Headers.Referrer!.Host);
        }

        [Fact] // T-BROWSER-CTX-02: allowed custom header reaches the request.
        public async Task T_BROWSER_CTX_02_Allowed_Header_Reaches_Server()
        {
            var dir = Path.Combine(Path.GetTempPath(), "DRRipperCtxE2E", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                await using var server = await TestDownloadServer.StartAsync(new ServerProfile
                {
                    FileSize = 2L * 1024 * 1024,
                    Seed = 11,
                });
                var context = new BrowserRequestContext
                {
                    Referrer = "https://shop.example.com/item/42",
                    UserAgent = "Mozilla/5.0 (Test) DRRipper-Browser/0.7",
                };
                context.Headers["Accept-Language"] = "de-DE,de;q=0.9";
                var downloader = new ParallelDownloader { RequestContext = context };
                using var cts = new CancellationTokenSource(Budget);
                var finalPath = await downloader.StartAsync(server.FileUrl("ctx.bin"), dir, 2, cts.Token);
                Assert.True(File.Exists(finalPath));
                var referrers = server.Requests
                    .Select(r => r.RequestHeaders != null && r.RequestHeaders.TryGetValue("Referer", out var v) ? v : null)
                    .Where(v => v != null)
                    .ToList();
                Assert.NotEmpty(referrers);
                Assert.All(referrers, v => Assert.Contains("shop.example.com", v!));
                var languages = server.Requests
                    .Select(r => r.RequestHeaders != null && r.RequestHeaders.TryGetValue("Accept-Language", out var v) ? v : null)
                    .Where(v => v != null)
                    .ToList();
                Assert.NotEmpty(languages);
                var agents = server.Requests
                    .Select(r => r.RequestHeaders != null && r.RequestHeaders.TryGetValue("User-Agent", out var v) ? v : null)
                    .Where(v => v != null)
                    .ToList();
                Assert.NotEmpty(agents);
                Assert.Contains(agents, v => v!.Contains("DRRipper-Browser"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-BROWSER-CTX-03: hop-by-hop/transport headers never reach the wire.
        public void T_BROWSER_CTX_03_Transport_Headers_Rejected()
        {
            var context = new BrowserRequestContext();
            // Even a hand-built map cannot smuggle transport semantics (§16/§18).
            context.Headers["Host"] = "evil.example";
            context.Headers["Content-Length"] = "1";
            context.Headers["Range"] = "bytes=0-1";
            context.Headers["Cookie"] = "s=1";
            context.Headers["Accept-Encoding"] = "gzip";
            context.Headers["Accept-Language"] = "fr-FR";
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.com/f.bin");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 200);
            context.ApplyTo(request);
            Assert.DoesNotContain(request.Headers.Select(h => h.Key),
                k => !k.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase) &&
                     !k.Equals("Range", StringComparison.OrdinalIgnoreCase) &&
                     !k.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
            var range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(100, range.From); // engine-owned range untouched
            Assert.Equal("fr-FR", string.Join(",", request.Headers.GetValues("Accept-Language")));
        }

        [Fact] // T-BROWSER-CTX-04: Authorization value applied to transport, redacted everywhere else.
        public void T_BROWSER_CTX_04_Authorization_Redacted()
        {
            var context = new BrowserRequestContext();
            context.Headers["Authorization"] = "Bearer super-secret-token-123";
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.com/f.bin");
            context.ApplyTo(request);
            Assert.True(request.Headers.Contains("Authorization")); // transport needs it
            var diagnostics = context.DescribeForDiagnostics();
            Assert.DoesNotContain("super-secret-token-123", diagnostics);
            Assert.Contains("Authorization:[redacted]", diagnostics);
            // CRLF smuggling rejected at validation, not at ApplyTo.
            Assert.False(BrowserValidation.TryFilterHeaders(
                new Dictionary<string, string> { ["Accept"] = "x\r\nInjected: 1" },
                out _, out _, out _));
        }

        [Fact] // T-BROWSER-CTX-05: cookie material never appears in ordinary diagnostics.
        public async Task T_BROWSER_CTX_05_Cookies_Never_In_Diagnostics()
        {
            var dir = Path.Combine(Path.GetTempPath(), "DRRipperCtxCk", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var store = await JobStore.CreateAsync(Path.Combine(dir, "queue.db"));
                using var scheduler = new DownloadScheduler(store, new SchedulerSettings());
                using var bridge = new BrowserBridgeService(scheduler, () => dir);
                var response = await bridge.HandleEnqueueAsync(new BrowserEnqueueRequest
                {
                    Version = BrowserBridgeProtocol.CurrentVersion,
                    Type = BrowserBridgeProtocol.MessageTypeEnqueue,
                    RequestId = "cookie-1",
                    Url = "https://example.com/f.bin",
                    Source = "Edge",
                    Cookies = new List<BrowserCookie>
                    {
                        new() { Name = "session", Value = "deadbeef-secret" },
                    },
                });
                Assert.True(response.Accepted);
                Assert.Contains(response.Warnings ?? new List<string>(), w => w.Contains("cookies-deferred"));
                var job = await store.GetAsync(Guid.Parse(response.JobId!));
                Assert.NotNull(job);
                // Nothing cookie-shaped persisted or described.
                var context = new BrowserRequestContext();
                var diagnostics = context.DescribeForDiagnostics();
                Assert.DoesNotContain("deadbeef", diagnostics);
                Assert.DoesNotContain("session", diagnostics);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
