using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DRRipper.Scheduler;
using Xunit;
using Xunit.Abstractions;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #005.1 allocator hardening tests (T-BUDGET-HARD-01..09).
    /// Deterministic, gate-driven; every wait is bounded. No network.
    /// </summary>
    public sealed class SchedulerBudgetHardeningTests
    {
        private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(120);
        private readonly ITestOutputHelper _output;

        public SchedulerBudgetHardeningTests(ITestOutputHelper output) => _output = output;

        private static async Task<T> WithBudget<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TestBudget));
            Assert.True(winner == task, $"Timed out ({TestBudget.TotalSeconds:F0}s): {what}");
            return await task;
        }

        private static async Task WithBudget(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TestBudget));
            Assert.True(winner == task, $"Timed out ({TestBudget.TotalSeconds:F0}s): {what}");
            await task;
        }

        private static async Task PollAsync(Func<bool> done, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                bool ok;
                try { ok = done(); } catch { ok = false; }
                if (ok) return;
                await Task.Delay(25);
            }
            Assert.Fail($"Timed out waiting for: {what}");
        }

        [Fact] // T-BUDGET-HARD-01: stable host-entry lifetime under churn
        public async Task T_BUDGET_HARD_01_Stable_Host_Entry()
        {
            using var budget = new ConnectionBudget(globalBudget: 8, perHostBudget: 4);
            const string host = "https://stable.example";
            // Sequential churn.
            for (int i = 0; i < 200; i++)
            {
                using var _ = await WithBudget(budget.AcquireAsync(host), "sequential acquire");
            }
            Assert.Equal(1, budget.HostStateCount);
            // Concurrent churn with arriving waiters.
            var tasks = new List<Task>();
            for (int i = 0; i < 50; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var _ = await budget.AcquireAsync(host);
                    await Task.Delay(5);
                }));
            }
            await WithBudget(Task.WhenAll(tasks), "concurrent churn");
            Assert.Equal(1, budget.HostStateCount);
            Assert.Equal(8, budget.AvailableGlobal);
            Assert.Equal(4, budget.AvailableForHost(host));
            Assert.True(budget.MaxHostObserved(host) <= 4, "per-host cap bypassed");
            _output.WriteLine($"OBSERVATION: 250 acquisitions, exactly one host entry, max observed={budget.MaxHostObserved(host)}.");
        }

        [Fact] // T-BUDGET-HARD-02: per-host cap under 2000-way acquisition race
        public async Task T_BUDGET_HARD_02_Host_Cap_Under_Race()
        {
            const int cap = 3;
            using var budget = new ConnectionBudget(globalBudget: 16, perHostBudget: cap);
            const string host = "https://raced.example";
            int concurrent = 0, maxSeen = 0;
            var gate = new object();
            var tasks = new List<Task>();
            for (int i = 0; i < 2000; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var _ = await budget.AcquireAsync(host);
                    var c = Interlocked.Increment(ref concurrent);
                    lock (gate) maxSeen = Math.Max(maxSeen, c);
                    await Task.Delay(10);
                    Interlocked.Decrement(ref concurrent);
                }));
            }
            await WithBudget(Task.WhenAll(tasks), "2000 racing acquisitions");
            Assert.True(maxSeen <= cap, $"host concurrency {maxSeen} exceeded cap {cap}");
            Assert.True(budget.MaxHostObserved(host) <= cap, "allocator-observed host count exceeded cap");
            Assert.Equal(0, budget.CurrentHostUsed(host));
            _output.WriteLine($"OBSERVATION: 2000 racers, external max={maxSeen}, allocator max={budget.MaxHostObserved(host)}.");
        }

        [Fact] // T-BUDGET-HARD-03: cross-host head-of-line — B progresses while A queued
        public async Task T_BUDGET_HARD_03_No_Head_Of_Line_Blocking()
        {
            using var budget = new ConnectionBudget(globalBudget: 4, perHostBudget: 2);
            const string hostA = "https://a.example";
            const string hostB = "https://b.example";

            // Saturate host A (2 active). Global still has 2 free — nobody holds them waiting.
            var a1 = await WithBudget(budget.AcquireAsync(hostA), "a1");
            var a2 = await WithBudget(budget.AcquireAsync(hostA), "a2");

            // Flood A with 6 more waiters (host-saturated, must queue without pinning global).
            var aWaiters = new List<Task<IDisposable>>();
            for (int i = 0; i < 6; i++)
                aWaiters.Add(budget.AcquireAsync(hostA).ContinueWith(t => (IDisposable)t.GetAwaiter().GetResult()));
            await PollAsync(() => budget.PendingWaiterCount == 6, "6 A-waiters queued");
            // Global must NOT be exhausted by waiters: 2 used of 4.
            Assert.Equal(2, budget.AvailableGlobal);

            // Host B arrives afterward and must begin while A still queued.
            using var b1 = await WithBudget(budget.AcquireAsync(hostB), "B must progress while A queued");
            Assert.True(budget.PendingWaiterCount >= 6, "A waiters must still be queued while B holds its permit");
            Assert.True(budget.MaxHostObserved(hostA) <= 2, "A cap bypassed");

            // Drain: release everything, all A waiters complete.
            a1.Dispose();
            a2.Dispose();
            b1.Dispose();
            var drained = aWaiters.Select(t => t.ContinueWith(async tt =>
            {
                using var _ = await tt;
                await Task.Delay(5);
            }).Unwrap());
            await WithBudget(Task.WhenAll(drained), "A-waiter drain");
            Assert.Equal(0, budget.PendingWaiterCount);
            Assert.Equal(4, budget.AvailableGlobal);
            _output.WriteLine("OBSERVATION: B granted while 6 A-waiters queued; global never pinned by waiters.");
        }

        [Fact] // T-BUDGET-HARD-04: three-host fairness — light C progresses under heavy A/B
        public async Task T_BUDGET_HARD_04_Three_Host_Fairness()
        {
            using var budget = new ConnectionBudget(globalBudget: 4, perHostBudget: 4);
            using var cts = new CancellationTokenSource(TestBudget);
            int churn = 0;
            async Task ChurnAsync(string host, int rounds)
            {
                for (int i = 0; i < rounds; i++)
                {
                    using var _ = await budget.AcquireAsync(host, cts.Token);
                    Interlocked.Increment(ref churn);
                    await Task.Delay(10, cts.Token);
                }
            }
            var heavyA = Task.Run(() => ChurnAsync("https://heavy-a.example", 40));
            var heavyB = Task.Run(() => ChurnAsync("https://heavy-b.example", 40));
            await PollAsync(() => Volatile.Read(ref churn) >= 8, "heavy churn underway");
            // Light C must obtain a permit within a bounded time despite heavy A/B.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cPermit = await WithBudget(budget.AcquireAsync("https://light-c.example", cts.Token), "light host C must progress");
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Light host starved ({sw.Elapsed.TotalSeconds:F1}s).");
            _output.WriteLine($"OBSERVATION: light host served after {sw.Elapsed.TotalSeconds:F2}s under heavy A/B churn.");
            cPermit.Dispose();
            await WithBudget(Task.WhenAll(heavyA, heavyB), "churn drain");
            Assert.Equal(0, budget.CurrentGlobalUsed);
        }

        [Fact] // T-BUDGET-HARD-05: cancellation before grant — 200 queued waiters
        public async Task T_BUDGET_HARD_05_Cancel_Before_Grant()
        {
            using var budget = new ConnectionBudget(globalBudget: 1, perHostBudget: 1);
            using var holder = await WithBudget(budget.AcquireAsync("https://h.example"), "holder");
            var tasks = new List<Task>();
            var sources = new List<CancellationTokenSource>();
            for (int i = 0; i < 200; i++)
            {
                var cts = new CancellationTokenSource();
                sources.Add(cts);
                tasks.Add(Task.Run(async () =>
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.AcquireAsync("https://h.example", cts.Token));
                }));
            }
            await PollAsync(() => budget.PendingWaiterCount == 200, "200 waiters queued");
            foreach (var s in sources) { try { s.Cancel(); } catch { } }
            await WithBudget(Task.WhenAll(tasks), "all cancelled waiters settle");
            foreach (var s in sources) s.Dispose();
            holder.Dispose();
            Assert.Equal(0, budget.CurrentGlobalUsed);
            Assert.Equal(0, budget.PendingWaiterCount);
            Assert.Equal(1, budget.AvailableGlobal);
            // Allocator still functional afterward.
            using var again = await WithBudget(budget.AcquireAsync("https://h.example"), "post-cancel acquire");
            _output.WriteLine("OBSERVATION: 200 pre-grant cancels, zero drift, allocator healthy.");
        }

        [Fact] // T-BUDGET-HARD-06: cancellation/grant race — thousands of races, zero drift
        public async Task T_BUDGET_HARD_06_Cancel_Grant_Race()
        {
            using var budget = new ConnectionBudget(globalBudget: 4, perHostBudget: 4);
            const string host = "https://racy.example";
            int granted = 0, cancelled = 0;
            var tasks = new List<Task>();
            for (int w = 0; w < 64; w++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    for (int r = 0; r < 30; r++)
                    {
                        using var cts = new CancellationTokenSource();
                        var acquire = budget.AcquireAsync(host, cts.Token);
                        await Task.Delay(Random.Shared.Next(0, 3));
                        try { cts.Cancel(); } catch { }
                        try
                        {
                            using var _ = await acquire;
                            Interlocked.Increment(ref granted);
                        }
                        catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); }
                        catch (ObjectDisposedException) { Interlocked.Increment(ref cancelled); }
                    }
                }));
            }
            await WithBudget(Task.WhenAll(tasks), "1920 cancel/grant races");
            Assert.Equal(1920, granted + cancelled);
            Assert.Equal(0, budget.CurrentGlobalUsed);
            Assert.Equal(0, budget.CurrentHostUsed(host));
            Assert.Equal(4, budget.AvailableGlobal);
            _output.WriteLine($"OBSERVATION: 1920 races → granted={granted} cancelled={cancelled}, zero drift.");
        }

        [Fact] // T-BUDGET-HARD-07: Dispose idempotence — capacity returns exactly once
        public void T_BUDGET_HARD_07_Dispose_Idempotent()
        {
            using var budget = new ConnectionBudget(globalBudget: 2, perHostBudget: 2);
            var permit = budget.AcquireAsync("https://h.example").GetAwaiter().GetResult();
            Assert.Equal(1, budget.AvailableGlobal);
            permit.Dispose();
            Assert.Equal(2, budget.AvailableGlobal);
            Assert.Equal(0, budget.CurrentGlobalUsed);
            permit.Dispose();
            permit.Dispose();
            Assert.Equal(2, budget.AvailableGlobal);
            Assert.Equal(0, budget.CurrentGlobalUsed);
            // Fresh acquisition still works after redundant disposes.
            using var again = budget.AcquireAsync("https://h.example").GetAwaiter().GetResult();
            Assert.Equal(1, budget.AvailableGlobal);
        }

        [Fact] // T-BUDGET-HARD-08: disposal with 50 pending waiters completes deterministically
        public async Task T_BUDGET_HARD_08_Dispose_With_Pending()
        {
            var budget = new ConnectionBudget(globalBudget: 2, perHostBudget: 2);
            var holders = new List<IDisposable>
            {
                await WithBudget(budget.AcquireAsync("https://h.example"), "holder 1"),
                await WithBudget(budget.AcquireAsync("https://h.example"), "holder 2"),
            };
            var pending = new List<Task>();
            for (int i = 0; i < 50; i++)
            {
                pending.Add(Task.Run(async () =>
                {
                    var ex = await Record.ExceptionAsync(() => budget.AcquireAsync("https://h.example"));
                    Assert.NotNull(ex); // deterministic failure, never a hang
                }));
            }
            await PollAsync(() => budget.PendingWaiterCount == 50, "50 waiters pending");
            budget.Dispose();
            await WithBudget(Task.WhenAll(pending), "pending waiters settle on dispose");
            // Granted permits remain safely disposable after budget disposal.
            foreach (var h in holders) h.Dispose();
            // Future acquisitions are rejected deterministically.
            await Assert.ThrowsAsync<ObjectDisposedException>(() => budget.AcquireAsync("https://h.example"));
            _output.WriteLine("OBSERVATION: 50 pending waiters failed deterministically on dispose; no hang.");
        }

        [Fact] // T-BUDGET-HARD-09: 10k mixed acquire/release across 8 hosts, caps always hold
        public async Task T_BUDGET_HARD_09_Mixed_Stress_10k()
        {
            using var budget = new ConnectionBudget(globalBudget: 16, perHostBudget: 8);
            var hosts = Enumerable.Range(0, 8).Select(i => $"https://host{i}.example").ToArray();
            int cancels = 0;
            var tasks = new List<Task>();
            for (int w = 0; w < 8; w++)
            {
                int worker = w;
                tasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < 1250; i++)
                    {
                        var host = hosts[(worker + i) % hosts.Length];
                        if (i % 10 == 9)
                        {
                            using var cts = new CancellationTokenSource();
                            var pending = budget.AcquireAsync(host, cts.Token);
                            cts.Cancel();
                            try { using var _ = await pending; }
                            catch (OperationCanceledException) { Interlocked.Increment(ref cancels); }
                        }
                        else
                        {
                            using var _ = await budget.AcquireAsync(host);
                            if ((i & 127) == 0) await Task.Delay(1);
                        }
                    }
                }));
            }
            await WithBudget(Task.WhenAll(tasks), "10k mixed acquire/release");
            Assert.True(budget.MaxGlobalObserved <= 16, $"global {budget.MaxGlobalObserved} > 16");
            foreach (var h in hosts)
                Assert.True(budget.MaxHostObserved(h) <= 8, $"host {h}: {budget.MaxHostObserved(h)} > 8");
            Assert.Equal(0, budget.CurrentGlobalUsed);
            Assert.Equal(0, budget.PendingWaiterCount);
            _output.WriteLine($"OBSERVATION: 10k ops, maxGlobal={budget.MaxGlobalObserved}, cancels={cancels}, zero drift.");
        }

        [Fact] // §18: host-state memory — 10k retained entries stay small
        public async Task T_HOSTMEM_10k_Retention_Small()
        {
            using var budget = new ConnectionBudget(globalBudget: 64, perHostBudget: 8);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(forceFullCollection: false);
            for (int i = 0; i < 10000; i++)
            {
                using var _ = await budget.AcquireAsync($"https://memhost{i}.example");
            }
            Assert.Equal(10000, budget.HostStateCount);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long after = GC.GetTotalMemory(forceFullCollection: false);
            double mb = (after - before) / 1024.0 / 1024.0;
            _output.WriteLine($"OBSERVATION: 10k host entries retain {mb:F2} MB.");
            Assert.True(mb < 16.0, $"Host retention unexpectedly large: {mb:F2} MB for 10k entries.");
        }
    }

    /// <summary>
    /// Ticket #005.1 progress tests (T-PROGRESS-01..05). Temp SQLite each.
    /// </summary>
    public sealed class SchedulerProgressTests
    {
        private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(120);
        private readonly ITestOutputHelper _output;

        public SchedulerProgressTests(ITestOutputHelper output) => _output = output;

        private static string TempDbPath(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "DRRipperProgTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "queue.db");
        }

        private static DownloadJob NewJob(string targetDir)
        {
            return new DownloadJob
            {
                OriginalUrl = "https://example.com/f.bin",
                TargetDirectory = targetDir,
                QueuePosition = 0,
                State = JobState.Downloading,
                TotalBytes = 200L * 1024 * 1024,
                HostKey = "https://example.com",
            };
        }

        private static async Task<T> WithBudget<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TestBudget));
            Assert.True(winner == task, $"Timed out ({TestBudget.TotalSeconds:F0}s): {what}");
            return await task;
        }

        private static async Task WithBudget(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TestBudget));
            Assert.True(winner == task, $"Timed out ({TestBudget.TotalSeconds:F0}s): {what}");
            await task;
        }

        [Fact] // T-PROGRESS-01: delayed smaller update cannot regress a larger one (both orders)
        public async Task T_PROGRESS_01_Out_Of_Order_No_Regression()
        {
            var db = TempDbPath(out var dir);
            try
            {
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);

                // Order A: 100 MB persisted, then a delayed 90 MB arrives later.
                var a = NewJob(target);
                await WithBudget(store.AddAsync(a), "add A");
                await WithBudget(store.UpdateProgressAsync(a.JobId, 100L * 1024 * 1024), "write 100");
                var slow90 = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    await store.UpdateProgressAsync(a.JobId, 90L * 1024 * 1024);
                });
                await WithBudget(slow90, "delayed 90");
                var gotA = await WithBudget(store.GetAsync(a.JobId), "read A");
                Assert.True(gotA!.CompletedBytes >= 100L * 1024 * 1024,
                    $"Regressed to {gotA.CompletedBytes}");

                // Order B: 90 MB persisted, then 100 MB arrives later.
                var b = NewJob(target);
                await WithBudget(store.AddAsync(b), "add B");
                await WithBudget(store.UpdateProgressAsync(b.JobId, 90L * 1024 * 1024), "write 90");
                await WithBudget(store.UpdateProgressAsync(b.JobId, 100L * 1024 * 1024), "write 100");
                var gotB = await WithBudget(store.GetAsync(b.JobId), "read B");
                Assert.Equal(100L * 1024 * 1024, gotB!.CompletedBytes);
                _output.WriteLine("OBSERVATION: both arrival orders converge to the maximum.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PROGRESS-02: 500 shuffled concurrent updates converge to the max
        public async Task T_PROGRESS_02_Shuffled_Converges_To_Max()
        {
            var db = TempDbPath(out var dir);
            try
            {
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                var job = NewJob(target);
                await WithBudget(store.AddAsync(job), "add");
                var rnd = new Random(51);
                var values = Enumerable.Range(0, 500).Select(_ => (long)rnd.Next(0, 1000000)).ToList();
                long expected = values.Max();
                values = values.OrderBy(_ => rnd.Next()).ToList(); // shuffle arrival
                var tasks = values.Select(v => Task.Run(() => store.UpdateProgressAsync(job.JobId, v)));
                await WithBudget(Task.WhenAll(tasks), "500 concurrent updates");
                var got = await WithBudget(store.GetAsync(job.JobId), "read");
                Assert.Equal(expected, got!.CompletedBytes);
                _output.WriteLine($"OBSERVATION: 500 shuffled updates converged to max={expected}.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PROGRESS-03: known TotalBytes never regresses to unknown/smaller
        public async Task T_PROGRESS_03_Total_Never_Regresses()
        {
            var db = TempDbPath(out var dir);
            try
            {
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                var job = NewJob(target);
                job.TotalBytes = -1; // unknown at enqueue
                await WithBudget(store.AddAsync(job), "add");
                await WithBudget(store.UpdateProgressAsync(job.JobId, 500, 1000), "learn total=1000");
                await WithBudget(store.UpdateProgressAsync(job.JobId, 600, null), "stale unknown total");
                var g1 = await WithBudget(store.GetAsync(job.JobId), "read 1");
                Assert.Equal(1000, g1!.TotalBytes);
                Assert.Equal(600, g1.CompletedBytes);
                await WithBudget(store.UpdateProgressAsync(job.JobId, 700, 500), "stale smaller total");
                var g2 = await WithBudget(store.GetAsync(job.JobId), "read 2");
                Assert.Equal(1000, g2!.TotalBytes);
                Assert.Equal(700, g2.CompletedBytes);
                _output.WriteLine("OBSERVATION: total pinned at 1000 across unknown/smaller writes.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PROGRESS-04: late progress cannot corrupt terminal jobs
        public async Task T_PROGRESS_04_Terminal_Immune_To_Late_Progress()
        {
            var db = TempDbPath(out var dir);
            try
            {
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                foreach (var terminal in new[] { JobState.Completed, JobState.Failed, JobState.Cancelled })
                {
                    var job = NewJob(target);
                    await WithBudget(store.AddAsync(job), "add");
                    await WithBudget(store.UpdateStateAsync(job.JobId, terminal,
                        failureReason: terminal == JobState.Failed ? "boom" : null,
                        completedBytes: 1000, totalBytes: 1000,
                        completedUtc: DateTimeOffset.UtcNow), $"to {terminal}");
                    var before = await WithBudget(store.GetAsync(job.JobId), "read before");
                    // Stale racing progress arrives after terminalization.
                    await WithBudget(store.UpdateProgressAsync(job.JobId, 10, 100), $"late progress on {terminal}");
                    var after = await WithBudget(store.GetAsync(job.JobId), "read after");
                    Assert.Equal(terminal, after!.State);
                    Assert.Equal(before!.CompletedBytes, after.CompletedBytes);
                    Assert.Equal(before.TotalBytes, after.TotalBytes);
                    Assert.Equal(before.FailureReason, after.FailureReason);
                    Assert.Equal(before.CompletedUtc, after.CompletedUtc);
                }
                _output.WriteLine("OBSERVATION: Completed/Failed/Cancelled rows immune to late progress.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-PROGRESS-05: concurrent progress across jobs stays isolated
        public async Task T_PROGRESS_05_Multi_Job_Isolation()
        {
            var db = TempDbPath(out var dir);
            try
            {
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                var ids = new List<Guid>();
                for (int j = 0; j < 8; j++)
                {
                    var job = NewJob(target);
                    job.QueuePosition = j;
                    await WithBudget(store.AddAsync(job), $"add {j}");
                    ids.Add(job.JobId);
                }
                var rnd = new Random(52);
                var tasks = new List<Task>();
                var maxPerJob = new Dictionary<Guid, long>();
                for (int j = 0; j < 8; j++)
                {
                    long base_ = j * 1000000L;
                    var values = Enumerable.Range(0, 100).Select(_ => base_ + rnd.Next(0, 900000)).ToList();
                    maxPerJob[ids[j]] = values.Max();
                    var id = ids[j];
                    foreach (var v in values)
                        tasks.Add(Task.Run(() => store.UpdateProgressAsync(id, v)));
                }
                await WithBudget(Task.WhenAll(tasks), "800 cross-job updates");
                foreach (var id in ids)
                {
                    var got = await WithBudget(store.GetAsync(id), "read");
                    Assert.Equal(maxPerJob[id], got!.CompletedBytes);
                }
                _output.WriteLine("OBSERVATION: 8 jobs × 100 updates converged to per-job maxima.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
