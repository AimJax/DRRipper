using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DRRipper.Scheduler;
using DRRipper.UI;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #006 UI scale tests (T-UI-SCALE-*). Headless: view-model load,
    /// filter, sort, and search over 100/1k/10k rows plus managed-memory deltas.
    /// No rendering, no screenshots. Virtualized visuals are verified separately
    /// (STA XAML check + manual pass) since headless layout cannot realize rows.
    /// </summary>
    public sealed class SchedulerUiScaleTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(300);
        private readonly ITestOutputHelper _output;

        public SchedulerUiScaleTests(ITestOutputHelper output) => _output = output;

        private sealed class ScaleHarness : IAsyncDisposable
        {
            public string Dir { get; }
            public JobStore Store { get; private set; } = null!;
            public DownloadScheduler Scheduler { get; private set; } = null!;
            public MainViewModel Vm { get; private set; } = null!;

            private ScaleHarness(string dir) => Dir = dir;

            public static async Task<ScaleHarness> CreateAsync()
            {
                var dir = Path.Combine(Path.GetTempPath(), "DRRipperUiScale", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var h = new ScaleHarness(dir);
                h.Store = await JobStore.CreateAsync(Path.Combine(dir, "queue.db"));
                h.Scheduler = new DownloadScheduler(h.Store, new SchedulerSettings());
                h.Vm = new MainViewModel(h.Scheduler, ImmediateDispatcher.Instance,
                    searchDebounceMs: 0, refreshMs: 1000000);
                await h.Vm.InitializeAsync();
                return h;
            }

            public async ValueTask DisposeAsync()
            {
                try { Vm.Dispose(); } catch { }
                try { await Scheduler.StopAsync(); } catch { }
                try { Scheduler.Dispose(); } catch { }
                try { Store.Dispose(); } catch { }
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            }
        }

        private static DownloadJob MakeJob(int i, string targetDir)
        {
            var states = new[] { JobState.Queued, JobState.Downloading, JobState.Paused, JobState.Completed, JobState.Failed };
            return new DownloadJob
            {
                OriginalUrl = $"https://cdn{i % 37}.example.com/files/package-{i:D5}.bin",
                TargetDirectory = targetDir,
                ResolvedFileName = $"package-{i:D5}.bin",
                QueuePosition = i,
                State = states[i % states.Length],
                CompletedBytes = i * 997L % 1000000,
                TotalBytes = 1000000,
                ConnectionsPerFile = 4,
                HostKey = $"https://cdn{i % 37}.example.com",
            };
        }

        private async Task RunScaleAsync(int count, string name)
        {
            await using var h = await ScaleHarness.CreateAsync();
            var jobs = new List<DownloadJob>(count);
            for (int i = 0; i < count; i++) jobs.Add(MakeJob(i, h.Dir));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memBefore = GC.GetTotalMemory(forceFullCollection: false);

            var sw = Stopwatch.StartNew();
            const int batch = 500;
            for (int o = 0; o < jobs.Count; o += batch)
                await h.Store.AddBatchAsync(jobs.Skip(o).Take(Math.Min(batch, jobs.Count - o)));
            double enqueueS = sw.Elapsed.TotalSeconds;

            sw.Restart();
            foreach (var j in jobs) h.Vm.UpsertJob(j);
            h.Vm.RebuildView(); // burst coalescing: event-driven rebuilds throttle; explicit flush here
            double loadS = sw.Elapsed.TotalSeconds;
            Assert.Equal(count, h.Vm.VisibleJobs.Count);

            sw.Restart();
            h.Vm.Filter = JobFilter.Failed;
            int failed = h.Vm.VisibleJobs.Count;
            double filterS = sw.Elapsed.TotalSeconds;

            sw.Restart();
            h.Vm.Filter = JobFilter.All;
            h.Vm.SortColumn = JobSortColumn.Size;
            h.Vm.SortDescending = true;
            double sortS = sw.Elapsed.TotalSeconds;
            Assert.Equal(count, h.Vm.VisibleJobs.Count);

            sw.Restart();
            h.Vm.SearchText = "package-0004";
            double searchS = sw.Elapsed.TotalSeconds;
            int hits = h.Vm.VisibleJobs.Count;
            Assert.True(hits > 0 && hits < count);

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memAfter = GC.GetTotalMemory(forceFullCollection: false);
            double memMB = (memAfter - memBefore) / 1024.0 / 1024.0;

            _output.WriteLine(
                $"OBSERVATION: {name}: enqueue={enqueueS:F1}s model-load={loadS:F2}s " +
                $"filter={filterS * 1000:F0}ms({failed}) sort={sortS * 1000:F0}ms " +
                $"search={searchS * 1000:F0}ms({hits}) managed-delta={memMB:F1}MB.");

            // Responsiveness bars: generous, environment-tolerant (CI runners are slow).
            Assert.True(loadS < 60, $"{name}: model load too slow ({loadS:F1}s).");
            Assert.True(filterS < 30, $"{name}: filter too slow.");
            Assert.True(sortS < 30, $"{name}: sort too slow.");
            Assert.True(searchS < 30, $"{name}: search too slow.");
            // Memory honesty: lightweight models only (~a few KB/row incl. strings).
            double perRowKB = memMB * 1024 / Math.Max(count, 1);
            Assert.True(perRowKB < 64, $"{name}: {perRowKB:F1} KB/row suggests non-lightweight models.");
        }

        [Fact] // T-UI-SCALE-100
        public async Task T_UI_SCALE_100() => await RunScaleAsync(100, "UI-SCALE-100");

        [Fact] // T-UI-SCALE-1000
        public async Task T_UI_SCALE_1000() => await RunScaleAsync(1000, "UI-SCALE-1000");

        [Fact] // T-UI-SCALE-10000
        public async Task T_UI_SCALE_10000() => await RunScaleAsync(10000, "UI-SCALE-10000");
    }
}
