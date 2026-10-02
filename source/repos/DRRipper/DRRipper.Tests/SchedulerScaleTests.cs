using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>Ticket #005 scale tests (T-STRESS-QUEUE-* + mixed ledger). Bounded.</summary>
    [Collection("SchedulerSerial")]
    public sealed class SchedulerScaleTests
    {
        private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(300);
        private readonly ITestOutputHelper _output;

        public SchedulerScaleTests(ITestOutputHelper output) => _output = output;

        private static async Task WaitForAllTerminalAsync(JobStore store, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                var all = await store.GetAllJobsAsync();
                if (all.Count > 0 && all.All(j => j.IsTerminal)) return;
                await Task.Delay(300);
            }
            var left = (await store.GetAllJobsAsync()).Count(j => !j.IsTerminal);
            Assert.Fail($"Not all jobs terminal after {timeout.TotalSeconds:F0}s; {left} still active/queued.");
        }

        private async Task RunQueueToCompletionAsync(int count, long fileBytes, int limit, string testName)
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = fileBytes });
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dl = Path.Combine(dir, "dl");
            Directory.CreateDirectory(dl);
            string db = Path.Combine(dir, "queue.db");
            var proc = Process.GetCurrentProcess();
            long wsBefore = proc.WorkingSet64;
            try
            {
                var store = await JobStore.CreateAsync(db);
                using var sched = new DownloadScheduler(store, new SchedulerSettings
                {
                    ActiveDownloadLimit = limit,
                    GlobalConnectionBudget = 16,
                    PerHostConnectionBudget = 8,
                });
                await sched.StartAsync();
                var swEnq = Stopwatch.StartNew();
                var sb = new System.Text.StringBuilder(capacity: count * 80);
                for (int i = 0; i < count; i++) sb.AppendLine(fx.Url + $"?q{i}");
                var import = await sched.ImportAsync(sb.ToString(), dl, connectionsPerFile: 1);
                swEnq.Stop();
                Assert.Equal(count, import.Accepted);
                Assert.Equal(0, import.Rejected);
                var swRun = Stopwatch.StartNew();
                await WaitForAllTerminalAsync(store, TestBudget);
                swRun.Stop();
                var all = await store.GetAllJobsAsync();
                int completed = all.Count(j => j.State == JobState.Completed);
                long wsAfter = proc.WorkingSet64;
                _output.WriteLine($"OBSERVATION: {testName}: enqueue {swEnq.Elapsed.TotalSeconds:F1}s, " +
                    $"run {swRun.Elapsed.TotalSeconds:F1}s, completed {completed}/{count}, " +
                    $"ws-delta={(wsAfter - wsBefore) / 1024.0 / 1024.0:F1}MB, maxActive<={limit}.");
                Assert.Equal(count, completed);
                Assert.True(sched.ActiveCount <= limit, "Active limit violated during scale run.");
                await sched.StopAsync();
                store.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STRESS-QUEUE-100
        public async Task T_STRESS_QUEUE_100() => await RunQueueToCompletionAsync(100, 256L * 1024, 4, "QUEUE-100");

        [Fact] // T-STRESS-QUEUE-500
        public async Task T_STRESS_QUEUE_500() => await RunQueueToCompletionAsync(500, 128L * 1024, 4, "QUEUE-500");

        [Fact] // T-STRESS-QUEUE-1000 (small files, fast execution)
        public async Task T_STRESS_QUEUE_1000() => await RunQueueToCompletionAsync(1000, 64L * 1024, 4, "QUEUE-1000");

        [Fact] // T-STRESS-QUEUE-10000: enqueue/startup/memory/O(active) — not full download
        public async Task T_STRESS_QUEUE_10000()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 64L * 1024 });
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dl = Path.Combine(dir, "dl");
            Directory.CreateDirectory(dl);
            string db = Path.Combine(dir, "queue.db");
            var proc = Process.GetCurrentProcess();
            try
            {
                var store = await JobStore.CreateAsync(db);
                var sb = new System.Text.StringBuilder(capacity: 10000 * 80);
                for (int i = 0; i < 10000; i++) sb.AppendLine(fx.Url + $"?q{i}");
                var swEnq = Stopwatch.StartNew();
                var jobs = new List<DownloadJob>(10000);
                long pos = 0;
                foreach (var line in sb.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    jobs.Add(new DownloadJob
                    {
                        OriginalUrl = line.Trim(),
                        TargetDirectory = dl,
                        QueuePosition = pos++,
                        ConnectionsPerFile = 1,
                        HostKey = "sched-test",
                    });
                }
                await store.AddBatchAsync(jobs);
                swEnq.Stop();
                var dbSize = new FileInfo(db).Length;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long wsIdle = proc.WorkingSet64;

                // Startup time: reopen + normalize + eligible sample (no sessions).
                var swStart = Stopwatch.StartNew();
                store.Dispose();
                var store2 = await JobStore.CreateAsync(db);
                await store2.NormalizeInterruptedStatesAsync();
                var sample = await store2.GetEligibleJobsAsync(4);
                swStart.Stop();
                Assert.Equal(4, sample.Count);

                using var sched = new DownloadScheduler(store2, new SchedulerSettings { ActiveDownloadLimit = 3 });
                await sched.StartAsync();
                await Task.Delay(5000); // let a few start; 10k must NOT all materialize
                int active = sched.ActiveCount;
                long wsRunning = proc.WorkingSet64;
                _output.WriteLine($"OBSERVATION: QUEUE-10000: enqueue {swEnq.Elapsed.TotalSeconds:F1}s, " +
                    $"db={dbSize / 1024.0:F0}KB, reopen {swStart.Elapsed.TotalSeconds:F2}s, " +
                    $"ws-idle={wsIdle / 1024.0 / 1024.0:F1}MB, ws-running={wsRunning / 1024.0 / 1024.0:F1}MB, " +
                    $"active={active} (limit 3).");
                Assert.True(swEnq.Elapsed < TimeSpan.FromSeconds(120), "10k enqueue must be bounded.");
                Assert.True(active <= 3, $"O(active) violated: {active} active sessions for 10k queue.");
                await sched.StopAsync();
                store2.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // Mixed execution stress: successes + 404 + 500 + throttled + cancelled; ledger correct
        public async Task T_STRESS_MIXED_Ledger()
        {
            await using var good = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = 256L * 1024 });
            await using var notFound = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 256L * 1024,
                GlobalStatus = 404,
            });
            await using var broken = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 256L * 1024,
                GlobalStatus = 500,
            });
            await using var slow = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 2L * 1024 * 1024,
                BytesPerSecond = 512L * 1024,
            });
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperSched", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dl = Path.Combine(dir, "dl");
            Directory.CreateDirectory(dl);
            string db = Path.Combine(dir, "queue.db");
            try
            {
                var store = await JobStore.CreateAsync(db);
                using var sched = new DownloadScheduler(store, new SchedulerSettings
                {
                    ActiveDownloadLimit = 4,
                    GlobalConnectionBudget = 16,
                    PerHostConnectionBudget = 8,
                });
                await sched.StartAsync();
                var goodIds = new List<Guid>();
                var badIds = new List<Guid>();
                for (int i = 0; i < 40; i++)
                    goodIds.Add((await sched.EnqueueAsync(good.Url + $"?g={i}", dl, connectionsPerFile: 1)).JobId);
                for (int i = 0; i < 10; i++)
                    badIds.Add((await sched.EnqueueAsync(notFound.Url + $"?n={i}", dl, connectionsPerFile: 1)).JobId);
                for (int i = 0; i < 10; i++)
                    badIds.Add((await sched.EnqueueAsync(broken.Url + $"?b={i}", dl, connectionsPerFile: 1)).JobId);
                var slowJob = await sched.EnqueueAsync(slow.Url + "?slow=1", dl, connectionsPerFile: 2);
                // Cancel the slow job mid-flight.
                await Task.Delay(2000);
                await sched.CancelJobAsync(slowJob.JobId);

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TestBudget)
                {
                    var all = await store.GetAllJobsAsync();
                    if (all.All(j => j.IsTerminal)) break;
                    await Task.Delay(300);
                }
                var all2 = await store.GetAllJobsAsync();
                int completed = all2.Count(j => j.State == JobState.Completed);
                int failed = all2.Count(j => j.State == JobState.Failed);
                int cancelled = all2.Count(j => j.State == JobState.Cancelled);
                _output.WriteLine($"OBSERVATION: MIXED: completed={completed} failed={failed} cancelled={cancelled} total={all2.Count}.");
                foreach (var j in all2.Where(j => j.State == JobState.Failed).Take(8))
                    _output.WriteLine($"  FAILED job={j.JobId} url={j.OriginalUrl.Substring(0, Math.Min(80, j.OriginalUrl.Length))} reason={j.FailureReason}");
                Assert.Equal(40, completed);
                Assert.Equal(20, failed);
                Assert.Equal(1, cancelled);
                // Spot-check content of a good file.
                var one = await store.GetAsync(goodIds[0]);
                good.AssertFileHash(one!.ResolvedFinalPath!, good.ExpectedHash, "T-STRESS-MIXED content");
                await sched.StopAsync();
                store.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
