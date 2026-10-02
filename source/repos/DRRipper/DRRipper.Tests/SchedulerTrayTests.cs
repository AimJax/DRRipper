using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DRRipper.UI;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #006.1 tray + close-controller tests (T-TRAY-01..10). Headless:
    /// CloseController with injected delegates + FakeTrayService. No windows.
    /// </summary>
    public sealed class SchedulerTrayTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

        private sealed class Rig : IDisposable
        {
            public readonly FakeTrayService Tray = new();
            public int ShutdownRuns;
            public int HideRuns;
            public int ShowRuns;
            public AppSettings Settings = AppSettings.Defaults();
            public Func<bool> HasActive = () => false;
            public CloseController Controller = null!;
            public readonly List<Task> Background = new();

            public Rig()
            {
                Rebuild();
            }

            public void Rebuild()
            {
                Controller = new CloseController(
                    hasActiveWork: () => HasActive(),
                    getSettings: () => Settings,
                    tray: Tray,
                    shutdownAsync: () => { Interlocked.Increment(ref ShutdownRuns); return Task.CompletedTask; },
                    hideWindow: () => Interlocked.Increment(ref HideRuns),
                    showWindow: () => Interlocked.Increment(ref ShowRuns));
            }

            public void Dispose() => Tray.Dispose();
        }

        private static async Task AwaitBudget(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, $"Timed out: {what}");
            await task;
        }

        [Fact] // T-TRAY-01: active downloads + X => hide, scheduler untouched.
        public void T_TRAY_01_Active_Close_Hides()
        {
            using var rig = new Rig();
            rig.HasActive = () => true; // default MinimizeToTray
            Assert.True(rig.Controller.RequestClose()); // consumed
            Assert.Equal(ShutdownPhase.Hiding, rig.Controller.Phase);
            Assert.Equal(1, rig.HideRuns);
            Assert.Equal(0, rig.ShutdownRuns); // scheduler never stopped
            Assert.True(rig.Tray.IsVisible);
        }

        [Fact] // T-TRAY-02: idle + ExitWhenIdle => real exit.
        public async Task T_TRAY_02_ExitWhenIdle_Exits()
        {
            using var rig = new Rig();
            rig.Settings.CloseBehavior = CloseButtonBehavior.ExitWhenIdle;
            rig.HasActive = () => false;
            Assert.True(rig.Controller.RequestClose());
            await AwaitBudget(rig.Controller.RequestExitAsync(), "exit");
            Assert.Equal(1, rig.ShutdownRuns);
            Assert.Equal(ShutdownPhase.Exited, rig.Controller.Phase);
            Assert.True(rig.Tray.IsDisposed);
        }

        [Fact] // T-TRAY-03: AlwaysExit exits even with active jobs.
        public async Task T_TRAY_03_AlwaysExit_Exits()
        {
            using var rig = new Rig();
            rig.Settings.CloseBehavior = CloseButtonBehavior.AlwaysExit;
            rig.HasActive = () => true;
            Assert.True(rig.Controller.RequestClose());
            await AwaitBudget(rig.Controller.RequestExitAsync(), "exit");
            Assert.Equal(1, rig.ShutdownRuns);
            Assert.Equal(ShutdownPhase.Exited, rig.Controller.Phase);
        }

        [Fact] // T-TRAY-04: minimize hides without stopping the scheduler.
        public void T_TRAY_04_Minimize_Hides()
        {
            using var rig = new Rig();
            rig.Settings.MinimizeToTray = true;
            Assert.True(rig.Controller.RequestMinimize(minimized: true));
            Assert.Equal(1, rig.HideRuns);
            Assert.Equal(0, rig.ShutdownRuns);
            Assert.True(rig.Tray.IsVisible);
            Assert.False(rig.Controller.RequestMinimize(minimized: false)); // restore is separate
        }

        [Fact] // T-TRAY-05: tray Open restores.
        public void T_TRAY_05_Tray_Open_Restores()
        {
            using var rig = new Rig();
            rig.HasActive = () => true;
            Assert.True(rig.Controller.RequestClose());
            Assert.Equal(ShutdownPhase.Hiding, rig.Controller.Phase);
            rig.Controller.RequestRestore();
            Assert.Equal(ShutdownPhase.Running, rig.Controller.Phase);
            Assert.Equal(1, rig.ShowRuns);
            Assert.Equal(0, rig.ShutdownRuns);
        }

        [Fact] // T-TRAY-06: tray Exit runs the single graceful path exactly once.
        public async Task T_TRAY_06_Tray_Exit_Once()
        {
            using var rig = new Rig();
            await AwaitBudget(rig.Controller.RequestExitAsync(), "exit");
            Assert.Equal(1, rig.ShutdownRuns);
            Assert.Equal(ShutdownPhase.Exited, rig.Controller.Phase);
        }

        [Fact] // T-TRAY-07: repeated Exit never double-disposes / double-stops.
        public async Task T_TRAY_07_Repeated_Exit_Safe()
        {
            using var rig = new Rig();
            var t1 = rig.Controller.RequestExitAsync();
            var t2 = rig.Controller.RequestExitAsync();
            var t3 = rig.Controller.RequestExitAsync();
            await AwaitBudget(Task.WhenAll(t1, t2, t3), "concurrent exits");
            Assert.Equal(1, rig.ShutdownRuns);
            await AwaitBudget(rig.Controller.RequestExitAsync(), "late exit");
            Assert.Equal(1, rig.ShutdownRuns);
            Assert.Equal(1, rig.Tray.DisposeCount);
            Assert.Equal(ShutdownPhase.Exited, rig.Controller.Phase);
            // Close after exit is allowed through (final window close).
            Assert.False(rig.Controller.RequestClose());
        }

        [Fact] // T-TRAY-08: tray Pause/Resume events route through existing VM commands.
        public async Task T_TRAY_08_Pause_Resume_Routed()
        {
            // Real VM + real (unstated) scheduler + FakeTray: menu EVENTS must
            // drive the same commands the window buttons use (no duplicated logic).
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DRRipperTrayRoute",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var store = await Scheduler.JobStore.CreateAsync(System.IO.Path.Combine(dir, "q.db"));
                try
                {
                    var scheduler = new Scheduler.DownloadScheduler(store, new Scheduler.SchedulerSettings());
                    var vm = new MainViewModel(scheduler, ImmediateDispatcher.Instance,
                        searchDebounceMs: 0, refreshMs: 1000000);
                    var tray = new FakeTrayService();
                    using var router = new TrayMenuRouter(vm);
                    try
                    {
                        await vm.InitializeAsync(startScheduler: false, default);
                        router.Attach(tray);
                        tray.RaisePauseAll(); // empty queue: routed no-ops must not throw
                        tray.RaiseResumeAll();
                        await Task.Delay(100);
                        Assert.Equal("Queue ready.", vm.StatusText);
                    }
                    finally
                    {
                        router.Detach();
                        vm.Dispose();
                    }
                    try { await scheduler.StopAsync(); } catch { }
                    try { scheduler.Dispose(); } catch { }
                }
                finally { try { store.Dispose(); } catch { } }
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-TRAY-09: status text throttled, redacted (no URL ever).
        public void T_TRAY_09_Status_Throttled_Redacted()
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Assert.False(TrayStatusFormatter.ShouldUpdate(t0, t0.AddSeconds(1)));
            Assert.True(TrayStatusFormatter.ShouldUpdate(t0, t0.AddSeconds(5)));
            Assert.True(TrayStatusFormatter.ShouldUpdate(t0, t0.AddMinutes(1)));
            string idle = TrayStatusFormatter.FormatTooltip(0, 0);
            Assert.Equal("DRRipper — queue idle", idle);
            string busy = TrayStatusFormatter.FormatTooltip(3, 42.6);
            Assert.Contains("3 active", busy);
            Assert.Contains("42.6", busy);
            Assert.True(busy.Length <= 60, "Tooltip exceeds tray limit.");
            Assert.DoesNotContain("http", busy);
            Assert.DoesNotContain("token", busy);
        }

        [Fact] // T-TRAY-10: tray disposed exactly once on exit.
        public async Task T_TRAY_10_Disposed_On_Exit()
        {
            using var rig = new Rig();
            Assert.False(rig.Tray.IsDisposed);
            await AwaitBudget(rig.Controller.RequestExitAsync(), "exit");
            Assert.True(rig.Tray.IsDisposed);
            Assert.Equal(1, rig.Tray.DisposeCount);
            rig.Tray.Dispose();
            Assert.Equal(1, rig.Tray.DisposeCount);
        }

        [Fact] // §34: 100 hide/restore cycles — single tray, no growth, single dispose.
        public void T_TRAY_LEAK_100_Cycles()
        {
            using var rig = new Rig();
            rig.HasActive = () => true;
            for (int i = 0; i < 100; i++)
            {
                Assert.True(rig.Controller.RequestClose());
                Assert.Equal(ShutdownPhase.Hiding, rig.Controller.Phase);
                rig.Controller.RequestRestore();
                Assert.Equal(ShutdownPhase.Running, rig.Controller.Phase);
            }
            Assert.Equal(100, rig.HideRuns);
            Assert.Equal(100, rig.ShowRuns);
            Assert.Equal(0, rig.ShutdownRuns); // hiding never stops work
            Assert.Equal(100, rig.Tray.ShowCount); // same single instance shown each time
            Assert.Equal(0, rig.Tray.HideCount); // tray stays visible while the window hides
            Assert.False(rig.Tray.IsDisposed);
        }
    }
}
