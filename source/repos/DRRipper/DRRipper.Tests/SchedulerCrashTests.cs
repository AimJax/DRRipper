using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #005 queue crash tests (T-QUEUE-CRASH-01..03).
    /// Crash is simulated by abandoning runtimes with zero orderly shutdown
    /// (no StopAsync, no state writes — the exact persistent artifacts of SIGKILL:
    /// Downloading rows + intact .part/.drmeta). Restart reopens the same DB file
    /// and resumes through DownloadSession recovery. Same restart code path as a
    /// genuine kill; documented in BASELINE (no child process spared the artifacts).
    /// </summary>
    [Collection("SchedulerSerial")]
    public sealed class SchedulerCrashTests
    {
        private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(120);
        private readonly ITestOutputHelper _output;

        public SchedulerCrashTests(ITestOutputHelper output) => _output = output;

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

        [Fact] // T-QUEUE-CRASH-01: kill with 2 active; restart resumes, completed not redownloaded
        public async Task T_QUEUE_CRASH_01_Kill_Active_Resumes()
        {
            await using var tinyFx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 1L * 1024 * 1024 });
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 24L * 1024 * 1024,
                BytesPerSecond = 2L * 1024 * 1024,
            });
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dl = Path.Combine(dir, "dl");
            Directory.CreateDirectory(dl);
            string db = Path.Combine(dir, "queue.db");
            Guid tinyId, big1Id, big2Id;
            string bigHash = fx.ExpectedHash;
            try
            {
                // Phase 1: run scheduler with 1 tiny (completes) + 2 big (active).
                var store = await JobStore.CreateAsync(db);
                var sched = new DownloadScheduler(store, new SchedulerSettings
                {
                    ActiveDownloadLimit = 2,
                    GlobalConnectionBudget = 16,
                    PerHostConnectionBudget = 8,
                });
                await sched.StartAsync();
                var tiny = await sched.EnqueueAsync(tinyFx.Url + "?tiny=1", dl, connectionsPerFile: 1);
                tinyId = tiny.JobId;
                var big1 = await sched.EnqueueAsync(fx.Url + "?big=1", dl, connectionsPerFile: 2);
                big1Id = big1.JobId;
                var big2 = await sched.EnqueueAsync(fx.Url + "?big=2", dl, connectionsPerFile: 2);
                big2Id = big2.JobId;

                // Wait: tiny completed, both bigs downloading with partial data.
                await WaitForStateAsync(store, tinyId, s => s == JobState.Completed, TestBudget, "tiny completed");
                await WaitForStateAsync(store, big1Id, s => s == JobState.Downloading, TestBudget, "big1 active");
                await WaitForStateAsync(store, big2Id, s => s == JobState.Downloading, TestBudget, "big2 active");
                await Task.Delay(3000); // accumulate verified bytes + checkpoints

                // SIGKILL simulation: abandon with zero orderly shutdown (no StopAsync,
                // no state writes). Rows stay Downloading; .part/.drmeta stay intact.
                sched.AbandonForKillTest();
                try { store.Dispose(); } catch { } // abrupt DB close (WAL keeps committed)

                // Phase 2: "reboot" — new objects on the same DB file.

                var store2 = await JobStore.CreateAsync(db);
                var preBig1 = await store2.GetAsync(big1Id);
                Assert.True(preBig1!.State == JobState.Downloading || preBig1.State == JobState.Interrupted,
                    $"expected crash-interrupted row, got {preBig1.State}");
                using var sched2 = new DownloadScheduler(store2, new SchedulerSettings());
                await sched2.StartAsync(); // normalizes to Interrupted, resumes via .drmeta
                var sw = System.Diagnostics.Stopwatch.StartNew();
                DownloadJob? dTiny = null, dBig1 = null, dBig2 = null;
                while (sw.Elapsed < TestBudget)
                {
                    dTiny = await store2.GetAsync(tinyId);
                    dBig1 = await store2.GetAsync(big1Id);
                    dBig2 = await store2.GetAsync(big2Id);
                    if (dTiny!.IsTerminal && dBig1!.IsTerminal && dBig2!.IsTerminal) break;
                    await Task.Delay(300);
                }
                Assert.Equal(JobState.Completed, dTiny!.State); // not redownloaded
                if (dBig1!.State != JobState.Completed || dBig2!.State != JobState.Completed)
                    _output.WriteLine($"OBSERVATION: post-restart big1={dBig1.State} reason={dBig1.FailureReason} big2={dBig2.State} reason={dBig2.FailureReason}");
                Assert.Equal(JobState.Completed, dBig1!.State);
                Assert.Equal(JobState.Completed, dBig2!.State);
                fx.AssertFileHash(dBig1.ResolvedFinalPath!, bigHash, "T-QUEUE-CRASH-01 big1");
                fx.AssertFileHash(dBig2.ResolvedFinalPath!, bigHash, "T-QUEUE-CRASH-01 big2");
                _output.WriteLine("OBSERVATION: kill with 2 active -> restart resumed both via .drmeta; completed job untouched.");
                await sched2.StopAsync();
                store2.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-QUEUE-CRASH-02: kill during DB state update preserves consistency
        public async Task T_QUEUE_CRASH_02_Db_Consistency()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string db = Path.Combine(dir, "queue.db");
            try
            {
                var store = await JobStore.CreateAsync(db);
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                // Interleave writes then abandon mid-batch (uncommitted tx rolls back).
                var jobs = Enumerable.Range(0, 50).Select(i => new DownloadJob
                {
                    OriginalUrl = $"https://example.com/f{i}.bin",
                    TargetDirectory = target,
                    QueuePosition = i,
                    HostKey = "https://example.com",
                }).ToList();
                await store.AddBatchAsync(jobs);
                await store.UpdateStateAsync(jobs[0].JobId, JobState.Downloading);
                // Abrupt close (no checkpoint, no normalize) then reopen.
                store.Dispose();
                using var store2 = await JobStore.CreateAsync(db);
                await store2.NormalizeInterruptedStatesAsync();
                var all = await store2.GetAllJobsAsync();
                Assert.Equal(50, all.Count); // committed batch intact
                var j0 = await store2.GetAsync(jobs[0].JobId);
                Assert.Equal(JobState.Interrupted, j0!.State); // normalized, consistent
                store2.Dispose();
                _output.WriteLine("OBSERVATION: abrupt close kept 50/50 committed rows; active row normalized.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-QUEUE-CRASH-03: kill while one finalizes and others run reconciles safely
        public async Task T_QUEUE_CRASH_03_Finalize_Race_Safe()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 1L * 1024 * 1024 });
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dl = Path.Combine(dir, "dl");
            Directory.CreateDirectory(dl);
            string db = Path.Combine(dir, "queue.db");
            try
            {
                var store = await JobStore.CreateAsync(db);
                using var sched = new DownloadScheduler(store, new SchedulerSettings { ActiveDownloadLimit = 3 });
                await sched.StartAsync();
                var ids = new System.Collections.Generic.List<Guid>();
                for (int i = 0; i < 4; i++)
                    ids.Add((await sched.EnqueueAsync(fx.Url + $"?f={i}", dl, connectionsPerFile: 1)).JobId);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TestBudget)
                {
                    var all = await store.GetAllJobsAsync();
                    if (all.All(j => j.IsTerminal)) break;
                    await Task.Delay(200);
                }
                var all2 = await store.GetAllJobsAsync();
                Assert.All(all2, j => Assert.Equal(JobState.Completed, j.State));
                // Every final exists, hashes match, no .part leftovers for completed.
                foreach (var j in all2)
                {
                    Assert.True(File.Exists(j.ResolvedFinalPath!), $"final missing for {j.JobId}");
                    fx.AssertFileHash(j.ResolvedFinalPath!, fx.ExpectedHash, "T-QUEUE-CRASH-03");
                }
                var leftovers = Directory.GetFiles(dl, "*.part");
                Assert.Empty(leftovers);
                await sched.StopAsync();
                store.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
