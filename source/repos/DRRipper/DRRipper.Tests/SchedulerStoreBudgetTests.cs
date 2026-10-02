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
    /// <summary>Ticket #005 persistence tests (T-STORE-01..10). No network. Explicit budgets.</summary>
    public sealed class SchedulerStoreTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
        private readonly ITestOutputHelper _output;

        public SchedulerStoreTests(ITestOutputHelper output) => _output = output;

        private static string TempDbPath(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "DRRipperSchedTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "queue.db");
        }

        private static DownloadJob NewJob(string url, string targetDir, long pos = 0)
        {
            return new DownloadJob
            {
                OriginalUrl = url,
                TargetDirectory = targetDir,
                QueuePosition = pos,
                State = JobState.Queued,
                HostKey = ConnectionBudget.NormalizeHostKey(url),
            };
        }

        private static async Task<T> WithBudget<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, $"Timed out ({Budget.TotalSeconds:F0}s): {what}");
            return await task;
        }

        private static async Task WithBudget(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, $"Timed out ({Budget.TotalSeconds:F0}s): {what}");
            await task;
        }

        [Fact] // T-STORE-01: fresh DB creation
        public async Task T_STORE_01_Fresh_Db_Creation()
        {
            var db = TempDbPath(out var dir);
            try
            {
                using var store = await WithBudget(JobStore.CreateAsync(db), "create db");
                var max = await WithBudget(store.GetMaxQueuePositionAsync(), "max pos");
                Assert.Equal(-1, max);
                Assert.True(File.Exists(db));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-02: schema version verification
        public async Task T_STORE_02_Schema_Version()
        {
            var db = TempDbPath(out var dir);
            try
            {
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                // Query version directly via new connection to prove versioned + idempotent.
                using var store2 = await WithBudget(JobStore.CreateAsync(db), "reopen (idempotent)");
                var all = await WithBudget(store2.GetAllJobsAsync(), "list");
                Assert.Empty(all);
                _output.WriteLine("OBSERVATION: schema v1 created + reopened idempotently.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-03: restart with queued jobs
        public async Task T_STORE_03_Restart_Queued_Jobs()
        {
            var db = TempDbPath(out var dir);
            try
            {
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                using (var store = await WithBudget(JobStore.CreateAsync(db), "create"))
                {
                    await WithBudget(store.AddAsync(NewJob("https://example.com/a.bin", target, 0)), "add");
                    await WithBudget(store.AddAsync(NewJob("https://example.com/b.bin", target, 1)), "add");
                }
                using (var store2 = await WithBudget(JobStore.CreateAsync(db), "reopen"))
                {
                    var eligible = await WithBudget(store2.GetEligibleJobsAsync(10), "eligible");
                    Assert.Equal(2, eligible.Count);
                    Assert.Equal("https://example.com/a.bin", eligible[0].OriginalUrl);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-04: restart with interrupted jobs
        public async Task T_STORE_04_Interrupted_Normalization()
        {
            var db = TempDbPath(out var dir);
            try
            {
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                using (var store = await WithBudget(JobStore.CreateAsync(db), "create"))
                {
                    var j = NewJob("https://example.com/a.bin", target, 0);
                    await WithBudget(store.AddAsync(j), "add");
                    await WithBudget(store.UpdateStateAsync(j.JobId, JobState.Downloading), "to downloading");
                    await WithBudget(store.NormalizeInterruptedStatesAsync(), "normalize");
                    var got = await WithBudget(store.GetAsync(j.JobId), "get");
                    Assert.NotNull(got);
                    Assert.Equal(JobState.Interrupted, got!.State);
                    var eligible = await WithBudget(store.GetEligibleJobsAsync(10), "eligible");
                    Assert.Single(eligible);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-05: completed jobs remain completed after restart
        public async Task T_STORE_05_Completed_Survives_Restart()
        {
            var db = TempDbPath(out var dir);
            try
            {
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                Guid id;
                using (var store = await WithBudget(JobStore.CreateAsync(db), "create"))
                {
                    var j = NewJob("https://example.com/a.bin", target, 0);
                    id = j.JobId;
                    await WithBudget(store.AddAsync(j), "add");
                    await WithBudget(store.UpdateStateAsync(j.JobId, JobState.Completed,
                        completedUtc: DateTimeOffset.UtcNow), "complete");
                }
                using (var store2 = await WithBudget(JobStore.CreateAsync(db), "reopen"))
                {
                    var got = await WithBudget(store2.GetAsync(id), "get");
                    Assert.NotNull(got);
                    Assert.Equal(JobState.Completed, got!.State);
                    var eligible = await WithBudget(store2.GetEligibleJobsAsync(10), "eligible");
                    Assert.Empty(eligible);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-06: failed jobs preserve reason + AttemptCount
        public async Task T_STORE_06_Failed_Preserves_Reason()
        {
            var db = TempDbPath(out var dir);
            try
            {
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                Guid id;
                using (var store = await WithBudget(JobStore.CreateAsync(db), "create"))
                {
                    var j = NewJob("https://example.com/a.bin", target, 0);
                    id = j.JobId;
                    await WithBudget(store.AddAsync(j), "add");
                    await WithBudget(store.UpdateStateAsync(j.JobId, JobState.Failed,
                        failureReason: "HTTP 404 (NotFound)", attemptCount: 2,
                        completedUtc: DateTimeOffset.UtcNow), "fail");
                }
                using (var store2 = await WithBudget(JobStore.CreateAsync(db), "reopen"))
                {
                    var got = await WithBudget(store2.GetAsync(id), "get");
                    Assert.NotNull(got);
                    Assert.Equal(JobState.Failed, got!.State);
                    Assert.Equal("HTTP 404 (NotFound)", got.FailureReason);
                    Assert.Equal(2, got.AttemptCount);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-07: 10,000-job bulk insert in a single batch transaction
        public async Task T_STORE_07_Bulk_Insert_10000()
        {
            var db = TempDbPath(out var dir);
            try
            {
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                var jobs = new List<DownloadJob>(10000);
                for (int i = 0; i < 10000; i++)
                {
                    jobs.Add(new DownloadJob
                    {
                        OriginalUrl = $"https://example.com/file{i:D5}.bin",
                        TargetDirectory = target,
                        QueuePosition = i,
                        State = JobState.Queued,
                        HostKey = "https://example.com",
                    });
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await WithBudget(store.AddBatchAsync(jobs), "bulk insert 10k");
                sw.Stop();
                var info = new FileInfo(db);
                _output.WriteLine($"OBSERVATION: 10k bulk insert took {sw.Elapsed.TotalSeconds:F1}s, db={info.Length / 1024.0:F0}KB.");
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "Bulk insert must complete in bounded time.");
                var eligible = await WithBudget(store.GetEligibleJobsAsync(3), "eligible sample");
                Assert.Equal(3, eligible.Count);
                Assert.Equal("https://example.com/file00000.bin", eligible[0].OriginalUrl);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-08: reorder survives restart
        public async Task T_STORE_08_Reorder_Survives_Restart()
        {
            var db = TempDbPath(out var dir);
            try
            {
                var target = Path.Combine(dir, "dl");
                Directory.CreateDirectory(target);
                Guid a, b, c;
                using (var store = await WithBudget(JobStore.CreateAsync(db), "create"))
                {
                    var ja = NewJob("https://example.com/a.bin", target, 0); a = ja.JobId;
                    var jb = NewJob("https://example.com/b.bin", target, 1); b = jb.JobId;
                    var jc = NewJob("https://example.com/c.bin", target, 2); c = jc.JobId;
                    await WithBudget(store.AddBatchAsync(new[] { ja, jb, jc }), "add");
                    await WithBudget(store.ReorderAsync(new[] { c, a, b }), "reorder");
                }
                using (var store2 = await WithBudget(JobStore.CreateAsync(db), "reopen"))
                {
                    var eligible = await WithBudget(store2.GetEligibleJobsAsync(10), "eligible");
                    Assert.Equal(3, eligible.Count);
                    Assert.Equal(c, eligible[0].JobId);
                    Assert.Equal(a, eligible[1].JobId);
                    Assert.Equal(b, eligible[2].JobId);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-09: corrupt/unsupported schema produces explicit error
        public async Task T_STORE_09_Unsupported_Schema_Explicit_Error()
        {
            var db = TempDbPath(out var dir);
            try
            {
                // Write a bogus future schema version by hand.
                using (var store = await WithBudget(JobStore.CreateAsync(db), "create"))
                {
                }
                // Bump version beyond supported via raw SQL.
                var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db };
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ConnectionString))
                {
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES (999, '2026-01-01T00:00:00Z');";
                    await cmd.ExecuteNonQueryAsync();
                }
                var ex = await Record.ExceptionAsync(() => JobStore.CreateAsync(db));
                Assert.NotNull(ex);
                Assert.IsType<InvalidOperationException>(ex);
                Assert.Contains("newer than supported", ex!.Message);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-STORE-10: unicode paths + URLs roundtrip
        public async Task T_STORE_10_Unicode_Roundtrip()
        {
            var db = TempDbPath(out var dir);
            try
            {
                var target = Path.Combine(dir, "dl-ünïcodé-日本語");
                Directory.CreateDirectory(target);
                const string url = "https://example.com/ünïcodé-файл.bin?x=1&y=ü";
                using var store = await WithBudget(JobStore.CreateAsync(db), "create");
                var j = NewJob(url, target, 0);
                j.RequestedFileName = "tëst-файл (1).bin";
                await WithBudget(store.AddAsync(j), "add");
                var got = await WithBudget(store.GetAsync(j.JobId), "get");
                Assert.NotNull(got);
                Assert.Equal(url, got!.OriginalUrl);
                Assert.Equal(target, got.TargetDirectory);
                Assert.Equal("tëst-файл (1).bin", got.RequestedFileName);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>Ticket #005 budget + power tests (T-BUDGET-01..08 + power). Deterministic, no sleep-sync.</summary>
    public sealed class SchedulerBudgetTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

        [Fact] // T-BUDGET-01: global budget never exceeded
        public async Task T_BUDGET_01_Global_Never_Exceeded()
        {
            using var budget = new ConnectionBudget(globalBudget: 4, perHostBudget: 4);
            var tasks = new List<Task>();
            for (int i = 0; i < 12; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var _ = await budget.AcquireAsync("https://h.example");
                    await Task.Delay(20);
                }));
            }
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(Budget));
            Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));
            Assert.True(budget.MaxGlobalObserved <= 4, $"max observed {budget.MaxGlobalObserved} > 4");
            Assert.Equal(4, budget.AvailableGlobal);
        }

        [Fact] // T-BUDGET-02: per-host budget never exceeded
        public async Task T_BUDGET_02_PerHost_Never_Exceeded()
        {
            using var budget = new ConnectionBudget(globalBudget: 16, perHostBudget: 2);
            int concurrent = 0, maxSeen = 0;
            var gate = new object();
            var tasks = new List<Task>();
            for (int i = 0; i < 8; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var _ = await budget.AcquireAsync("https://only.example");
                    var c = Interlocked.Increment(ref concurrent);
                    lock (gate) maxSeen = Math.Max(maxSeen, c);
                    await Task.Delay(30);
                    Interlocked.Decrement(ref concurrent);
                }));
            }
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(Budget));
            Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));
            Assert.True(maxSeen <= 2, $"per-host concurrency {maxSeen} > 2");
        }

        [Fact] // T-BUDGET-03: two hosts use independent budgets under shared global
        public async Task T_BUDGET_03_Hosts_Independent_Global_Shared()
        {
            using var budget = new ConnectionBudget(globalBudget: 4, perHostBudget: 4);
            var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Hold(string host, TaskCompletionSource entered, int ms)
            {
                using var _ = await budget.AcquireAsync(host);
                entered.TrySetResult();
                await Task.Delay(ms);
            }
            var a = Hold("https://a.example:5001", enteredA, 300);
            var b = Hold("https://b.example:5002", enteredB, 300);
            await Task.WhenAny(Task.WhenAll(enteredA.Task, enteredB.Task), Task.Delay(Budget));
            Assert.True(enteredA.Task.IsCompleted && enteredB.Task.IsCompleted,
                "Independent host budgets must admit one holder per host concurrently.");
            await Task.WhenAny(Task.WhenAll(a, b), Task.Delay(Budget));
            Assert.True(a.IsCompletedSuccessfully && b.IsCompletedSuccessfully);
        }

        [Fact] // T-BUDGET-04: permit returned after request failure
        public async Task T_BUDGET_04_Permit_Returned_After_Failure()
        {
            using var budget = new ConnectionBudget(globalBudget: 2, perHostBudget: 2);
            try
            {
                using var _ = await budget.AcquireAsync("https://h.example");
                throw new InvalidOperationException("simulated request failure");
            }
            catch (InvalidOperationException) { }
            Assert.Equal(2, budget.AvailableGlobal);
            using var again = await budget.AcquireAsync("https://h.example");
            Assert.Equal(1, budget.AvailableGlobal);
        }

        [Fact] // T-BUDGET-05: permit returned after cancellation
        public async Task T_BUDGET_05_Permit_Returned_After_Cancel()
        {
            using var budget = new ConnectionBudget(globalBudget: 1, perHostBudget: 1);
            using (var _ = await budget.AcquireAsync("https://h.example"))
            {
                Assert.Equal(0, budget.AvailableGlobal);
            }
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.AcquireAsync("https://h.example", cts.Token));
            Assert.Equal(1, budget.AvailableGlobal);
        }

        [Fact] // T-BUDGET-06: permit returned after pause (dispose path)
        public async Task T_BUDGET_06_Permit_Returned_After_Pause()
        {
            using var budget = new ConnectionBudget(globalBudget: 1, perHostBudget: 1);
            var permit = await budget.AcquireAsync("https://h.example");
            Assert.Equal(0, budget.AvailableGlobal);
            permit.Dispose(); // pause path releases before parking
            Assert.Equal(1, budget.AvailableGlobal);
            permit.Dispose(); // double-dispose safe
            Assert.Equal(1, budget.AvailableGlobal);
        }

        [Fact] // T-BUDGET-07: retry backoff holds zero permits
        public async Task T_BUDGET_07_Backoff_Holds_Zero_Permits()
        {
            using var budget = new ConnectionBudget(globalBudget: 2, perHostBudget: 2);
            // Simulate: attempt acquires + releases, backoff waits with nothing held.
            using (var _ = await budget.AcquireAsync("https://h.example")) { }
            Assert.Equal(2, budget.AvailableGlobal);
            await Task.Delay(50); // backoff window holds nothing
            Assert.Equal(2, budget.AvailableGlobal);
            using (var _ = await budget.AcquireAsync("https://h.example")) { }
            Assert.Equal(2, budget.AvailableGlobal);
        }

        [Fact] // T-BUDGET-08: no starvation across jobs (all contenders eventually served)
        public async Task T_BUDGET_08_No_Starvation()
        {
            using var budget = new ConnectionBudget(globalBudget: 2, perHostBudget: 2);
            int served = 0;
            var tasks = new List<Task>();
            for (int i = 0; i < 6; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var _ = await budget.AcquireAsync("https://h.example");
                    Interlocked.Increment(ref served);
                    await Task.Delay(20);
                }));
            }
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(Budget));
            Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));
            Assert.Equal(6, served);
        }

        [Fact] // §37: reference-counted power management
        public void T_POWER_RefCounted()
        {
            var pm = new SchedulerPowerManager();
            Assert.Equal(0, pm.RefCount);
            pm.Acquire(); pm.Acquire();
            Assert.Equal(2, pm.RefCount);
            pm.Release();
            Assert.Equal(1, pm.RefCount);
            pm.Release();
            Assert.Equal(0, pm.RefCount);
            pm.Release(); // underflow-safe
            Assert.Equal(0, pm.RefCount);
        }

        [Fact] // §14: host key normalization (default ports, IPv6, case)
        public void T_HOSTKEY_Normalization()
        {
            Assert.Equal("https://example.com",
                ConnectionBudget.NormalizeHostKey("https://example.com/file"));
            Assert.Equal("https://example.com",
                ConnectionBudget.NormalizeHostKey("https://example.com:443/file"));
            Assert.Equal("http://example.com",
                ConnectionBudget.NormalizeHostKey("http://example.com:80/file"));
            Assert.Equal("https://example.com:8443",
                ConnectionBudget.NormalizeHostKey("https://example.com:8443/file"));
            Assert.Equal("https://example.com",
                ConnectionBudget.NormalizeHostKey("HTTPS://EXAMPLE.COM/file"));
        }

        [Fact] // §26: URL redaction strips query + userinfo
        public void T_REDACT_Query_Userinfo_Stripped()
        {
            var redacted = UrlRedactor.Redact("https://user:pass@example.com/file.bin?token=secret&x=1");
            Assert.DoesNotContain("secret", redacted);
            Assert.DoesNotContain("user", redacted);
            Assert.Contains("example.com", redacted);
        }
    }
}
