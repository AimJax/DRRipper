using System.IO;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #008 high-concurrency integrity tests (T-PERF-INT-01..07).
    /// Exact content at 8/16/32 connections, reset recovery, crash restart,
    /// and rejection paths — all at elevated concurrency.
    /// </summary>
    [Collection("SchedulerSerial")]
    public sealed class SchedulerPerfIntegrityTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(300);
        private readonly ITestOutputHelper _output;

        public SchedulerPerfIntegrityTests(ITestOutputHelper output) => _output = output;

        private static string TempDir(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "DRRipperPerfInt", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static async Task<string> DownloadExactAsync(
            TestDownloadServer server, string dir, int connections, string context)
        {
            using var dl = new ParallelDownloader();
            using var cts = new CancellationTokenSource(Budget);
            string path = await dl.StartAsync(server.FileUrl(), dir, connections, cts.Token);
            string actual = DeterministicContent.Sha256HexFile(path);
            string expected = DeterministicContent.Sha256Hex(
                server.Profile.FileSize, server.Profile.Seed,
                server.Profile.ConstantContent, server.Profile.ConstantByte);
            Assert.True(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                $"{context}: content mismatch at {connections} connections.");
            return path;
        }

        [Theory] // T-PERF-INT-01/02/03: exact content at 8/16/32 connections.
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(32)]
        public async Task T_PERF_INT_High_Concurrency_Exact(int connections)
        {
            TempDir(out var dir);
            try
            {
                await using var server = await TestDownloadServer.StartAsync(new ServerProfile
                {
                    FileSize = 20L * 1024 * 1024,
                    Seed = 21,
                });
                await DownloadExactAsync(server, dir, connections, $"T-PERF-INT({connections})");
                _output.WriteLine($"OBSERVATION: 20 MB byte-identical at {connections} connections.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PERF-INT-04: connection-reset recovery at 16 connections, exact content.
        public async Task T_PERF_INT_04_Reset_Recovery_Exact()
        {
            TempDir(out var dir);
            try
            {
                await using var server = await TestDownloadServer.StartAsync(new ServerProfile
                {
                    FileSize = 20L * 1024 * 1024,
                    Seed = 22,
                    TruncateAfterBytes = 3L * 1024 * 1024, // every response aborts once
                });
                await DownloadExactAsync(server, dir, 16, "T-PERF-INT-04");
                _output.WriteLine("OBSERVATION: reset-heavy 16-conn transfer converged byte-identical.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PERF-INT-05: crash/restart at 16 connections, exact content.
        public async Task T_PERF_INT_05_Crash_Restart_Exact()
        {
            TempDir(out var dir);
            try
            {
                await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
                {
                    FileSize = 30L * 1024 * 1024,
                    Seed = 23,
                    BytesPerSecond = 4L * 1024 * 1024, // slow enough to kill mid-flight
                });
                string dl = Path.Combine(dir, "dl");
                Directory.CreateDirectory(dl);
                string db = Path.Combine(dir, "queue.db");
                var store = await JobStore.CreateAsync(db);
                var sched = new DownloadScheduler(store, new SchedulerSettings
                {
                    ActiveDownloadLimit = 1,
                    GlobalConnectionBudget = 32,
                    PerHostConnectionBudget = 32,
                });
                await sched.StartAsync();
                var job = await sched.EnqueueAsync(fx.Url, dl, connectionsPerFile: 16);
                // Wait for genuine activity, then SIGKILL-simulate.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                DRRipper.Scheduler.DownloadJob? cur = null;
                while (sw.Elapsed < Budget)
                {
                    cur = await store.GetAsync(job.JobId);
                    if (cur != null && cur.State == DRRipper.Scheduler.JobState.Downloading &&
                        cur.CompletedBytes > 0)
                        break;
                    await Task.Delay(200);
                }
                Assert.NotNull(cur);
                await Task.Delay(2000); // accumulate checkpointed ranges
                sched.AbandonForKillTest();
                try { store.Dispose(); } catch { }

                var store2 = await JobStore.CreateAsync(db);
                using var sched2 = new DownloadScheduler(store2, new SchedulerSettings());
                await sched2.StartAsync();
                sw.Restart();
                DRRipper.Scheduler.DownloadJob? done = null;
                while (sw.Elapsed < Budget)
                {
                    done = await store2.GetAsync(job.JobId);
                    if (done != null && done.IsTerminal) break;
                    await Task.Delay(300);
                }
                Assert.NotNull(done);
                Assert.Equal(DRRipper.Scheduler.JobState.Completed, done!.State);
                fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-INT-05");
                try { await sched2.StopAsync(); } catch { }
                try { store2.Dispose(); } catch { }
                _output.WriteLine("OBSERVATION: 16-conn kill/restart resumed byte-identical.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PERF-INT-06: range mismatch still rejected at 16 connections.
        public async Task T_PERF_INT_06_Mismatch_Rejected()
        {
            TempDir(out var dir);
            try
            {
                await using var server = await TestDownloadServer.StartAsync(new ServerProfile
                {
                    FileSize = 8L * 1024 * 1024,
                    ContentRangeOverride = "bytes 1-8388607/8388608", // wrong start
                });
                using var dl = new ParallelDownloader();
                using var cts = new CancellationTokenSource(Budget);
                var ex = await Record.ExceptionAsync(() => dl.StartAsync(server.FileUrl(), dir, 16, cts.Token));
                Assert.NotNull(ex);
                _output.WriteLine($"OBSERVATION: mismatch rejected at 16 conns: {ex!.GetType().Name}.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PERF-INT-07: clean-EOF shortfall still resumes byte-identical at 16 connections.
        public async Task T_PERF_INT_07_Short_Body_Resumes()
        {
            TempDir(out var dir);
            try
            {
                await using var server = await TestDownloadServer.StartAsync(new ServerProfile
                {
                    FileSize = 12L * 1024 * 1024,
                    Seed = 24,
                    ShortBodyBytes = 1L * 1024 * 1024,
                });
                await DownloadExactAsync(server, dir, 16, "T-PERF-INT-07");
                _output.WriteLine("OBSERVATION: short-body 16-conn transfer resumed byte-identical.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
