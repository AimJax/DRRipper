using System.IO;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #008 lifetime tests (T-PERF-LIFE-01..04): permit balance, cancel
    /// during ramp-up, pause/resume during adaptation, shutdown recovery —
    /// all at 32-connection Maximum concurrency.
    /// </summary>
    [Collection("SchedulerSerial")]
    public sealed class SchedulerPerfLifeTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(300);
        private readonly ITestOutputHelper _output;

        public SchedulerPerfLifeTests(ITestOutputHelper output) => _output = output;

        private sealed class Rig : IAsyncDisposable
        {
            public string Dir { get; }
            public string DlDir { get; }
            public JobStore Store { get; private set; } = null!;
            public DownloadScheduler Scheduler { get; private set; } = null!;

            private Rig(string dir, string dl) { Dir = dir; DlDir = dl; }

            public static async Task<Rig> CreateAsync(SchedulerSettings settings)
            {
                var dir = Path.Combine(Path.GetTempPath(), "DRRipperPerfLife", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var dl = Path.Combine(dir, "dl");
                Directory.CreateDirectory(dl);
                var store = await JobStore.CreateAsync(Path.Combine(dir, "queue.db"));
                var sched = new DownloadScheduler(store, settings);
                await sched.StartAsync();
                return new Rig(dir, dl) { Store = store, Scheduler = sched };
            }

            public async ValueTask DisposeAsync()
            {
                try { await Scheduler.StopAsync(); } catch { }
                try { Scheduler.Dispose(); } catch { }
                try { Store.Dispose(); } catch { }
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            }
        }

        private static SchedulerSettings MaxSettings() => new()
        {
            ActiveDownloadLimit = 1,
            GlobalConnectionBudget = 32,
            PerHostConnectionBudget = 32,
            TransferMode = TransferMode.MaximumThroughput,
            MaxConnectionsPerFile = 32,
        };

        private static async Task<DownloadJob> WaitTerminalAsync(JobStore store, Guid id)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < Budget)
            {
                var j = await store.GetAsync(id);
                if (j != null && j.IsTerminal) return j;
                await Task.Delay(200);
            }
            var cur = await store.GetAsync(id);
            Assert.Fail($"Timed out; last state={cur?.State} reason={cur?.FailureReason}");
            throw new InvalidOperationException("unreachable");
        }

        private static async Task<DownloadJob> WaitStateAsync(JobStore store, Guid id, Func<JobState, bool> pred, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < Budget)
            {
                var j = await store.GetAsync(id);
                if (j != null && pred(j.State)) return j;
                await Task.Delay(200);
            }
            var cur = await store.GetAsync(id);
            Assert.Fail($"Timed out waiting for {what}; last state={cur?.State}");
            throw new InvalidOperationException("unreachable");
        }

        [Fact] // T-PERF-LIFE-01: 32-worker completion leaks no permits.
        public async Task T_PERF_LIFE_01_No_Leaked_Permits()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 40L * 1024 * 1024,
                Seed = 41,
            });
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 32);
            var done = await WaitTerminalAsync(rig.Store, job.JobId);
            Assert.Equal(JobState.Completed, done.State);
            fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-LIFE-01");
            Assert.Equal(32, rig.Scheduler.Budget.AvailableGlobal);
            Assert.Equal(0, rig.Scheduler.Budget.PendingWaiterCount);
            Assert.Equal(0, rig.Scheduler.ActiveCount);
            _output.WriteLine("OBSERVATION: 32-worker run balanced exactly.");
        }

        [Fact] // T-PERF-LIFE-02: cancel during ramp-up cleans all workers.
        public async Task T_PERF_LIFE_02_Cancel_During_Ramp()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 300L * 1024 * 1024,
                Seed = 42,
                BytesPerSecond = 2L * 1024 * 1024, // slow: cancel lands mid-flight
            });
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 32);
            // Wait for genuine mid-transfer activity (≥1 chunk verified), so the
            // cancel deterministically hits in-flight workers under any load.
            await WaitForTransferActivityAsync(rig.Store, job.JobId);
            await rig.Scheduler.CancelJobAsync(job.JobId);
            var done = await WaitTerminalAsync(rig.Store, job.JobId);
            Assert.Equal(JobState.Cancelled, done.State);
            await WaitForBalanceAsync(rig.Scheduler, job.JobId, done.HostKey ?? string.Empty);
            _output.WriteLine("OBSERVATION: ramp-up cancel cleaned all 32 workers.");
        }

        private static async Task WaitForTransferActivityAsync(JobStore store, Guid id)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < Budget)
            {
                var j = await store.GetAsync(id);
                if (j != null && j.State == JobState.Downloading && j.CompletedBytes >= 8L * 1024 * 1024)
                    return;
                await Task.Delay(200);
            }
            var cur = await store.GetAsync(id);
            Assert.Fail($"No transfer activity; last state={cur?.State} bytes={cur?.CompletedBytes}");
            throw new InvalidOperationException("unreachable");
        }

        private static async Task WaitForBalanceAsync(DownloadScheduler scheduler, Guid jobId, string hostKey = "")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (scheduler.Budget.AvailableGlobal == 32 &&
                    scheduler.Budget.PendingWaiterCount == 0 &&
                    scheduler.ActiveCount == 0)
                    return;
                await Task.Delay(200);
            }
            int hostActive = -1;
            int hostMax = -1;
            string diag = string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(hostKey))
                {
                    hostActive = scheduler.Budget.CurrentHostUsed(hostKey);
                    hostMax = scheduler.Budget.MaxHostObserved(hostKey);
                }
                diag = scheduler.GetRunDiagnostics(jobId);
            }
            catch { }
            Assert.Fail($"Budget never balanced: available={scheduler.Budget.AvailableGlobal} " +
                $"pending={scheduler.Budget.PendingWaiterCount} active={scheduler.ActiveCount} " +
                $"hostActive={hostActive} hostMax={hostMax} maxGlobal={scheduler.Budget.MaxGlobalObserved} " +
                $"run=[{diag}]");
        }

        [Fact] // T-PERF-LIFE-03: pause/resume during adaptation stays correct.
        public async Task T_PERF_LIFE_03_Pause_Resume_During_Adaptation()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 300L * 1024 * 1024,
                Seed = 43,
                BytesPerSecond = 2L * 1024 * 1024, // slow: pause lands mid-flight
            });
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 32);
            await WaitStateAsync(rig.Store, job.JobId,
                s => s == JobState.Downloading, "active adaptation");
            await Task.Delay(4000); // let at least one adaptation window elapse
            await rig.Scheduler.PauseJobAsync(job.JobId);
            var paused = await WaitStateAsync(rig.Store, job.JobId,
                s => s == JobState.Paused, "paused");
            Assert.Equal(JobState.Paused, paused.State);
            await rig.Scheduler.ResumeJobAsync(job.JobId);
            var done = await WaitTerminalAsync(rig.Store, job.JobId);
            Assert.Equal(JobState.Completed, done.State);
            fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-LIFE-03");
            _output.WriteLine("OBSERVATION: pause/resume across adaptation windows byte-identical.");
        }

        [Fact] // T-PERF-LIFE-04: shutdown during 32-connection transfer recovers cleanly.
        public async Task T_PERF_LIFE_04_Shutdown_Recovers()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 150L * 1024 * 1024,
                Seed = 44,
                BytesPerSecond = 4L * 1024 * 1024, // slow: shutdown lands mid-flight
            });
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperPerfLife", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dl = Path.Combine(dir, "dl");
            Directory.CreateDirectory(dl);
            string db = Path.Combine(dir, "queue.db");
            try
            {
                var store = await JobStore.CreateAsync(db);
                var sched = new DownloadScheduler(store, MaxSettings());
                await sched.StartAsync();
                var job = await sched.EnqueueAsync(fx.Url, dl, connectionsPerFile: 32);
                await WaitStateAsync(store, job.JobId,
                    s => s == JobState.Downloading, "active transfer");
                await sched.StopAsync(); // immediate: races probe/startup by design
                try { sched.Dispose(); } catch { }
                try { store.Dispose(); } catch { }

                var store2 = await JobStore.CreateAsync(db);
                using var sched2 = new DownloadScheduler(store2, MaxSettings());
                await sched2.StartAsync();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                DownloadJob? done = null;
                while (sw.Elapsed < Budget)
                {
                    done = await store2.GetAsync(job.JobId);
                    if (done != null && done.IsTerminal) break;
                    await Task.Delay(300);
                }
                Assert.NotNull(done);
                Assert.True(done!.State == JobState.Completed,
                    $"Post-restart ended {done.State}: {done.FailureReason}");
                fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-LIFE-04");
                try { await sched2.StopAsync(); } catch { }
                try { store2.Dispose(); } catch { }
                _output.WriteLine("OBSERVATION: 32-conn shutdown/restart resumed byte-identical.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
