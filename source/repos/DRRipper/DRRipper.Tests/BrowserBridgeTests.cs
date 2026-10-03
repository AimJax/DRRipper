using System.IO;
using System.IO.Pipes;
using DRRipper.BrowserBridge;
using DRRipper.Scheduler;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #007 desktop bridge tests (T-BRIDGE-01..08). Real JobStore +
    /// real DownloadScheduler (never started: no transfers run) + real
    /// BrowserBridgeService over unique pipe names. Temp paths only.
    /// </summary>
    public sealed class BrowserBridgeTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

        private sealed class Rig : IDisposable
        {
            public readonly string Dir;
            public readonly JobStore Store;
            public readonly DownloadScheduler Scheduler;
            public readonly BrowserBridgeService Bridge;
            public readonly string TargetDir;
            public readonly string PipeName;

            public Rig()
            {
                Dir = Path.Combine(Path.GetTempPath(), "DRRipperBridgeTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Dir);
                TargetDir = Path.Combine(Dir, "dl");
                Directory.CreateDirectory(TargetDir);
                PipeName = "DRRipper.TestBridge." + Guid.NewGuid().ToString("N");
                Store = JobStore.CreateAsync(Path.Combine(Dir, "queue.db")).GetAwaiter().GetResult();
                Scheduler = new DownloadScheduler(Store, new SchedulerSettings());
                Bridge = new BrowserBridgeService(Scheduler, () => TargetDir, PipeName);
            }

            public void Dispose()
            {
                try { Bridge.Dispose(); } catch { }
                try { Scheduler.Dispose(); } catch { }
                try { Store.Dispose(); } catch { }
                try { Directory.Delete(Dir, true); } catch { }
            }
        }

        private static BrowserEnqueueRequest ValidRequest(string? requestId = null) => new()
        {
            Version = BrowserBridgeProtocol.CurrentVersion,
            Type = BrowserBridgeProtocol.MessageTypeEnqueue,
            RequestId = requestId ?? Guid.NewGuid().ToString("N"),
            Url = "https://example.com/files/setup.exe?token=abc123",
            SuggestedFileName = "../../evil.exe",
            Referrer = "https://example.com/downloads/page",
            Source = "Chrome",
            Headers = new Dictionary<string, string> { ["Accept-Language"] = "en-US" },
        };

        private static async Task<T> WithBudget<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, "Timed out: " + what);
            return await task;
        }

        private static async Task AwaitBudget(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, "Timed out: " + what);
            await task;
        }

        private static async Task<BrowserEnqueueResponse> SendOverPipeAsync(string pipeName, BrowserEnqueueRequest request)
        {
            using var cts = new CancellationTokenSource(Budget);
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token);
            client.ReadMode = PipeTransmissionMode.Byte;
            await NativeMessageFraming.WriteAsync(client, request, cts.Token);
            return await NativeMessageFraming.ReadAsync<BrowserEnqueueResponse>(client, cts.Token);
        }

        [Fact] // T-BRIDGE-01: valid enqueue reaches the scheduler with metadata.
        public async Task T_BRIDGE_01_Enqueue_Reaches_Scheduler()
        {
            using var rig = new Rig();
            var response = await WithBudget(
                rig.Bridge.HandleEnqueueAsync(ValidRequest("b1"), default), "enqueue");
            Assert.True(response.Accepted);
            Assert.Equal("b1", response.RequestId);
            Assert.NotNull(response.JobId);
            var job = await WithBudget(rig.Store.GetAsync(Guid.Parse(response.JobId!)), "fetch");
            Assert.NotNull(job);
            Assert.Equal("Chrome", job!.SourceApplication);
            Assert.Equal("example.com", job.ReferrerHost);
            Assert.Equal("b1", job.BrowserRequestId);
            // Browser owns the suggestion only; DRRipper owns the directory (§32).
            Assert.Equal(rig.TargetDir, job.TargetDirectory);
            Assert.Equal("evil.exe", job.RequestedFileName); // traversal stripped
        }

        [Fact] // T-BRIDGE-02: returned JobId corresponds to a persisted job.
        public async Task T_BRIDGE_02_JobId_Persisted()
        {
            using var rig = new Rig();
            var response = await WithBudget(
                rig.Bridge.HandleEnqueueAsync(ValidRequest(), default), "enqueue");
            Assert.True(response.Accepted);
            var jobId = Guid.Parse(response.JobId!);
            using var store2 = await WithBudget(JobStore.CreateAsync(Path.Combine(rig.Dir, "queue.db")), "reopen");
            var reloaded = await WithBudget(store2.GetAsync(jobId), "reload");
            Assert.NotNull(reloaded);
            Assert.Equal("Chrome", reloaded!.SourceApplication);
            Assert.Equal("example.com", reloaded.ReferrerHost);
        }

        [Fact] // T-BRIDGE-03: duplicate requestId is idempotent — one job, same id.
        public async Task T_BRIDGE_03_Duplicate_Idempotent()
        {
            using var rig = new Rig();
            var first = await WithBudget(rig.Bridge.HandleEnqueueAsync(ValidRequest("dup-1"), default), "first");
            var second = await WithBudget(rig.Bridge.HandleEnqueueAsync(ValidRequest("dup-1"), default), "second");
            Assert.True(first.Accepted);
            Assert.True(second.Accepted);
            Assert.True(second.Duplicate);
            Assert.Equal(first.JobId, second.JobId);
            var all = await WithBudget(rig.Store.GetAllJobsAsync(), "list");
            Assert.Single(all);
        }

        [Fact] // T-BRIDGE-04: different requestIds with the same URL create independent jobs.
        public async Task T_BRIDGE_04_Distinct_Ids_Independent()
        {
            using var rig = new Rig();
            var a = await WithBudget(rig.Bridge.HandleEnqueueAsync(ValidRequest("id-a"), default), "a");
            var b = await WithBudget(rig.Bridge.HandleEnqueueAsync(ValidRequest("id-b"), default), "b");
            Assert.True(a.Accepted);
            Assert.True(b.Accepted);
            Assert.False(a.Duplicate);
            Assert.False(b.Duplicate);
            Assert.NotEqual(a.JobId, b.JobId);
            var all = await WithBudget(rig.Store.GetAllJobsAsync(), "list");
            Assert.Equal(2, all.Count);
        }

        [Fact] // T-BRIDGE-05: malformed payloads rejected with categories, nothing enqueued.
        public async Task T_BRIDGE_05_Malformed_Rejected()
        {
            using var rig = new Rig();
            var badScheme = ValidRequest();
            badScheme.Url = "ftp://example.com/f.bin";
            var r1 = await WithBudget(rig.Bridge.HandleEnqueueAsync(badScheme, default), "scheme");
            Assert.False(r1.Accepted);
            Assert.Equal(BrowserErrorCodes.DisallowedScheme, r1.ErrorCode);

            var badVersion = ValidRequest();
            badVersion.Version = 42;
            var r2 = await WithBudget(rig.Bridge.HandleEnqueueAsync(badVersion, default), "version");
            Assert.False(r2.Accepted);
            Assert.Equal(BrowserErrorCodes.UnsupportedVersion, r2.ErrorCode);

            var r3 = await WithBudget(rig.Bridge.HandleEnqueueAsync(null, default), "null");
            Assert.False(r3.Accepted);

            var all = await WithBudget(rig.Store.GetAllJobsAsync(), "list");
            Assert.Empty(all);
        }

        [Fact] // T-BRIDGE-06: bridge shutdown cleanly stops the pipe server.
        public async Task T_BRIDGE_06_Shutdown_Stops_Pipe()
        {
            using var rig = new Rig();
            rig.Bridge.Start();
            Assert.True(rig.Bridge.IsRunning);
            var pong = await WithBudget(
                Task.Run(async () => await SendOverPipeAsync(rig.PipeName, new BrowserEnqueueRequest
                {
                    Version = BrowserBridgeProtocol.CurrentVersion,
                    Type = BrowserBridgeProtocol.MessageTypePing,
                    RequestId = "ping-1",
                    Url = "https://localhost/",
                })), "ping");
            Assert.True(pong.Accepted);
            await AwaitBudget(rig.Bridge.StopAsync(), "stop");
            // After stop, new connections fail promptly (server gone).
            var refused = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut);
                await client.ConnectAsync(cts.Token);
            }
            catch { refused = true; }
            Assert.True(refused);
        }

        [Fact] // T-BRIDGE-07: handoff reuses the running bridge (same process); guard is exclusive.
        public async Task T_BRIDGE_07_Running_Bridge_Reused()
        {
            using var rig = new Rig();
            rig.Bridge.Start();
            // A pipe client reaching the live server proves same-process reuse (§44).
            var response = await WithBudget(
                Task.Run(async () => await SendOverPipeAsync(rig.PipeName, ValidRequest("reuse-1"))), "pipe enqueue");
            Assert.True(response.Accepted);
            Assert.Equal("reuse-1", response.RequestId);
            // Mutex exclusivity: a second guard cannot acquire while held.
            using var first = new SingleInstanceGuard();
            Assert.True(first.TryAcquire());
            using var second = new SingleInstanceGuard();
            Assert.False(second.TryAcquire());
            // ...and the live bridge answers pings (what the second instance checks).
            bool pong = await SingleInstanceGuard.PingBridgeAsync(rig.PipeName, TimeSpan.FromSeconds(10));
            Assert.True(pong);
            await AwaitBudget(rig.Bridge.StopAsync(), "stop");
        }

        [Fact] // T-BRIDGE-08: large but valid URL accepted within limits.
        public async Task T_BRIDGE_08_Large_Url_Accepted()
        {
            using var rig = new Rig();
            var request = ValidRequest("big-1");
            request.Url = "https://example.com/file.bin?token=" + new string('a', 7000);
            Assert.True(request.Url.Length < BrowserBridgeProtocol.MaxUrlLength);
            var response = await WithBudget(rig.Bridge.HandleEnqueueAsync(request, default), "enqueue");
            Assert.True(response.Accepted);
            Assert.NotNull(response.JobId);
            // The exact URL persists (required for resume); diagnostics redact it.
            var job = await WithBudget(rig.Store.GetAsync(Guid.Parse(response.JobId!)), "fetch");
            Assert.NotNull(job);
            Assert.Contains("token=", job!.OriginalUrl);
            Assert.DoesNotContain(new string('a', 100), BrowserValidation.RedactUrl(job.OriginalUrl));
        }
    }
}
