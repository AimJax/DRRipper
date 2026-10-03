using System.IO;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #008 budget tests (T-PERF-BUDGET-01..05): full-budget saturation,
    /// sharing without deadlock, host caps, redirect attribution, leak-free cancel.
    /// </summary>
    [Collection("SchedulerSerial")]
    public sealed class SchedulerPerfBudgetTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(300);
        private readonly ITestOutputHelper _output;

        public SchedulerPerfBudgetTests(ITestOutputHelper output) => _output = output;

        private sealed class Rig : IAsyncDisposable
        {
            public string Dir { get; }
            public string DlDir { get; }
            public JobStore Store { get; private set; } = null!;
            public DownloadScheduler Scheduler { get; private set; } = null!;

            private Rig(string dir, string dl) { Dir = dir; DlDir = dl; }

            public static async Task<Rig> CreateAsync(SchedulerSettings settings)
            {
                var dir = Path.Combine(Path.GetTempPath(), "DRRipperPerfBud", Guid.NewGuid().ToString("N"));
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

        private static SchedulerSettings MaxSettings(int active = 1) => new()
        {
            ActiveDownloadLimit = active,
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

        private static async Task WaitForBalanceAsync(DownloadScheduler scheduler)
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
            Assert.Fail($"Budget never balanced: available={scheduler.Budget.AvailableGlobal} " +
                $"pending={scheduler.Budget.PendingWaiterCount} active={scheduler.ActiveCount}");
        }

        [Fact] // T-PERF-BUDGET-01: one Maximum job consumes most of the full budget.
        public async Task T_PERF_BUDGET_01_Single_Consumes_Budget()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 500L * 1024 * 1024,
                Seed = 31,
                BytesPerSecond = 2L * 1024 * 1024, // per-connection pace: ramp has windows to engage
            });
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 32);
            var done = await WaitTerminalAsync(rig.Store, job.JobId);
            Assert.True(done.State == JobState.Completed, $"Ended {done.State}: {done.FailureReason}");
            int maxSeen = rig.Scheduler.Budget.MaxGlobalObserved;
            _output.WriteLine($"OBSERVATION: 32-worker single job, max global observed={maxSeen}.");
            Assert.True(maxSeen >= 16, $"Expected ramp past initial 8, saw {maxSeen}.");
            Assert.Equal(32, rig.Scheduler.Budget.AvailableGlobal);
        }

        [Fact] // T-PERF-BUDGET-02: two Maximum jobs share the budget without deadlock.
        public async Task T_PERF_BUDGET_02_Two_Jobs_Share()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 30L * 1024 * 1024,
                Seed = 32,
            });
            await using var rig = await Rig.CreateAsync(MaxSettings(active: 2));
            var a = await rig.Scheduler.EnqueueAsync(fx.Url + "?a=1", rig.DlDir, connectionsPerFile: 16);
            var b = await rig.Scheduler.EnqueueAsync(fx.Url + "?a=2", rig.DlDir, connectionsPerFile: 16);
            var da = await WaitTerminalAsync(rig.Store, a.JobId);
            var db = await WaitTerminalAsync(rig.Store, b.JobId);
            Assert.Equal(JobState.Completed, da.State);
            Assert.Equal(JobState.Completed, db.State);
            fx.AssertFileHash(da.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-BUDGET-02a");
            fx.AssertFileHash(db.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-BUDGET-02b");
            Assert.Equal(32, rig.Scheduler.Budget.AvailableGlobal);
            _output.WriteLine("OBSERVATION: 2×16-conn jobs shared the 32 budget, both byte-identical.");
        }

        [Fact] // T-PERF-BUDGET-03: per-host cap still respected at high concurrency.
        public async Task T_PERF_BUDGET_03_Host_Cap_Respected()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 40L * 1024 * 1024,
                Seed = 33,
            });
            var settings = MaxSettings();
            settings.PerHostConnectionBudget = 8; // tight host cap, roomy global
            await using var rig = await Rig.CreateAsync(settings);
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 16);
            var done = await WaitTerminalAsync(rig.Store, job.JobId);
            Assert.Equal(JobState.Completed, done.State);
            int maxHost = rig.Scheduler.Budget.MaxHostObserved(done.HostKey!);
            _output.WriteLine($"OBSERVATION: host cap 8 with 16 workers, max host observed={maxHost}.");
            Assert.True(maxHost <= 8, $"Host cap violated: {maxHost} > 8.");
        }

        [Fact] // T-PERF-BUDGET-04: redirected host correctly accounted (§39).
        public async Task T_PERF_BUDGET_04_Redirect_Accounting()
        {
            await using var target = await TestDownloadServer.StartAsync(new ServerProfile
            {
                FileSize = 40L * 1024 * 1024,
                Seed = 34,
                BytesPerSecond = 8L * 1024 * 1024, // paced: post-redirect attempts span seconds
            });
            var redirectProfile = new ServerProfile
            {
                FileSize = 10L * 1024 * 1024,
                RedirectTarget = target.FileUrl("redirected.bin"),
            };
            await using var redirector = await TestDownloadServer.StartAsync(redirectProfile);
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(redirector.FileUrl("entry.bin"), rig.DlDir, connectionsPerFile: 8);
            var done = await WaitTerminalAsync(rig.Store, job.JobId);
            Assert.True(done.State == JobState.Completed,
                $"Redirect job ended {done.State}: {done.FailureReason}");
            // Effective host (target server) carried the transfer.
            string effectiveKey = ConnectionBudget.NormalizeHostKey(target.BaseAddress);
            int maxEffective = rig.Scheduler.Budget.MaxHostObserved(effectiveKey);
            _output.WriteLine($"OBSERVATION: redirect served by {effectiveKey}, max observed={maxEffective}.");
            Assert.True(maxEffective > 0, "Effective host saw no attributed permits.");
            Assert.Equal(32, rig.Scheduler.Budget.AvailableGlobal);
            Assert.Equal(0, rig.Scheduler.Budget.PendingWaiterCount);
        }

        [Fact] // T-PERF-BUDGET-05: cancel of a 32-connection job leaks no permits.
        public async Task T_PERF_BUDGET_05_Cancel_Leak_Free()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 300L * 1024 * 1024,
                Seed = 35,
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
            await WaitForBalanceAsync(rig.Scheduler);
            _output.WriteLine("OBSERVATION: 32-conn cancel settled with zero budget drift.");
        }
    }
}
