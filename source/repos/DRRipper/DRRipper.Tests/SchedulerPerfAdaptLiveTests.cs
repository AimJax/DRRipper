using System.IO;
using DRRipper.Scheduler;
using DRRipper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #008 live adaptation tests: the controller engages inside real
    /// transfers — ramp observed on a per-connection-capped server (§28) and
    /// backoff observed under 429 pressure (§31). Polls the telemetry snapshot;
    /// both jobs must still complete byte-identical.
    /// </summary>
    [Collection("SchedulerSerial")]
    public sealed class SchedulerPerfAdaptLiveTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(300);
        private readonly ITestOutputHelper _output;

        public SchedulerPerfAdaptLiveTests(ITestOutputHelper output) => _output = output;

        private sealed class Rig : IAsyncDisposable
        {
            public string Dir { get; }
            public string DlDir { get; }
            public JobStore Store { get; private set; } = null!;
            public DownloadScheduler Scheduler { get; private set; } = null!;

            private Rig(string dir, string dl) { Dir = dir; DlDir = dl; }

            public static async Task<Rig> CreateAsync(SchedulerSettings settings)
            {
                var dir = Path.Combine(Path.GetTempPath(), "DRRipperPerfAdapt", Guid.NewGuid().ToString("N"));
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

        [Fact] // T-PERF-ADAPT-09: ramp engages live on a per-connection-capped server (§28).
        public async Task T_PERF_ADAPT_09_Ramp_Engages_Live()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 300L * 1024 * 1024,
                Seed = 51,
                BytesPerSecond = 2L * 1024 * 1024, // per-connection pace
            });
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 32);
            int peakTarget = 0;
            string decisions = string.Empty;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DownloadJob? done = null;
            while (sw.Elapsed < Budget)
            {
                var snap = rig.Scheduler.TryGetAdaptiveSnapshot(job.JobId);
                if (snap.Active)
                {
                    peakTarget = Math.Max(peakTarget, snap.Target);
                    if (!decisions.Contains(snap.Decision))
                        decisions += snap.Decision + ";";
                }
                var cur = await rig.Store.GetAsync(job.JobId);
                if (cur != null && cur.IsTerminal) { done = cur; break; }
                await Task.Delay(500);
            }
            Assert.NotNull(done);
            Assert.Equal(JobState.Completed, done!.State);
            fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-ADAPT-09");
            _output.WriteLine($"OBSERVATION: capped server ramped to target={peakTarget} [{decisions}].");
            Assert.True(peakTarget >= 16, $"Expected live ramp to >= 16, peaked at {peakTarget}.");
        }

        [Fact] // T-PERF-ADAPT-10: 429 pressure visibly backs the controller off (§31).
        public async Task T_PERF_ADAPT_10_Throttle_Backs_Off_Live()
        {
            var profile = new ServerProfile
            {
                FileSize = 120L * 1024 * 1024,
                Seed = 52,
                BytesPerSecond = 4L * 1024 * 1024,
                RetryAfterSeconds = 1,
            };
            // Persistent 429 on the first 8 MB segment: every attempt there throbs.
            profile.RangeFaults.Add(new RangeFault(0, 8L * 1024 * 1024 - 1, 429));
            await using var fx = await DownloadFixture.CreateAsync(profile);
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 32);
            bool sawDown = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DownloadJob? done = null;
            while (sw.Elapsed < Budget)
            {
                var snap = rig.Scheduler.TryGetAdaptiveSnapshot(job.JobId);
                if (snap.Active && (snap.Decision.StartsWith("down-") || snap.Decision == "at-floor"))
                    sawDown = true;
                var cur = await rig.Store.GetAsync(job.JobId);
                if (cur != null && cur.IsTerminal) { done = cur; break; }
                await Task.Delay(500);
            }
            // The 429'd first segment exhausts bounded retries: the job fails
            // LOUDLY (never corrupt, never infinite) — and the controller must
            // have backed off along the way.
            Assert.NotNull(done);
            Assert.True(done!.IsTerminal, $"Ended {done.State}");
            _output.WriteLine($"OBSERVATION: 429 storm ended {done.State}, backoff observed={sawDown}.");
            Assert.True(sawDown, "Controller never backed off under 429 pressure.");
            if (done.State == JobState.Completed)
                fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-ADAPT-10");
            else
                Assert.Equal(JobState.Failed, done.State);
        }

        [Fact] // T-PERF-ADAPT-11: bad-scaling server stays bounded, still completes (§30).
        public async Task T_PERF_ADAPT_11_Bad_Scaling_Bounded()
        {
            await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
            {
                FileSize = 120L * 1024 * 1024,
                Seed = 53,
                DegradeAfterConnections = 6,
                DegradeAddedLatency = TimeSpan.FromMilliseconds(1500),
            });
            await using var rig = await Rig.CreateAsync(MaxSettings());
            var job = await rig.Scheduler.EnqueueAsync(fx.Url, rig.DlDir, connectionsPerFile: 32);
            int peakTarget = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DownloadJob? done = null;
            while (sw.Elapsed < Budget)
            {
                var snap = rig.Scheduler.TryGetAdaptiveSnapshot(job.JobId);
                if (snap.Active)
                    peakTarget = Math.Max(peakTarget, snap.Target);
                var cur = await rig.Store.GetAsync(job.JobId);
                if (cur != null && cur.IsTerminal) { done = cur; break; }
                await Task.Delay(500);
            }
            Assert.NotNull(done);
            Assert.Equal(JobState.Completed, done!.State);
            fx.AssertFileHash(done.ResolvedFinalPath!, fx.ExpectedHash, "T-PERF-ADAPT-11");
            _output.WriteLine($"OBSERVATION: degrading server peaked at target={peakTarget}, completed exact.");
            Assert.True(peakTarget <= 24, $"Degrading path must stay bounded, peaked at {peakTarget}.");
        }
    }
}
