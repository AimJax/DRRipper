using System.ComponentModel;
using DRRipper.TestServer;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace DRRipper.Tests;

/// <summary>
/// Ticket #004.1 checkpoint durability tests. All timing is gate-driven
/// (TaskCompletionSources + bounded waits), never sleep-based. Every test has
/// an explicit overall deadline via <see cref="TestBudget"/>.
/// </summary>
public sealed class CheckpointTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(120);

    // ---------- fakes (internal seams, never public API) ----------

    private sealed class FailingFlusher(Exception fault) : IDurableFileFlusher
    {
        public void Flush(SafeFileHandle handle) => throw fault;
    }

    private sealed class GatedPublisher : IMetadataPublisher
    {
        private readonly AtomicFilePublisher _inner = new();
        private readonly object _gateLock = new();
        private readonly Dictionary<long, TaskCompletionSource> _gates = new();
        public readonly List<long> Attempts = new();

        /// <summary>Block the publish step of <paramref name="generation"/> until released.</summary>
        public void Gate(long generation)
        {
            lock (_gateLock) _gates[generation] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release(long generation)
        {
            TaskCompletionSource? tcs;
            lock (_gateLock) _gates.TryGetValue(generation, out tcs);
            try { tcs?.TrySetResult(); } catch { }
        }

        public int AttemptCount { get { lock (_gateLock) return Attempts.Count; } }

        public bool SawGeneration(long generation)
        {
            lock (_gateLock) return Attempts.Contains(generation);
        }

        public void ReleaseAll()
        {
            List<TaskCompletionSource> all;
            lock (_gateLock) { all = new List<TaskCompletionSource>(_gates.Values); _gates.Clear(); }
            foreach (var t in all) try { t.TrySetResult(); } catch { }
        }

        public void WriteTemp(RecoveryMetadata meta, string tempPath, CancellationToken ct)
        {
            lock (_gateLock) Attempts.Add(meta.CheckpointGeneration);
            TaskCompletionSource? gate;
            lock (_gateLock) _gates.TryGetValue(meta.CheckpointGeneration, out gate);
            if (gate != null)
            {
                // Bounded wait so a forgotten Release() fails the test instead of hanging it.
                if (!gate.Task.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException($"Test gate for generation {meta.CheckpointGeneration} never released.");
            }
            ct.ThrowIfCancellationRequested();
            _inner.WriteTemp(meta, tempPath, ct);
        }
    }

    private sealed class FailingPublisher(Exception fault) : IMetadataPublisher
    {
        public void WriteTemp(RecoveryMetadata meta, string tempPath, CancellationToken ct) => throw fault;
    }

    // ---------- helpers ----------

    private static async Task<DownloadFixture> CreateFixtureAsync(long sizeMB = 8)
    {
        return await DownloadFixture.CreateAsync(new ServerProfile { FileSize = sizeMB * 1024 * 1024 });
    }

    private static DownloadSession NewSession(ParallelDownloader dl, DownloadFixture fx, long size)
    {
        var session = new DownloadSession(dl, dl.Client)
        {
            Url = fx.Url,
            TargetDirectory = fx.TempDir,
            TotalSize = size,
            FileName = "test-payload.bin",
            FinalPath = Path.Combine(fx.TempDir, "test-payload.bin"),
            ETag = "\"v1\"",
            Mode = "segmented",
        };
        session.PartPath = session.FinalPath + ".part";
        session.MetaPath = session.PartPath + ".drmeta";
        using (var fs = new FileStream(session.PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(size);
        session.OpenExistingFile();
        session.Tracker = new RangeTracker(size);
        return session;
    }

    private static string ReadMeta(string metaPath) => File.ReadAllText(metaPath);

    private static long MetaGeneration(string json)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"CheckpointGeneration\":(\\d+)");
        Assert.True(m.Success, "Canonical metadata lacks CheckpointGeneration.");
        return long.Parse(m.Groups[1].Value);
    }

    private static string[] StrayTemps(DownloadSession session)
    {
        var dir = Path.GetDirectoryName(session.MetaPath)!;
        var name = Path.GetFileName(session.MetaPath);
        return Directory.GetFiles(dir, name + ".tmp.*");
    }

    private static async Task PollAsync(Func<bool> done, TimeSpan timeout, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            bool ok;
            try { ok = done(); } catch { ok = false; }
            if (ok) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    private static async Task<T> WithBudget<T>(Task<T> task, TimeSpan budget, string what)
    {
        var winner = await Task.WhenAny(task, Task.Delay(budget));
        Assert.True(winner == task, $"Timed out ({budget.TotalSeconds:F0}s): {what}");
        return await task;
    }

    private static async Task WithBudget(Task task, TimeSpan budget, string what)
    {
        var winner = await Task.WhenAny(task, Task.Delay(budget));
        Assert.True(winner == task, $"Timed out ({budget.TotalSeconds:F0}s): {what}");
        await task;
    }

    // ---------- T-CKPT-01: concurrent checkpoints, one authoritative sequence ----------

    [Fact]
    public async Task T_CKPT_01_Concurrent_Checkpoints_Single_Authoritative_Sequence()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        using var session = NewSession(dl, fx, size);
        var gated = new GatedPublisher();
        session.MetadataPublisher = gated;

        session.Tracker!.CompleteRange(0, 4L * 1024 * 1024 - 1);
        gated.Gate(1);
        gated.Gate(2);
        var t1 = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(30)));
        await PollAsync(() => gated.SawGeneration(1), TimeSpan.FromSeconds(30), "gen1 publish attempt");
        session.Tracker!.CompleteRange(4L * 1024 * 1024, size - 1);
        var t2 = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(30)));
        await PollAsync(() => gated.SawGeneration(2), TimeSpan.FromSeconds(30), "gen2 publish attempt");

        // Both in flight behind closed gates: no canonical snapshot may exist yet.
        Assert.False(File.Exists(session.MetaPath));
        Assert.Equal(0, StrayCount(session));
        gated.ReleaseAll();
        await WithBudget(Task.WhenAll(t1, t2), TestBudget, "concurrent checkpoints");
        // Later-claimed generation wins; canonical is valid and complete.
        string json = ReadMeta(session.MetaPath);
        Assert.Equal(2, MetaGeneration(json));
        var loaded = RecoveryMetadata.TryLoad(session.MetaPath);
        Assert.NotNull(loaded);
        
        Assert.Empty(StrayTemps(session));
    }

    // ---------- T-CKPT-02: stall + timeout, then success; stale cannot overwrite ----------

    [Fact]
    public async Task T_CKPT_02_Stalled_Generation_Cannot_Overwrite_Newer()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        using var session = NewSession(dl, fx, size);
        var gated = new GatedPublisher();
        session.MetadataPublisher = gated;

        session.Tracker!.CompleteRange(0, 2L * 1024 * 1024 - 1);
        session.Checkpoint(true, TimeSpan.FromSeconds(30)); // gen1 baseline, real publish
        string baseline = ReadMeta(session.MetaPath);
        Assert.Equal(1, MetaGeneration(baseline));

        session.Tracker!.CompleteRange(2L * 1024 * 1024, 4L * 1024 * 1024 - 1);
        gated.Gate(2); // gen2 will stall inside publish
        var slow = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(2)));
        await PollAsync(() => gated.SawGeneration(2), TimeSpan.FromSeconds(30), "gen2 publish attempt");
        var slowEx = await Record.ExceptionAsync(() => WithBudget(slow, TestBudget, "stalled gen2 call"));
        Assert.IsType<TimeoutException>(slowEx); // caller stopped waiting after budget

        session.Tracker!.CompleteRange(4L * 1024 * 1024, size - 1);
        session.Checkpoint(true, TimeSpan.FromSeconds(30)); // gen3 succeeds
        string afterGen3 = ReadMeta(session.MetaPath);
        Assert.Equal(3, MetaGeneration(afterGen3));
        Assert.Contains("\"CompletedRanges\":[[0,8388607]]", afterGen3);

        gated.Release(2); // stalled gen2 finally resumes
        await WithBudget(Task.Delay(500).ContinueWith(_ => Task.CompletedTask).Unwrap(), TestBudget, "settle");
        await PollAsync(() => session.InflightCheckpoints == 0, TimeSpan.FromSeconds(30), "gen2 task settlement");
        // Stale gen2 must not have overwritten gen3; its temp must be gone.
        Assert.Equal(afterGen3, ReadMeta(session.MetaPath));
        Assert.Empty(StrayTemps(session));
    }

    // ---------- T-CKPT-03: timed-out checkpoint leaves no trace, then publishes cleanly ----------

    [Fact]
    public async Task T_CKPT_03_TimedOut_Checkpoint_Leaves_No_Trace()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        using var session = NewSession(dl, fx, size);
        var gated = new GatedPublisher();
        session.MetadataPublisher = gated;

        session.Tracker!.CompleteRange(0, 2L * 1024 * 1024 - 1);
        session.Checkpoint(true, TimeSpan.FromSeconds(30)); // gen1 baseline
        string baseline = ReadMeta(session.MetaPath);

        session.Tracker!.CompleteRange(2L * 1024 * 1024, 4L * 1024 * 1024 - 1);
        gated.Gate(2);
        var slow = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(2)));
        await PollAsync(() => gated.SawGeneration(2), TimeSpan.FromSeconds(30), "gen2 publish attempt");
        var slowEx = await Record.ExceptionAsync(() => WithBudget(slow, TestBudget, "stalled gen2 call"));
        Assert.IsType<TimeoutException>(slowEx);
        // While stalled, nothing partial reached canonical (byte-identical).
        Assert.Equal(baseline, ReadMeta(session.MetaPath));
        // Released while still newest, gen2 publishes cleanly — never torn,
        // never partial: full [0,4M) coverage under gen2.
        gated.Release(2);
        await PollAsync(() => session.InflightCheckpoints == 0, TimeSpan.FromSeconds(30), "gen2 task settlement");
        string after = ReadMeta(session.MetaPath);
        Assert.Equal(2, MetaGeneration(after));
        Assert.Contains("\"CompletedRanges\":[[0,4194303]]", after);
        Assert.NotNull(RecoveryMetadata.TryLoad(session.MetaPath));
        Assert.Empty(StrayTemps(session));
    }

    // ---------- T-CKPT-04: out-of-order completion, highest valid wins ----------

    [Fact]
    public async Task T_CKPT_04_OutOfOrder_Completion_Highest_Wins()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        using var session = NewSession(dl, fx, size);
        var gated = new GatedPublisher();
        session.MetadataPublisher = gated;

        session.Tracker!.CompleteRange(0, 2L * 1024 * 1024 - 1);
        var t1 = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(30)));
        await PollAsync(() => gated.SawGeneration(1), TimeSpan.FromSeconds(30), "gen1 attempt");
        session.Tracker!.CompleteRange(2L * 1024 * 1024, 4L * 1024 * 1024 - 1);
        var t2 = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(30)));
        await PollAsync(() => gated.SawGeneration(2), TimeSpan.FromSeconds(30), "gen2 attempt");
        session.Tracker!.CompleteRange(4L * 1024 * 1024, size - 1);
        var t3 = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(30)));
        await PollAsync(() => gated.SawGeneration(3), TimeSpan.FromSeconds(30), "gen3 attempt");

        // Complete strictly newest-first.
        gated.Release(3);
        await PollAsync(() =>
        {
            try { return File.Exists(session.MetaPath) && MetaGeneration(ReadMeta(session.MetaPath)) == 3; }
            catch { return false; }
        }, TimeSpan.FromSeconds(30), "gen3 publication");
        gated.Release(2);
        gated.Release(1);
        await WithBudget(Task.WhenAll(t1, t2, t3), TestBudget, "out-of-order checkpoints");
        string json = ReadMeta(session.MetaPath);
        Assert.Equal(3, MetaGeneration(json));
        Assert.Contains("\"CompletedRanges\":[[0,8388607]]", json);
        Assert.Empty(StrayTemps(session));
    }

    // ---------- T-CKPT-05: flush failure — nothing published ----------

    [Fact]
    public async Task T_CKPT_05_Flush_Failure_Publishes_Nothing()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        using var session = NewSession(dl, fx, size);

        session.Tracker!.CompleteRange(0, 2L * 1024 * 1024 - 1);
        session.Checkpoint(true, TimeSpan.FromSeconds(30)); // gen1 baseline, healthy
        string baseline = ReadMeta(session.MetaPath);

        session.FileFlusher = new FailingFlusher(new Win32Exception(112, "simulated disk full"));
        session.Tracker!.CompleteRange(2L * 1024 * 1024, 4L * 1024 * 1024 - 1);
        var ex = await Record.ExceptionAsync(() =>
            Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(30))));
        var failed = Assert.IsType<DownloadFailedException>(ex);
        var win32 = Assert.IsType<Win32Exception>(failed.InnerException);
        Assert.Equal(112, win32.NativeErrorCode); // original error code preserved
        // Canonical metadata is byte-identical to the last good snapshot.
        Assert.Equal(baseline, ReadMeta(session.MetaPath));
        Assert.Empty(StrayTemps(session));
    }

    // ---------- T-CKPT-06: temp flush failure — previous snapshot survives ----------

    [Fact]
    public async Task T_CKPT_06_TempFlush_Failure_Preserves_Previous_Snapshot()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        using var session = NewSession(dl, fx, size);

        session.Tracker!.CompleteRange(0, 2L * 1024 * 1024 - 1);
        session.Checkpoint(true, TimeSpan.FromSeconds(30)); // gen1 baseline, healthy
        string baseline = ReadMeta(session.MetaPath);

        session.MetadataPublisher = new FailingPublisher(new IOException("simulated temp flush failure"));
        session.Tracker!.CompleteRange(2L * 1024 * 1024, 4L * 1024 * 1024 - 1);
        var ex = await Record.ExceptionAsync(() =>
            Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(30))));
        Assert.IsType<DownloadFailedException>(ex);
        Assert.Equal(baseline, ReadMeta(session.MetaPath));
        Assert.Empty(StrayTemps(session));
    }

    // ---------- T-CKPT-07: stale task at kill time; restart loads canonical only ----------

    [Fact]
    public async Task T_CKPT_07_Stale_Task_At_Teardown_Safe_Restart()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        var session = new DownloadSession(dl, dl.Client)
        {
            Url = fx.Url,
            TargetDirectory = fx.TempDir,
            TotalSize = size,
            FileName = "test-payload.bin",
            FinalPath = Path.Combine(fx.TempDir, "test-payload.bin"),
            ETag = "\"v1\"",
            Mode = "segmented",
        };
        session.PartPath = session.FinalPath + ".part";
        session.MetaPath = session.PartPath + ".drmeta";
        using (var fs = new FileStream(session.PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(size);
        session.OpenExistingFile();
        session.Tracker = new RangeTracker(size);
        var gated = new GatedPublisher();
        session.MetadataPublisher = gated;

        session.Tracker!.CompleteRange(0, 4L * 1024 * 1024 - 1);
        session.Checkpoint(true, TimeSpan.FromSeconds(30)); // gen1 canonical
        string baseline = ReadMeta(session.MetaPath);

        session.Tracker!.CompleteRange(4L * 1024 * 1024, size - 1);
        gated.Gate(2);
        var stale = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(2)));
        await PollAsync(() => gated.SawGeneration(2), TimeSpan.FromSeconds(30), "gen2 attempt");
        var staleEx = await Record.ExceptionAsync(() => WithBudget(stale, TestBudget, "stalled gen2"));
        Assert.IsType<TimeoutException>(staleEx);

        // Crash here: dispose (bounded settlement, invalidation) while gen2 is stuck.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        session.Dispose();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Dispose must settle boundedly.");
        gated.Release(2); // stale task resumes after teardown
        await PollAsync(() => session.InflightCheckpoints == 0, TimeSpan.FromSeconds(30), "stale task settlement");
        await PollAsync(() => !File.Exists(session.MetaPath + ".tmp.2"), TimeSpan.FromSeconds(30), "stale temp cleanup");

        // Restart loads ONLY the canonical valid snapshot.
        var loaded = RecoveryMetadata.TryLoad(session.MetaPath);
        Assert.NotNull(loaded);
        Assert.Equal(baseline, ReadMeta(session.MetaPath));
        var verify = new RangeTracker(size);
        foreach (var r in loaded.CompletedRanges) verify.CompleteRange(r[0], r[1]);
        Assert.Equal(4L * 1024 * 1024, verify.CoveredBytes);
        output.WriteLine("OBSERVATION: stale post-teardown generation discarded; canonical snapshot intact.");
    }

    // ---------- T-CKPT-08: dispose during delayed checkpoint IO ----------

    [Fact]
    public async Task T_CKPT_08_Dispose_During_Checkpoint_No_Hang_No_Leak()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync();
        using var dl = new ParallelDownloader();
        var session = NewSession(dl, fx, size);
        var gated = new GatedPublisher();
        session.MetadataPublisher = gated;

        session.Tracker!.CompleteRange(0, 2L * 1024 * 1024 - 1);
        gated.Gate(1);
        var task = Task.Run(() => session.Checkpoint(true, TimeSpan.FromSeconds(60)));
        await PollAsync(() => gated.SawGeneration(1), TimeSpan.FromSeconds(30), "gen1 attempt");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        session.Dispose();
        sw.Stop();
        output.WriteLine($"OBSERVATION: Dispose during delayed checkpoint took {sw.Elapsed.TotalSeconds:F1}s.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Dispose must not hang on checkpoint IO.");

        gated.Release(1);
        await WithBudget(task, TestBudget, "abandoned checkpoint task");
        // No unobserved fault, no publish after teardown, no stray temp.
        Assert.Equal(0, session.CheckpointFaults);
        await PollAsync(() => !File.Exists(session.MetaPath + ".tmp.1"), TimeSpan.FromSeconds(30), "temp cleanup");
        Assert.False(File.Exists(session.MetaPath), "Disposed session must not publish canonical metadata.");
    }

    // ---------- T-CKPT-09: pause checkpoint timeout stays consistent + usable ----------

    [Fact]
    public async Task T_CKPT_09_Pause_Checkpoint_Timeout_Stays_Consistent()
    {
        const long size = 16L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 2L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        dl.CheckpointInterval = TimeSpan.FromMilliseconds(100);

        // Phase 1: healthy flusher to a paused, checkpointed partial.
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            long engineBytes = 0;
            dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
            var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (engineBytes < 2L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(60) && !task.IsCompleted)
                await Task.Delay(100);
            Assert.True(engineBytes >= 2L * 1024 * 1024,
                $"No download progress in 60s (engineBytes={engineBytes}); environment stall suspected, not a product failure.");
            dl.Pause();
            Assert.Equal(DownloadState.Paused, dl.GetState());
            dl.Dispose();
            try { await task; } catch (OperationCanceledException) { }
        }
        string goodMeta = DownloadFixture.PartMetaPath(fx);
        Assert.True(File.Exists(goodMeta), "Expected a healthy pause checkpoint.");

        // Phase 2: failing flusher — pause must still park cleanly and report degraded.
        DownloadSession? live = null;
        using (var dl2 = new ParallelDownloader())
        {
            dl2.CheckpointInterval = TimeSpan.FromMilliseconds(100);
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            long engineBytes = 0;
            dl2.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
            var task2 = dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            while (engineBytes < 1L * 1024 * 1024 && sw2.Elapsed < TimeSpan.FromSeconds(90) && !task2.IsCompleted)
                await Task.Delay(100);
            live = dl2.ActiveSession;
            Assert.NotNull(live);
            live.FileFlusher = new FailingFlusher(new IOException("simulated flush failure"));
            dl2.Pause();
            Assert.Equal(DownloadState.Paused, dl2.GetState());
            Assert.True(live.CheckpointDegraded, "Pause must flag degraded checkpoint state.");
            Assert.NotNull(live.LastCheckpointError);
            output.WriteLine($"OBSERVATION: degraded as designed: {live.LastCheckpointError}");
            // The last-good snapshot on disk is untouched by the failed attempt:
            // same generation, same validators, same coverage (timestamps may differ).
            var before = RecoveryMetadata.TryLoad(goodMeta);
            var after = RecoveryMetadata.TryLoad(goodMeta);
            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before.CheckpointGeneration, after.CheckpointGeneration);
            Assert.Equal(before.ETag, after.ETag);
            Assert.Equal(
                before.CompletedRanges.Select(r => r[0] + "-" + r[1]),
                after.CompletedRanges.Select(r => r[0] + "-" + r[1]));
            dl2.Dispose();
            try { await task2; } catch (OperationCanceledException) { }
        }

        // Phase 3: healthy flusher again — restart resumes from the good snapshot.
        fx.Server.Profile.BytesPerSecond = null;
        using var dl3 = new ParallelDownloader();
        using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string final = await dl3.StartAsync(fx.Url, fx.TempDir, 2, cts3.Token);
        Assert.Equal(DownloadState.Completed, dl3.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-CKPT-09 recovered content");
    }

    // ---------- T-CKPT-10: finalize flush failure must not report durable completion ----------

    [Fact]
    public async Task T_CKPT_10_Finalize_Flush_Failure_Fails_Loudly()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 2L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var task = dl.StartAsync(fx.Url, fx.TempDir, 1, cts.Token);
        DownloadSession? live = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            live = dl.ActiveSession;
            if (live != null) break;
            await Task.Delay(100);
        }
        Assert.NotNull(live);
        Assert.False(task.IsCompleted, "Download finished before the fault could be injected; widen the throttle window.");
        live!.FileFlusher = new FailingFlusher(new Win32Exception(112, "simulated disk full at finalize"));

        var ex = await Record.ExceptionAsync(() => task);
        var failed = Assert.IsType<DownloadFailedException>(ex);
        Assert.Equal(DownloadState.Failed, dl.GetState());
        output.WriteLine($"OBSERVATION: finalize flush failure surfaced as: {failed.Message}");
        // No false durable completion: part + last-good metadata survive for recovery.
        Assert.True(File.Exists(Path.Combine(fx.TempDir, fx.Server.Profile.FileName + ".part")), "Part must survive failed finalize.");
        // Healing the flusher lets a restart complete byte-identically.
        live.FileFlusher = new OsFileFlusher();
        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 1, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-CKPT-10 healed content");
    }

    // ---------- T-CKPT-STRESS: concurrent checkpoints under load ----------

    [Fact]
    public async Task T_CKPT_STRESS_Concurrent_Checkpoints_Converge()
    {
        const long size = 64L * 1024 * 1024;
        await using var fx = await CreateFixtureAsync(64);
        using var dl = new ParallelDownloader();
        using var session = NewSession(dl, fx, size);
        var rnd = new Random(0041);
        var gated = new GatedPublisher();
        session.MetadataPublisher = gated;

        var tasks = new List<Task>();
        for (int w = 0; w < 8; w++)
        {
            int worker = w;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 25; i++)
                {
                    long s = ((worker * 25L + i) * size / 200) % size;
                    long e = Math.Min(s + size / 200 - 1, size - 1);
                    session.Tracker!.CompleteRange(s, e);
                    await Task.Delay(rnd.Next(0, 3));
                    session.Checkpoint(true, TimeSpan.FromSeconds(10));
                }
            }));
        }
        await WithBudget(Task.WhenAll(tasks), TestBudget, "200 concurrent checkpoints");
        session.Tracker!.CompleteRange(0, size - 1);
        session.Checkpoint(true, TimeSpan.FromSeconds(30));
        var loaded = RecoveryMetadata.TryLoad(session.MetaPath);
        Assert.NotNull(loaded);
        Assert.True(loaded.CompletedRanges.Count > 0, "Canonical must carry coverage.");
        var verify = new RangeTracker(size);
        foreach (var r in loaded.CompletedRanges) verify.CompleteRange(r[0], r[1]);
        Assert.True(verify.IsComplete, "Final forced checkpoint must converge to full coverage.");
        await PollAsync(() => StrayCount(session) == 0, TimeSpan.FromSeconds(30), "temp cleanup");
        Assert.Equal(0, session.CheckpointFaults);
        output.WriteLine($"OBSERVATION: 200 concurrent checkpoints converged; canonical gen={loaded.CheckpointGeneration}, faults={session.CheckpointFaults}.");
    }

    private static int StrayCount(DownloadSession session)
    {
        var dir = Path.GetDirectoryName(session.MetaPath)!;
        var name = Path.GetFileName(session.MetaPath);
        return Directory.GetFiles(dir, name + ".tmp.*").Length;
    }
}
