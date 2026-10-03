using System.IO;
using System.Text;
using System.Text.Json;
using DRRipper.BrowserBridge;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #007 native-host tests (T-BROWSER-HOST-01..10). Framing +
    /// NativeHostRequestHandler with a fake desktop forwarder. No processes,
    /// no pipes, no registry.
    /// </summary>
    public sealed class BrowserHostTests
    {
        private sealed class FakeForwarder : IDesktopForwarder
        {
            public Func<BrowserEnqueueRequest, BrowserEnqueueResponse>? Handler;
            public int Calls;
            public BrowserEnqueueRequest? Last;

            public Task<BrowserEnqueueResponse> ForwardAsync(BrowserEnqueueRequest request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                Last = request;
                if (Handler != null)
                    return Task.FromResult(Handler(request));
                return Task.FromResult(new BrowserEnqueueResponse
                {
                    RequestId = request.RequestId,
                    Accepted = true,
                    JobId = Guid.NewGuid().ToString(),
                    Message = "Queued.",
                });
            }
        }

        private static BrowserEnqueueRequest ValidRequest(string? requestId = null) => new()
        {
            Version = BrowserBridgeProtocol.CurrentVersion,
            Type = BrowserBridgeProtocol.MessageTypeEnqueue,
            RequestId = requestId ?? Guid.NewGuid().ToString("N"),
            Url = "https://example.com/files/setup.exe?token=abc",
            SuggestedFileName = "setup.exe",
            Referrer = "https://example.com/downloads",
            Source = "Chrome",
            Headers = new Dictionary<string, string>
            {
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["X-Custom"] = "dropped",
                ["Content-Length"] = "9999",
            },
        };

        private static byte[] Payload(BrowserEnqueueRequest request)
            => JsonSerializer.SerializeToUtf8Bytes(request);

        [Fact] // T-BROWSER-HOST-01: valid framed enqueue request accepted, id preserved.
        public async Task T_BROWSER_HOST_01_Valid_Enqueue()
        {
            var forwarder = new FakeForwarder();
            var handler = new NativeHostRequestHandler(forwarder);
            var request = ValidRequest("req-1");
            var response = await handler.HandlePayloadAsync(Payload(request), default);
            Assert.True(response.Accepted);
            Assert.Equal("req-1", response.RequestId);
            Assert.NotNull(response.JobId);
            Assert.Null(response.ErrorCode);
            Assert.Equal(1, forwarder.Calls);
            // Transport-owned headers never reach the desktop; allowlisted ones do.
            Assert.NotNull(forwarder.Last);
            Assert.False(forwarder.Last!.Headers!.ContainsKey("Content-Length"));
            Assert.False(forwarder.Last.Headers.ContainsKey("X-Custom"));
            Assert.Equal("en-US,en;q=0.9", forwarder.Last.Headers["Accept-Language"]);
        }

        [Fact] // T-BROWSER-HOST-02: malformed JSON rejected with category.
        public async Task T_BROWSER_HOST_02_Malformed_Json_Rejected()
        {
            var handler = new NativeHostRequestHandler(new FakeForwarder());
            var response = await handler.HandlePayloadAsync(
                Encoding.UTF8.GetBytes("{ this is not json {{{"), default);
            Assert.False(response.Accepted);
            Assert.Equal(BrowserErrorCodes.MalformedMessage, response.ErrorCode);
        }

        [Fact] // T-BROWSER-HOST-03: oversized message rejected at framing.
        public async Task T_BROWSER_HOST_03_Oversized_Rejected()
        {
            using var stream = new MemoryStream();
            var len = BrowserBridgeProtocol.MaxMessageBytes + 1;
            stream.Write(BitConverter.GetBytes(len), 0, 4);
            stream.Position = 0;
            var ex = await Record.ExceptionAsync(() =>
                NativeMessageFraming.ReadPayloadAsync(stream, default));
            Assert.NotNull(ex);
            Assert.IsType<InvalidDataException>(ex);
            Assert.Contains("exceeds", ex!.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact] // T-BROWSER-HOST-04: truncated frame rejected (no hang).
        public async Task T_BROWSER_HOST_04_Truncated_Rejected()
        {
            using var stream = new MemoryStream();
            var payload = Payload(ValidRequest());
            stream.Write(BitConverter.GetBytes(payload.Length), 0, 4);
            stream.Write(payload, 0, payload.Length / 2); // half the body missing
            stream.Position = 0;
            var ex = await Record.ExceptionAsync(() =>
                NativeMessageFraming.ReadPayloadAsync(stream, default));
            Assert.NotNull(ex);
            Assert.IsType<EndOfStreamException>(ex);
        }

        [Fact] // T-BROWSER-HOST-05: unsupported version rejected before forwarding.
        public async Task T_BROWSER_HOST_05_Version_Rejected()
        {
            var forwarder = new FakeForwarder();
            var handler = new NativeHostRequestHandler(forwarder);
            var request = ValidRequest();
            request.Version = 999;
            var response = await handler.HandlePayloadAsync(Payload(request), default);
            Assert.False(response.Accepted);
            Assert.Equal(BrowserErrorCodes.UnsupportedVersion, response.ErrorCode);
            Assert.Equal(0, forwarder.Calls); // never forwarded
        }

        [Fact] // T-BROWSER-HOST-06: disallowed schemes rejected (§4).
        public async Task T_BROWSER_HOST_06_Scheme_Rejected()
        {
            var forwarder = new FakeForwarder();
            var handler = new NativeHostRequestHandler(forwarder);
            foreach (var url in new[]
            {
                "file:///C:/evil.exe",
                "javascript:alert(1)",
                "data:text/plain,hi",
                "ftp://example.com/f.bin",
                "dripper://queue/add",
            })
            {
                var request = ValidRequest();
                request.Url = url;
                var response = await handler.HandlePayloadAsync(Payload(request), default);
                Assert.False(response.Accepted);
                Assert.Equal(BrowserErrorCodes.DisallowedScheme, response.ErrorCode);
            }
            Assert.Equal(0, forwarder.Calls);
        }

        [Fact] // T-BROWSER-HOST-07: sensitive/transport headers filtered (§16).
        public async Task T_BROWSER_HOST_07_Headers_Filtered()
        {
            var forwarder = new FakeForwarder();
            var handler = new NativeHostRequestHandler(forwarder);
            var request = ValidRequest();
            request.Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer secret-token",
                ["Host"] = "evil.example",
                ["Cookie"] = "session=abc",
                ["Range"] = "bytes=0-1",
                ["Accept"] = "application/octet-stream",
            };
            var response = await handler.HandlePayloadAsync(Payload(request), default);
            Assert.True(response.Accepted);
            Assert.NotNull(forwarder.Last);
            Assert.Equal("Bearer secret-token", forwarder.Last!.Headers!["Authorization"]);
            Assert.Equal("application/octet-stream", forwarder.Last.Headers["Accept"]);
            Assert.False(forwarder.Last.Headers.ContainsKey("Host"));
            Assert.False(forwarder.Last.Headers.ContainsKey("Cookie"));
            Assert.False(forwarder.Last.Headers.ContainsKey("Range"));
        }

        [Fact] // T-BROWSER-HOST-08: response preserves requestId even when the far end rewrites it.
        public async Task T_BROWSER_HOST_08_RequestId_Preserved()
        {
            var forwarder = new FakeForwarder
            {
                Handler = _ => new BrowserEnqueueResponse
                {
                    RequestId = "rewritten-by-far-end",
                    Accepted = true,
                    JobId = Guid.NewGuid().ToString(),
                },
            };
            var handler = new NativeHostRequestHandler(forwarder);
            var response = await handler.HandlePayloadAsync(Payload(ValidRequest("keep-me")), default);
            Assert.True(response.Accepted);
            Assert.Equal("keep-me", response.RequestId);
        }

        [Fact] // T-BROWSER-HOST-09: desktop unavailable produces the explicit category.
        public async Task T_BROWSER_HOST_09_Desktop_Unavailable()
        {
            var forwarder = new FakeForwarder
            {
                Handler = _ => throw new InvalidOperationException("pipe refused"),
            };
            // Throw inside Task to simulate async forwarder failure.
            var throwing = new ThrowingForwarder();
            var handler = new NativeHostRequestHandler(throwing);
            var response = await handler.HandlePayloadAsync(Payload(ValidRequest("r9")), default);
            Assert.False(response.Accepted);
            Assert.Equal(BrowserErrorCodes.DesktopUnavailable, response.ErrorCode);
            Assert.Equal("r9", response.RequestId);
        }

        private sealed class ThrowingForwarder : IDesktopForwarder
        {
            public Task<BrowserEnqueueResponse> ForwardAsync(BrowserEnqueueRequest request, CancellationToken ct)
                => Task.FromException<BrowserEnqueueResponse>(new InvalidOperationException("pipe refused"));
        }

        [Fact] // T-BROWSER-HOST-10: framing round-trips concurrently without cross-talk.
        public async Task T_BROWSER_HOST_10_Concurrent_Frames()
        {
            using var stream = new MemoryStream();
            var requests = Enumerable.Range(0, 16).Select(i => ValidRequest("c-" + i)).ToList();
            foreach (var r in requests)
            {
                var frame = NativeMessageFraming.Encode(r);
                stream.Write(frame, 0, frame.Length);
            }
            stream.Position = 0;
            var read = new List<BrowserEnqueueRequest>();
            for (int i = 0; i < 16; i++)
                read.Add(await NativeMessageFraming.ReadAsync<BrowserEnqueueRequest>(stream, default));
            Assert.Equal(16, read.Select(r => r.RequestId).Distinct().Count());
            for (int i = 0; i < 16; i++)
                Assert.Equal("c-" + i, read[i].RequestId);
        }

        [Fact] // Zero-length frame rejected (never an empty deserialize).
        public async Task T_BROWSER_HOST_Zero_Length_Rejected()
        {
            using var stream = new MemoryStream(new byte[4]);
            var ex = await Record.ExceptionAsync(() =>
                NativeMessageFraming.ReadPayloadAsync(stream, default));
            Assert.NotNull(ex);
            Assert.IsType<InvalidDataException>(ex);
        }

        [Theory] // §33: filename sanitization never escapes the target directory.
        [InlineData("../evil.exe", "evil.exe")]
        [InlineData("..\\evil.exe", "evil.exe")]
        [InlineData("C:\\Windows\\evil.exe", "evil.exe")]
        [InlineData("/etc/evil.exe", "evil.exe")]
        [InlineData("CON", "_CON")]
        [InlineData("NUL.txt", "_NUL.txt")]
        [InlineData("trailing. ", "trailing")]
        [InlineData("a:b.exe", "a_b.exe")]
        [InlineData("", "download.bin")]
        public void T_BROWSER_HOST_Filename_Sanitized(string raw, string expected)
        {
            var safe = BrowserValidation.SanitizeFileName(raw);
            Assert.Equal(expected, safe);
            Assert.DoesNotContain("/", safe);
            Assert.DoesNotContain("\\", safe);
            Assert.DoesNotContain("..", safe);
        }

        [Fact]
        public void T_BROWSER_HOST_Filename_Long_Truncated()
        {
            var safe = BrowserValidation.SanitizeFileName(new string('a', 300) + ".exe");
            Assert.True(safe.Length <= 255);
            Assert.DoesNotContain("/", safe);
        }
    }
}
