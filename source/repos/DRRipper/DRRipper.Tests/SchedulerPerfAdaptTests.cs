using DRRipper.Scheduler;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #008 adaptation tests (T-PERF-ADAPT-01..08): the pure
    /// AdaptiveConcurrencyController driven with scripted windows.
    /// Deterministic, no network, no timing.
    /// </summary>
    public sealed class SchedulerPerfAdaptTests
    {
        [Fact] // T-PERF-ADAPT-01: scaling opportunity ramps upward through the ladder.
        public void T_PERF_ADAPT_01_Ramps_Upward()
        {
            var ctl = new AdaptiveConcurrencyController(initial: 4, cap: 32);
            Assert.Equal(4, ctl.Target);
            ctl.Observe(50, 0, 0); // baseline window
            Assert.Equal(8, ctl.Observe(60, 0, 0));   // +20% gain
            Assert.Equal(8, ctl.Observe(75, 0, 0));   // warming grace (new workers establishing)
            Assert.Equal(16, ctl.Observe(95, 0, 0));  // validated + continued gain
            Assert.Equal(16, ctl.Observe(120, 0, 0)); // warming
            Assert.Equal(24, ctl.Observe(150, 0, 0)); // validated + gain
            Assert.Equal(24, ctl.Observe(190, 0, 0)); // warming
            Assert.Equal(32, ctl.Observe(240, 0, 0)); // validated + gain
            Assert.Equal("up-gain", ctl.LastDecision);
            Assert.True(ctl.PeakMBps >= 240);
        }

        [Fact] // T-PERF-ADAPT-02: global-capped server stops ramping (probe + revert + hold).
        public void T_PERF_ADAPT_02_Plateau_Stops_Ramp()
        {
            var ctl = new AdaptiveConcurrencyController(initial: 4, cap: 32);
            ctl.Observe(50, 0, 0);
            // +16% looks like headroom: one legitimate probe upward...
            Assert.Equal(8, ctl.Observe(58, 0, 0));
            // ...warm-up grace, then validation sees halved per-conn: revert...
            Assert.Equal(8, ctl.Observe(58.0, 0, 0));
            Assert.Equal(4, ctl.Observe(58.0, 0, 0));
            Assert.StartsWith("down-", ctl.LastDecision);
            // ...and the decaying block holds with zero upward ratchet.
            int max = 4;
            for (int i = 0; i < 10; i++)
                max = Math.Max(max, ctl.Observe(58.0, 0, 0));
            Assert.True(max <= 8, $"Capped path must not ratchet, reached {max}.");
            Assert.Equal(4, ctl.Target);
            Assert.Contains("hold", ctl.LastDecision);
        }

        [Fact] // T-PERF-ADAPT-03: bad-scaling server (regression) backs off gradually.
        public void T_PERF_ADAPT_03_Regression_Backs_Off()
        {
            var ctl = new AdaptiveConcurrencyController(initial: 16, cap: 32);
            ctl.Observe(100, 0, 0);
            Assert.Equal(24, ctl.Observe(115, 0, 0)); // initial gain first
            Assert.Equal(16, ctl.Observe(90, 0, 0));  // -22%: probe reverted, one rung down
            Assert.StartsWith("down-", ctl.LastDecision);
            Assert.Equal(8, ctl.Observe(70, 0, 0));   // still regressing: another rung
            // Gradual (one rung at a time), never a cliff to 1.
            Assert.True(ctl.Target >= 4);
        }

        [Fact] // T-PERF-ADAPT-04: 429 reduces concurrency immediately.
        public void T_PERF_ADAPT_04_Throttle_Steps_Down()
        {
            var ctl = new AdaptiveConcurrencyController(initial: 24, cap: 32);
            ctl.Observe(200, 0, 0);
            Assert.Equal(16, ctl.Observe(200, 0, 1)); // single 429: one rung down
            Assert.Equal("down-throttle", ctl.LastDecision);
            // Cooldown holds the line against immediate re-ramp (§7).
            Assert.Equal(16, ctl.Observe(220, 0, 0));
            Assert.Equal(16, ctl.Observe(230, 0, 0));
            Assert.Equal(16, ctl.Observe(240, 0, 0));
            // After cooldown, genuine gain ramps again.
            Assert.Equal(24, ctl.Observe(280, 0, 0));
        }

        [Fact] // T-PERF-ADAPT-05: Retry-After-honored throttles behave like 429s.
        public void T_PERF_ADAPT_05_RetryAfter_Backoff()
        {
            var ctl = new AdaptiveConcurrencyController(initial: 16, cap: 32);
            ctl.Observe(100, 0, 0);
            // Throttle count > 0 (from 503 + Retry-After) steps down even at same throughput.
            Assert.Equal(8, ctl.Observe(100, 0, 2));
            // Severe fault storms drop to the floor, never below it.
            var storm = new AdaptiveConcurrencyController(initial: 32, cap: 32);
            storm.Observe(300, 0, 0);
            Assert.Equal(4, storm.Observe(300, 12, 0));
            Assert.True(storm.Target >= 1);
        }

        [Fact] // T-PERF-ADAPT-06: no oscillation under noisy measurements.
        public void T_PERF_ADAPT_06_No_Oscillation()
        {
            var ctl = new AdaptiveConcurrencyController(initial: 8, cap: 32);
            ctl.Observe(100, 0, 0);
            var rnd = new Random(42);
            int last = ctl.Target;
            int ups = 0;
            int downs = 0;
            int maxSeen = last;
            for (int i = 0; i < 40; i++)
            {
                // ±4% noise around a flat line: never a real gain, never a regression.
                double sample = 100 * (1 + (rnd.NextDouble() - 0.5) * 0.08);
                int t = ctl.Observe(sample, 0, 0);
                if (t > last) ups++;
                if (t < last) downs++;
                last = t;
                maxSeen = Math.Max(maxSeen, t);
            }
            // Bounded probing only: decaying blocks keep flaps rare, and the
            // probes never ratchet (validation always fails on flat delivery).
            Assert.True(downs <= 3, $"Too much flapping ({downs} downs) under pure noise.");
            Assert.True(ups <= 3, $"Too much probing ({ups} ups) under pure noise.");
            Assert.True(maxSeen <= 16, $"Probes must not ratchet on flat delivery (reached {maxSeen}).");
            Assert.True(ctl.Target <= 16);
        }

        [Fact] // T-PERF-ADAPT-07: file-size tier affects the initial ceiling.
        public void T_PERF_ADAPT_07_Size_Tiers()
        {
            Assert.Equal(4, SchedulerThroughputPolicy.InitialConcurrency(5L * 1024 * 1024, 32));
            Assert.Equal(8, SchedulerThroughputPolicy.InitialConcurrency(500L * 1024 * 1024, 32));
            Assert.Equal(16, SchedulerThroughputPolicy.InitialConcurrency(5L * 1024 * 1024 * 1024, 32));
            Assert.Equal(8, SchedulerThroughputPolicy.InitialConcurrency(-1, 32)); // unknown size
            Assert.Equal(8, SchedulerThroughputPolicy.InitialConcurrency(5L * 1024 * 1024 * 1024, 8)); // cap respected
            // Ladder steps are gradual, never jumps.
            Assert.Equal(8, SchedulerThroughputPolicy.NextLadderStep(4, 32));
            Assert.Equal(16, SchedulerThroughputPolicy.NextLadderStep(8, 32));
            Assert.Equal(32, SchedulerThroughputPolicy.NextLadderStep(24, 32));
            Assert.Equal(32, SchedulerThroughputPolicy.NextLadderStep(32, 32));
            Assert.Equal(24, SchedulerThroughputPolicy.PrevLadderStep(32));
            Assert.Equal(16, SchedulerThroughputPolicy.PrevLadderStep(24));
        }

        [Fact] // T-PERF-ADAPT-08: Balanced defaults are the conservative pre-#008 values.
        public void T_PERF_ADAPT_08_Balanced_Unchanged()
        {
            var settings = SchedulerSettings.Default;
            Assert.Equal(TransferMode.Balanced, settings.TransferMode);
            Assert.Equal(8, settings.MaxConnectionsPerFile);
            Assert.Equal(
                SchedulerThroughputPolicy.BalancedPerFileConnections,
                DownloadScheduler.DefaultConnections(settings, null));
            Assert.Equal(8, DownloadScheduler.DefaultConnections(settings, null));
            Assert.Equal(16, DownloadScheduler.DefaultConnections(settings, 16));
            Assert.Equal(64, DownloadScheduler.DefaultConnections(settings, 999));
            var max = SchedulerSettings.Default;
            SchedulerThroughputPolicy.ApplyMaximumPreset(max);
            Assert.Equal(32, DownloadScheduler.DefaultConnections(max, null));
        }

        [Fact] // Transfer mode + ceiling persist safely; old files default cleanly (§41).
        public async Task T_PERF_ADAPT_Settings_Persist()
        {
            var dir = Path.Combine(Path.GetTempPath(), "DRRipperPerfSettings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "settings.json");
                var svc = new DRRipper.UI.SettingsService(path);
                var fresh = DRRipper.UI.AppSettings.Defaults();
                Assert.Equal(Scheduler.TransferMode.Balanced, fresh.TransferMode);
                Assert.Equal(8, fresh.MaxConnectionsPerFile);
                fresh.TransferMode = Scheduler.TransferMode.MaximumThroughput;
                fresh.MaxConnectionsPerFile = 32;
                fresh.GlobalConnectionBudget = 32;
                fresh.PerHostConnectionBudget = 32;
                await svc.SaveAsync(fresh);
                var reloaded = await new DRRipper.UI.SettingsService(path).LoadAsync();
                Assert.Equal(Scheduler.TransferMode.MaximumThroughput, reloaded.TransferMode);
                Assert.Equal(32, reloaded.MaxConnectionsPerFile);
                Assert.Equal(32, reloaded.GlobalConnectionBudget);
                // Corrupt/old files fall back safely (no exception, sane defaults).
                await File.WriteAllTextAsync(path, "{\"Version\":1,\"ActiveDownloadLimit\":3}");
                var legacy = await new DRRipper.UI.SettingsService(path).LoadAsync();
                Assert.Equal(Scheduler.TransferMode.Balanced, legacy.TransferMode);
                Assert.Equal(8, legacy.MaxConnectionsPerFile);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // Maximum preset is explicit and complete (§23).
        public void T_PERF_ADAPT_Maximum_Preset()
        {            // Share guard (§12/§53): 1→32, 2→16, 4→8, 8+→8 floor.
            Assert.Equal(32, SchedulerThroughputPolicy.SharedWorkerCap(1));
            Assert.Equal(32, SchedulerThroughputPolicy.SharedWorkerCap(0));
            Assert.Equal(16, SchedulerThroughputPolicy.SharedWorkerCap(2));
            Assert.Equal(10, SchedulerThroughputPolicy.SharedWorkerCap(3));
            Assert.Equal(8, SchedulerThroughputPolicy.SharedWorkerCap(4));
            Assert.Equal(8, SchedulerThroughputPolicy.SharedWorkerCap(8));
            Assert.Equal(8, SchedulerThroughputPolicy.SharedWorkerCap(100));            var settings = SchedulerSettings.Default;
            SchedulerThroughputPolicy.ApplyMaximumPreset(settings);
            Assert.Equal(TransferMode.MaximumThroughput, settings.TransferMode);
            Assert.Equal(32, settings.GlobalConnectionBudget);
            Assert.Equal(32, settings.PerHostConnectionBudget);
            Assert.Equal(32, settings.MaxConnectionsPerFile);
            Assert.True(SchedulerThroughputPolicy.IsMaximum(settings));
            Assert.False(SchedulerThroughputPolicy.IsMaximum(null));
            Assert.False(SchedulerThroughputPolicy.IsMaximum(SchedulerSettings.Default));
        }
    }
}
