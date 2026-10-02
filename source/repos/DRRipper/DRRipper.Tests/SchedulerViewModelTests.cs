using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DRRipper.Scheduler;
using DRRipper.UI;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>Headless MainViewModel harness: real scheduler + temp DB, inline dispatcher, stub shell/clipboard.</summary>
    internal sealed class VmHarness : IAsyncDisposable
    {
        public string Dir { get; }
        public string DlDir { get; }
        public JobStore Store { get; private set; } = null!;
        public DownloadScheduler Scheduler { get; private set; } = null!;
        public MainViewModel Vm { get; private set; } = null!;
        public List<string> Clipboard { get; } = new();

        private VmHarness(string dir) { Dir = dir; DlDir = Path.Combine(dir, "dl"); }

        public static async Task<VmHarness> CreateAsync(
            Func<AddDownloadsViewModel, bool?>? showAdd = null,
            int refreshMs = 1000000, int searchDebounceMs = 0,
            bool startScheduler = false)
        {
            var dir = Path.Combine(Path.GetTempPath(), "DRRipperVmTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var h = new VmHarness(dir);
            Directory.CreateDirectory(h.DlDir);
            h.Store = await JobStore.CreateAsync(Path.Combine(dir, "queue.db"));
            h.Scheduler = new DownloadScheduler(h.Store, new SchedulerSettings
            {
                ActiveDownloadLimit = 3,
                GlobalConnectionBudget = 16,
                PerHostConnectionBudget = 8,
            });
            var self = h;
            var clipboard = new StubClipboard(self.Clipboard);
            h.Vm = new MainViewModel(h.Scheduler, ImmediateDispatcher.Instance,
                shell: new StubShell(), clipboard: clipboard,
                showAddDialog: showAdd ?? (_ => null),
                showSettingsDialog: _ => null,
                refreshMs: refreshMs, searchDebounceMs: searchDebounceMs);
            // Pure view/model tests: leave the admission loop stopped so
            // researcher-supplied snapshots never churn underneath assertions.
            await h.Vm.InitializeAsync(startScheduler, default);
            return h;
        }

        private sealed class StubShell : IShellService
        {
            public bool CanOpenFile(DownloadJobViewModel job) => false;
            public bool TryOpenFile(DownloadJobViewModel job, out string? error) { error = "stub"; return false; }
            public bool TryOpenFolder(DownloadJobViewModel job, out string? error) { error = "stub"; return false; }
        }

        private sealed class StubClipboard(List<string> log) : IClipboardService
        {
            public void SetText(string text) => log.Add(text);
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

    internal static class VmJobFactory
    {
        private static int _seq;

        public static DownloadJob Make(
            JobState state = JobState.Queued,
            long completed = 0, long total = 1000,
            string? name = null, string? url = null, string targetDir = "C:\\dl")
        {
            int n = Interlocked.Increment(ref _seq);
            var jobUrl = url ?? $"https://example.com/file{n}.bin";
            return new DownloadJob
            {
                OriginalUrl = jobUrl,
                TargetDirectory = targetDir,
                ResolvedFileName = name ?? $"file{n}.bin",
                QueuePosition = n,
                State = state,
                CompletedBytes = completed,
                TotalBytes = total,
                ConnectionsPerFile = 4,
                HostKey = ConnectionBudget.NormalizeHostKey(jobUrl),
            };
        }
    }

    /// <summary>Ticket #006 view-model tests (T-UI-VM-01..10). Headless, no WPF visuals.</summary>
    public sealed class SchedulerViewModelTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

        private static async Task<T> WithBudget<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, $"Timed out: {what}");
            return await task;
        }

        private static async Task AwaitBudget(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, $"Timed out: {what}");
            await task;
        }

        [Fact] // T-UI-VM-01: job state maps to display status (+glyph, non-color cues)
        public void T_UI_VM_01_State_Mapping()
        {
            var expectations = new (JobState State, string Text, string Glyph)[]
            {
                (JobState.Queued, "Queued", "○"),
                (JobState.Downloading, "Downloading", "▼"),
                (JobState.Pausing, "Pausing…", "◌"),
                (JobState.Paused, "Paused", "❚❚"),
                (JobState.Retrying, "Retrying…", "↻"),
                (JobState.Completed, "Completed", "✓"),
                (JobState.Failed, "Failed", "✕"),
                (JobState.Cancelled, "Cancelled", "⊘"),
                (JobState.Interrupted, "Interrupted", "⚠"),
            };
            foreach (var (state, text, glyph) in expectations)
            {
                var job = VmJobFactory.Make(state: state);
                if (state == JobState.Failed) job.FailureReason = "HTTP 404 (NotFound)";
                var (t, g) = DownloadJobViewModel.MapStatus(job);
                Assert.StartsWith(text, t);
                Assert.Equal(glyph, g);
                var vm = new DownloadJobViewModel(job.JobId);
                vm.Apply(job);
                Assert.Equal(state, vm.State);
                Assert.StartsWith(text, vm.StatusText);
                Assert.Equal(glyph, vm.StatusGlyph);
                if (state == JobState.Failed) Assert.Contains("404", vm.StatusText);
            }
        }

        [Fact] // T-UI-VM-02: progress formatting
        public void T_UI_VM_02_Progress_Formatting()
        {
            Assert.Equal("50.0%", Formatting.FormatProgress(500, 1000));
            Assert.Equal("100.0%", Formatting.FormatProgress(1000, 1000));
            Assert.Equal("100.0%", Formatting.FormatProgress(1500, 1000)); // clamped
            Assert.Equal("—", Formatting.FormatProgress(0, 0));
            Assert.Equal("—", Formatting.FormatProgress(0, -1));
            Assert.Equal("1.5 TB", Formatting.FormatBytes(3L * 1024 * 1024 * 1024 * 1024 / 2));
            Assert.Equal("2.5 GB", Formatting.FormatBytes((long)(2.5 * 1024 * 1024 * 1024)));
            Assert.Equal("10 MB", Formatting.FormatBytes(10L * 1024 * 1024));
            Assert.Equal("4 KB", Formatting.FormatBytes(4096));
            Assert.Equal("7 B", Formatting.FormatBytes(7));
            Assert.Equal("?", Formatting.FormatBytes(-1));
        }

        [Fact] // T-UI-VM-03: speed formatting
        public void T_UI_VM_03_Speed_Formatting()
        {
            Assert.Equal("2.5 MB/s", Formatting.FormatRate(2.5 * 1024 * 1024));
            Assert.Equal("512 KB/s", Formatting.FormatRate(512 * 1024));
            Assert.Equal("1.5 GB/s", Formatting.FormatRate(1.5 * 1024 * 1024 * 1024));
            Assert.Equal("0 B/s", Formatting.FormatRate(0));
            Assert.Equal("—", Formatting.FormatRate(-1));
            Assert.Equal("—", Formatting.FormatRate(double.NaN));
        }

        [Fact] // T-UI-VM-04: ETA calculation rules
        public void T_UI_VM_04_Eta_Rules()
        {
            Assert.Equal("—", Formatting.FormatEta(null, 0, 1024 * 1024)); // unknown size
            Assert.Equal("—", Formatting.FormatEta(1000, 100, 0)); // stalled
            Assert.Equal("—", Formatting.FormatEta(1000, 100, 500)); // <1KB/s trickle
            Assert.Equal("45s", Formatting.FormatEta(46080, 0, 1024)); // 45KB @1KB/s
            Assert.Equal("0s", Formatting.FormatEta(1000, 1000, 100));
            Assert.Equal("2m 0s", Formatting.FormatEta(120 * 1024, 0, 1024));
            Assert.Equal("1h 0m", Formatting.FormatEta(3600 * 1024, 0, 1024));
            Assert.Equal("1d 0h", Formatting.FormatEta(86400 * 1024L, 0, 1024));
            Assert.Equal("—", Formatting.FormatEta(long.MaxValue, 0, 1)); // absurd guard
        }

        [Fact] // T-UI-VM-05: command CanExecute rules
        public async Task T_UI_VM_05_Command_Rules()
        {
            await using var h = await VmHarness.CreateAsync();
            var queued = VmJobFactory.Make(JobState.Queued, name: "q.bin");
            var active = VmJobFactory.Make(JobState.Downloading, completed: 10, name: "a.bin");
            var failed = VmJobFactory.Make(JobState.Failed, name: "f.bin");
            await AwaitBudget(h.Store.AddAsync(queued), "add");
            await AwaitBudget(h.Store.AddAsync(active), "add");
            await AwaitBudget(h.Store.AddAsync(failed), "add");
            h.Vm.UpsertJob(queued);
            h.Vm.UpsertJob(active);
            h.Vm.UpsertJob(failed);

            // Nothing selected: bulk commands disabled, Add enabled.
            h.Vm.SyncSelection(Array.Empty<Guid>());
            Assert.True(h.Vm.AddCommand.CanExecute(null));
            Assert.False(h.Vm.PauseSelectedCommand.CanExecute(null));
            Assert.False(h.Vm.RetrySelectedCommand.CanExecute(null));
            Assert.False(h.Vm.RemoveSelectedCommand.CanExecute(null));

            // Active selected: pause/cancel enabled, retry disabled.
            h.Vm.SyncSelection(new[] { active.JobId });
            Assert.True(h.Vm.PauseSelectedCommand.CanExecute(null));
            Assert.True(h.Vm.CancelSelectedCommand.CanExecute(null));
            Assert.False(h.Vm.RetrySelectedCommand.CanExecute(null));

            // Failed selected: retry enabled, pause disabled.
            h.Vm.SyncSelection(new[] { failed.JobId });
            Assert.False(h.Vm.PauseSelectedCommand.CanExecute(null));
            Assert.True(h.Vm.RetrySelectedCommand.CanExecute(null));
            Assert.True(h.Vm.RemoveSelectedCommand.CanExecute(null));
        }

        [Fact] // T-UI-VM-06: filter mapping (incl. Interrupted bucketed with Queued)
        public async Task T_UI_VM_06_Filter_Mapping()
        {
            await using var h = await VmHarness.CreateAsync();
            var jobs = new[]
            {
                VmJobFactory.Make(JobState.Queued, name: "q.bin"),
                VmJobFactory.Make(JobState.Interrupted, name: "i.bin"),
                VmJobFactory.Make(JobState.Downloading, completed: 5, name: "d.bin"),
                VmJobFactory.Make(JobState.Paused, completed: 5, name: "p.bin"),
                VmJobFactory.Make(JobState.Completed, completed: 1000, name: "c.bin"),
                VmJobFactory.Make(JobState.Failed, name: "f.bin"),
                VmJobFactory.Make(JobState.Cancelled, name: "x.bin"),
            };
            foreach (var j in jobs)
            {
                await AwaitBudget(h.Store.AddAsync(j), "add");
                h.Vm.UpsertJob(j);
            }
            h.Vm.RebuildView(); // flush event-coalesced rebuilds (bulk-setup contract)
            async Task<int> CountAsync(JobFilter f)
            {
                h.Vm.Filter = f;
                await Task.Delay(10);
                return h.Vm.VisibleJobs.Count;
            }
            Assert.Equal(7, await CountAsync(JobFilter.All));
            Assert.Equal(1, await CountAsync(JobFilter.Active));
            Assert.Equal(2, await CountAsync(JobFilter.Queued)); // Queued + Interrupted
            Assert.Equal(1, await CountAsync(JobFilter.Paused));
            Assert.Equal(1, await CountAsync(JobFilter.Completed));
            Assert.Equal(1, await CountAsync(JobFilter.Failed));
            Assert.Equal(1, await CountAsync(JobFilter.Cancelled));
        }

        [Fact] // T-UI-VM-07: search matching across filename/URL/host/status/path
        public async Task T_UI_VM_07_Search_Matching()
        {
            await using var h = await VmHarness.CreateAsync(searchDebounceMs: 0);
            var a = VmJobFactory.Make(JobState.Downloading, completed: 5, name: "ubuntu-24.04.iso", url: "https://mirror.example.com/ubuntu-24.04.iso");
            var b = VmJobFactory.Make(JobState.Failed, name: "report.pdf", url: "https://files.other.org/report.pdf");
            b.FailureReason = "HTTP 404";
            foreach (var j in new[] { a, b })
            {
                await AwaitBudget(h.Store.AddAsync(j), "add");
                h.Vm.UpsertJob(j);
            }
            async Task<int> SearchAsync(string q)
            {
                h.Vm.SearchText = q;
                await Task.Delay(50); // debounce is 0 in this harness; allow dispatcher-free propagation
                return h.Vm.VisibleJobs.Count;
            }
            Assert.Equal(1, await SearchAsync("ubuntu"));
            Assert.Equal(1, await SearchAsync("other.org"));
            Assert.Equal(1, await SearchAsync("404"));
            Assert.Equal(1, await SearchAsync("report"));
            Assert.Equal(2, await SearchAsync(""));
            Assert.Equal(0, await SearchAsync("no-such-thing-zzz"));
        }

        [Fact] // T-UI-VM-08: bulk-operation isolation (one bad op doesn't stop others)
        public async Task T_UI_VM_08_Bulk_Isolation()
        {
            await using var h = await VmHarness.CreateAsync();
            var failed = VmJobFactory.Make(JobState.Failed, name: "f.bin");
            var active = VmJobFactory.Make(JobState.Downloading, completed: 5, name: "a.bin");
            await AwaitBudget(h.Store.AddAsync(failed), "add");
            await AwaitBudget(h.Store.AddAsync(active), "add");
            h.Vm.UpsertJob(failed);
            h.Vm.UpsertJob(active);
            // Retry both: Failed succeeds, Downloading throws (not retryable) — recorded, not fatal.
            h.Vm.SyncSelection(new[] { failed.JobId, active.JobId });
            var retry = (AsyncRelayCommand)h.Vm.RetrySelectedCommand;
            Exception? fault = null;
            retry.Faulted += ex => fault = ex;
            retry.Execute(null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < Budget && h.Vm.StatusText == string.Empty) await Task.Delay(50);
            var afterFailed = await WithBudget(h.Store.GetAsync(failed.JobId), "read");
            var afterActive = await WithBudget(h.Store.GetAsync(active.JobId), "read");
            // Retry accepted: Failed job left Failed state (scheduler may already
            // be downloading it again — pump loop is running in this harness).
            Assert.True(afterFailed!.State == JobState.Queued || afterFailed.State == JobState.Downloading,
                $"retried job should leave Failed, got {afterFailed.State}");
            Assert.Equal(JobState.Downloading, afterActive!.State); // untouched by sibling error
            Assert.NotEmpty(h.Vm.LastBulkErrors); // error recorded
            Assert.Contains("1 failed", h.Vm.StatusText);
            Assert.Null(fault); // command itself never throws out
        }

        [Fact] // T-UI-VM-09: error redaction/display formatting
        public void T_UI_VM_09_Error_Formatting()
        {
            Assert.Equal("Failed", Formatting.FormatError(null));
            Assert.Equal("Failed", Formatting.FormatError("   "));
            Assert.Equal("HTTP 404 (NotFound)", Formatting.FormatError("HTTP 404 (NotFound)"));
            string signed = "Download failed for https://cdn.example.com/f.bin?token=secret-abc&x=1";
            string shown = Formatting.FormatError(signed);
            Assert.DoesNotContain("secret-abc", shown);
            string longMsg = new string('x', 500);
            Assert.True(Formatting.FormatError(longMsg).Length <= 163);
        }

        [Fact] // T-UI-VM-10: disposal detaches scheduler handlers (no post-close callbacks)
        public async Task T_UI_VM_10_Dispose_Detaches()
        {
            // NOTE: scheduler loop RUNS here on purpose: queued jobs churn
            // underneath while the VM is dead — any leaked handler would observe it.
            await using var h = await VmHarness.CreateAsync(startScheduler: true);
            var job = VmJobFactory.Make(JobState.Queued, name: "q.bin");
            await AwaitBudget(h.Store.AddAsync(job), "add");
            h.Vm.UpsertJob(job);
            h.Vm.RebuildView(); // flush (setup races the 250 ms burst-coalescing window)
            Assert.Single(h.Vm.VisibleJobs);
            h.Vm.Dispose();
            // Scheduler keeps working; the dead VM must not observe or throw.
            var job2 = VmJobFactory.Make(JobState.Queued, name: "w.bin");
            await AwaitBudget(h.Store.AddAsync(job2), "add");
            await Task.Delay(2000); // let admission + events fire against detached handlers
            Assert.Single(h.Vm.VisibleJobs); // unchanged after detach
        }
    }
}
