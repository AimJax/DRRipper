using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>Serial collection: scheduler core tests each run servers + SQLite + engine; same-collection tests never run in parallel.</summary>
    [CollectionDefinition("SchedulerSerial")]
    public sealed class SchedulerSerialCollection { }

    /// <summary>Ticket #005 core scheduler tests (T-SCHED-01..10). Bounded, gate-driven.</summary>
    [Collection("SchedulerSerial")]
    public sealed class SchedulerCoreTests
    {
        private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(120);
        private readonly ITestOutputHelper _output;

        public SchedulerCoreTests(ITestOutputHelper output) => _output = output;

        private sealed class SchedFixture : IAsyncDisposable
        {
            public string Dir { get; }
            public string DbPath { get; }
            public string DlDir { get; }
            public JobStore Store { get; private set; } = null!;
            public DownloadScheduler Scheduler { get; private set; } = null!;

            private SchedFixture(string dir, string db, string dl) { Dir = dir; DbPath = db; DlDir = dl; }

            public static async Task<SchedFixture> CreateAsync(SchedulerSettings? settings = null)
            {
                var dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var dl = Path.Combine(dir, "dl");
                Directory.CreateDirectory(dl);
                var db = Path.Combine(dir, "queue.db");
                var store = await JobStore.CreateAsync(db);
                var sched = new DownloadScheduler(store, settings ?? new SchedulerSettings
                {
                    ActiveDownloadLimit = 3,
                    GlobalConnectionBudget = 16,
                    PerHostConnectionBudget = 8,
                    ProgressPersistInterval = TimeSpan.FromSeconds(1),
                });
                await sched.StartAsync();
                return new SchedFixture(dir, db, dl) { Store = store, Scheduler = sched };
            }

            public async ValueTask DisposeAsync()
            {
                try { await Scheduler.StopAsync(); } catch { }
                try { Scheduler.Dispose(); } catch { }
                try { Store.Dispose(); } catch { }
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            }
        }

        private static async Task<DownloadJob> WaitForStateAsync(JobStore store, Guid id, Func<JobState, bool> pred, TimeSpan timeout, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                var j = await store.GetAsync(id);
                if (j != null && pred(j.State)) return j;
                await Task.Delay(100);
            }
            var cur = await store.GetAsync(id);
            Assert.Fail($"Timed out waiting for {what}; last state={cur?.State} reason={cur?.FailureReason}");
            throw new InvalidOperationException("unreachable");
        }

        private static async Task WaitForAllTerminalAsync(JobStore store, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                var all = await store.GetAllJobsAsync();
                if (all.Count > 0 && all.All(j => j.IsTerminal)) return;
                await Task.Delay(200);
            }
            var left = (await store.GetAllJobsAsync()).Where(j => !j.IsTerminal).Select(j => $"{j.JobId}:{j.State}");
            Assert.Fail("Not all jobs terminal: " + string.Join(",", left));
        }

        [Fact] // T-SCHED-01: enqueue one job -> completes
        public async Task T_SCHED_01_Single_Job_Completes()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 2L * 1024 * 1024 });
            await using var sf = await SchedFixture.CreateAsync();
            var job = await sf.Scheduler.EnqueueAsync(fx.Url, sf.DlDir, connectionsPerFile: 2);
            var done = await WaitForStateAsync(sf.Store, job.JobId,
                s => s == JobState.Completed || s == JobState.Failed,
                TestBudget, "single job terminal");
            Assert.Equal(JobState.Completed, done.State);
            Assert.NotNull(done.ResolvedFinalPath);
            Assert.True(File.Exists(done.ResolvedFinalPath!));
            fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-SCHED-01");
        }

        [Fact] // T-SCHED-02: good / 404 / good isolation
        public async Task T_SCHED_02_Failure_Isolation()
        {
            await using var good = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 1L * 1024 * 1024 });
            await using var bad = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 1L * 1024 * 1024,
                GlobalStatus = 404,
            });
            await using var sf = await SchedFixture.CreateAsync();
            var j1 = await sf.Scheduler.EnqueueAsync(good.Url, sf.DlDir, connectionsPerFile: 1);
            var j2 = await sf.Scheduler.EnqueueAsync(bad.Url, sf.DlDir, connectionsPerFile: 1);
            var j3 = await sf.Scheduler.EnqueueAsync(good.Url + "?second=1", sf.DlDir, connectionsPerFile: 1);
            await WaitForAllTerminalAsync(sf.Store, TestBudget);
            var g1 = await sf.Store.GetAsync(j1.JobId);
            var g2 = await sf.Store.GetAsync(j2.JobId);
            var g3 = await sf.Store.GetAsync(j3.JobId);
            Assert.Equal(JobState.Completed, g1!.State);
            Assert.Equal(JobState.Failed, g2!.State);
            Assert.NotNull(g2.FailureReason);
            Assert.Equal(JobState.Completed, g3!.State);
            _output.WriteLine($"OBSERVATION: isolation good1={g1.State} bad={g2.State}({g2.FailureReason}) good2={g3.State}");
        }

        [Fact] // T-SCHED-03: ActiveDownloadLimit enforced
        public async Task T_SCHED_03_Active_Limit_Enforced()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 8L * 1024 * 1024,
                BytesPerSecond = 1L * 1024 * 1024,
            });
            var settings = new SchedulerSettings
            {
                ActiveDownloadLimit = 2,
                GlobalConnectionBudget = 16,
                PerHostConnectionBudget = 8,
            };
            await using var sf = await SchedFixture.CreateAsync(settings);
            for (int i = 0; i < 5; i++)
                await sf.Scheduler.EnqueueAsync(fx.Url + $"?j={i}", sf.DlDir, connectionsPerFile: 2);
            int maxActive = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                maxActive = Math.Max(maxActive, sf.Scheduler.ActiveCount);
                var all = await sf.Store.GetAllJobsAsync();
                if (all.All(j => j.IsTerminal)) break;
                await Task.Delay(100);
            }
            Assert.True(maxActive <= 2, $"Active limit violated: max observed {maxActive} > 2");
            Assert.True(maxActive >= 1, "Scheduler never started any job.");
            _output.WriteLine($"OBSERVATION: max active observed={maxActive} (limit 2).");
            await sf.Scheduler.StopAsync();
        }

        [Fact] // T-SCHED-04: pause one active, others continue
        public async Task T_SCHED_04_Pause_One_Others_Continue()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 16L * 1024 * 1024,
                BytesPerSecond = 2L * 1024 * 1024,
            });
            await using var sf = await SchedFixture.CreateAsync();
            var j1 = await sf.Scheduler.EnqueueAsync(fx.Url + "?a=1", sf.DlDir, connectionsPerFile: 2);
            var j2 = await sf.Scheduler.EnqueueAsync(fx.Url + "?b=2", sf.DlDir, connectionsPerFile: 2);
            // Wait until both downloading.
            await WaitForStateAsync(sf.Store, j1.JobId, s => s == JobState.Downloading, TestBudget, "j1 downloading");
            await WaitForStateAsync(sf.Store, j2.JobId, s => s == JobState.Downloading, TestBudget, "j2 downloading");
            await sf.Scheduler.PauseJobAsync(j1.JobId);
            var p1 = await sf.Store.GetAsync(j1.JobId);
            _output.WriteLine($"OBSERVATION: after PauseJob j1 state={p1!.State} reason={p1.FailureReason} attempts={p1.AttemptCount}");
            Assert.Equal(JobState.Paused, p1!.State);
            // j2 must still make progress to completion.
            var done2 = await WaitForStateAsync(sf.Store, j2.JobId,
                s => s == JobState.Completed || s == JobState.Failed, TestBudget, "j2 terminal");
            Assert.Equal(JobState.Completed, done2.State);
            // Resume j1 -> completes.
            await sf.Scheduler.ResumeJobAsync(j1.JobId);
            var done1 = await WaitForStateAsync(sf.Store, j1.JobId,
                s => s == JobState.Completed || s == JobState.Failed, TestBudget, "j1 terminal after resume");
            Assert.Equal(JobState.Completed, done1.State);
        }

        [Fact] // T-SCHED-05: cancel one active, others continue
        public async Task T_SCHED_05_Cancel_One_Others_Continue()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 16L * 1024 * 1024,
                BytesPerSecond = 2L * 1024 * 1024,
            });
            await using var sf = await SchedFixture.CreateAsync();
            var j1 = await sf.Scheduler.EnqueueAsync(fx.Url + "?a=1", sf.DlDir, connectionsPerFile: 2);
            var j2 = await sf.Scheduler.EnqueueAsync(fx.Url + "?b=2", sf.DlDir, connectionsPerFile: 2);
            await WaitForStateAsync(sf.Store, j1.JobId, s => s == JobState.Downloading, TestBudget, "j1 downloading");
            await sf.Scheduler.CancelJobAsync(j1.JobId);
            var c1 = await sf.Store.GetAsync(j1.JobId);
            Assert.Equal(JobState.Cancelled, c1!.State);
            var done2 = await WaitForStateAsync(sf.Store, j2.JobId,
                s => s == JobState.Completed || s == JobState.Failed, TestBudget, "j2 terminal");
            Assert.Equal(JobState.Completed, done2.State);
        }

        [Fact] // T-SCHED-06: retry failed independently
        public async Task T_SCHED_06_Retry_Failed_Independently()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 1L * 1024 * 1024 });
            await using var bad = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 1L * 1024 * 1024,
                GlobalStatus = 500,
            });
            await using var sf = await SchedFixture.CreateAsync();
            var jGood = await sf.Scheduler.EnqueueAsync(fx.Url, sf.DlDir, connectionsPerFile: 1);
            var jBad = await sf.Scheduler.EnqueueAsync(bad.Url, sf.DlDir, connectionsPerFile: 1);
            var failed = await WaitForStateAsync(sf.Store, jBad.JobId,
                s => s == JobState.Failed || s == JobState.Completed, TestBudget, "bad terminal");
            Assert.Equal(JobState.Failed, failed.State);
            var goodDone = await sf.Store.GetAsync(jGood.JobId);
            Assert.Equal(JobState.Completed, goodDone!.State);
            // Heal server, retry only the failed job.
            bad.Server.Profile.GlobalStatus = null;
            await sf.Scheduler.RetryJobAsync(jBad.JobId);
            var retried = await WaitForStateAsync(sf.Store, jBad.JobId,
                s => s == JobState.Completed || s == JobState.Failed, TestBudget, "retried terminal");
            Assert.Equal(JobState.Completed, retried.State);
            // Good job untouched (still completed, same path).
            var goodAgain = await sf.Store.GetAsync(jGood.JobId);
            Assert.Equal(JobState.Completed, goodAgain!.State);
        }

        [Fact] // T-SCHED-07: PauseAll / ResumeAll
        public async Task T_SCHED_07_PauseAll_ResumeAll()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 16L * 1024 * 1024,
                BytesPerSecond = 2L * 1024 * 1024,
            });
            await using var sf = await SchedFixture.CreateAsync();
            var ids = new List<Guid>();
            for (int i = 0; i < 3; i++)
                ids.Add((await sf.Scheduler.EnqueueAsync(fx.Url + $"?j={i}", sf.DlDir, connectionsPerFile: 2)).JobId);
            await Task.Delay(3000); // let some start
            await sf.Scheduler.PauseAllAsync();
            await Task.Delay(2000);
            Assert.Equal(0, sf.Scheduler.ActiveCount);
            await sf.Scheduler.ResumeAllAsync();
            await WaitForAllTerminalAsync(sf.Store, TestBudget);
            var all = await sf.Store.GetAllJobsAsync();
            Assert.All(all, j => Assert.Equal(JobState.Completed, j.State));
        }

        [Fact] // T-SCHED-08: reorder waiting jobs follows QueuePosition
        public async Task T_SCHED_08_Reorder_FIFO()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 1L * 1024 * 1024 });
            var settings = new SchedulerSettings { ActiveDownloadLimit = 1 };
            await using var sf = await SchedFixture.CreateAsync(settings);
            // Suspend admission by pausing all immediately, then enqueue 3.
            await sf.Scheduler.PauseAllAsync();
            var a = await sf.Scheduler.EnqueueAsync(fx.Url + "?a=1", sf.DlDir, connectionsPerFile: 1);
            var b = await sf.Scheduler.EnqueueAsync(fx.Url + "?b=2", sf.DlDir, connectionsPerFile: 1);
            var c = await sf.Scheduler.EnqueueAsync(fx.Url + "?c=3", sf.DlDir, connectionsPerFile: 1);
            await sf.Scheduler.ReorderAsync(new[] { c.JobId, a.JobId, b.JobId });
            var eligible = await sf.Store.GetEligibleJobsAsync(10);
            Assert.Equal(c.JobId, eligible[0].JobId);
            Assert.Equal(a.JobId, eligible[1].JobId);
            Assert.Equal(b.JobId, eligible[2].JobId);
        }

        [Fact] // T-SCHED-09: duplicate URLs remain independent
        public async Task T_SCHED_09_Duplicate_Urls_Independent()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 1L * 1024 * 1024 });
            await using var sf = await SchedFixture.CreateAsync();
            var j1 = await sf.Scheduler.EnqueueAsync(fx.Url, sf.DlDir, connectionsPerFile: 1);
            var j2 = await sf.Scheduler.EnqueueAsync(fx.Url, sf.DlDir, connectionsPerFile: 1);
            Assert.NotEqual(j1.JobId, j2.JobId);
            await WaitForAllTerminalAsync(sf.Store, TestBudget);
            var g1 = await sf.Store.GetAsync(j1.JobId);
            var g2 = await sf.Store.GetAsync(j2.JobId);
            Assert.Equal(JobState.Completed, g1!.State);
            Assert.Equal(JobState.Completed, g2!.State);
            Assert.NotEqual(g1.ResolvedFinalPath, g2.ResolvedFinalPath);
            Assert.True(File.Exists(g1.ResolvedFinalPath!));
            Assert.True(File.Exists(g2.ResolvedFinalPath!));
        }

        [Fact] // T-SCHED-10: shutdown with active jobs preserves recovery state
        public async Task T_SCHED_10_Shutdown_Preserves_Recovery()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 32L * 1024 * 1024,
                BytesPerSecond = 2L * 1024 * 1024,
            });
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dl = Path.Combine(dir, "dl");
            Directory.CreateDirectory(dl);
            string db = Path.Combine(dir, "queue.db");
            Guid jobId;
            try
            {
                using (var store = await JobStore.CreateAsync(db))
                {
                    using var sched = new DownloadScheduler(store, new SchedulerSettings());
                    await sched.StartAsync();
                    var job = await sched.EnqueueAsync(fx.Url, dl, connectionsPerFile: 2);
                    jobId = job.JobId;
                    // Gate-driven partial progress: wait for Downloading + real
                    // .part/.drmeta on disk (bounded), never a fixed sleep.
                    var swGate = System.Diagnostics.Stopwatch.StartNew();
                    bool gated = false;
                    string lastSeen = "?";
                    while (swGate.Elapsed < TimeSpan.FromSeconds(90))
                    {
                        var cur = await store.GetAsync(jobId);
                        lastSeen = cur == null ? "null" : $"{cur.State} reason={cur.FailureReason}";
                        if (cur != null && cur.State == JobState.Downloading &&
                            Directory.GetFiles(dl, "*.part").Length > 0 &&
                            Directory.GetFiles(dl, "*.drmeta").Length > 0)
                        { gated = true; break; }
                        if (cur != null && cur.IsTerminal)
                            break; // faulted/cancelled fast: fail below with reason
                        await Task.Delay(200);
                    }
                    Assert.True(gated, $"Download never reached gated partial state. last={lastSeen}");
                    await Task.Delay(2000); // accumulate verified bytes past the first checkpoint
                    await sched.StopAsync(); // orderly shutdown preserves files
                }
                // Partial files must survive shutdown.
                var partFiles = Directory.GetFiles(dl, "*.part");
                var metaFiles = Directory.GetFiles(dl, "*.drmeta");
                Assert.NotEmpty(partFiles);
                Assert.NotEmpty(metaFiles);
                // Restart scheduler with same DB -> job resumable -> completes.
                using (var store2 = await JobStore.CreateAsync(db))
                {
                    var j = await store2.GetAsync(jobId);
                    Assert.NotNull(j);
                    Assert.True(j!.State == JobState.Interrupted || j.State == JobState.Queued || j.State == JobState.Paused,
                        $"expected resumable state, got {j.State}");
                    using var sched2 = new DownloadScheduler(store2, new SchedulerSettings());
                    await sched2.StartAsync();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    DownloadJob? done = null;
                    while (sw.Elapsed < TestBudget)
                    {
                        done = await store2.GetAsync(jobId);
                        if (done != null && done.IsTerminal) break;
                        await Task.Delay(200);
                    }
                    Assert.NotNull(done);
                    Assert.Equal(JobState.Completed, done!.State);
                    fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-SCHED-10 resumed");
                    await sched2.StopAsync();
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
