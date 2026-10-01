using System.Diagnostics;
using DRRipper.TestServer;
using Xunit.Abstractions;

namespace DRRipper.Tests;

/// <summary>
/// Ticket #004 recovery suite: genuine process kills (child driver), torn
/// checkpoints, corrupt/missing metadata, pause state machine, read-timeout
/// lifecycle. Every test has an explicit deadline; the test runner itself is
/// never terminated (a dedicated driver process takes the kill).
/// </summary>
public sealed class CrashRecoveryTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    private static string DriverExe =>
        Path.Combine(AppContext.BaseDirectory, "DRRipper.RecoveryDriver.exe");

    private sealed class DriverSession : IDisposable
    {
        public Process Proc { get; }
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        private DriverSession(Process proc) { Proc = proc; }

        public static DriverSession Launch(string url, string dir, int conns, string extra = "")
        {
            Assert.True(File.Exists(DriverExe), $"Recovery driver missing at {DriverExe} (build output copy failed).");
            var psi = new ProcessStartInfo(DriverExe,
                $"--url \"{url}\" --dir \"{dir}\" --conns {conns} {extra}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var session = new DriverSession(proc);
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) session._lines.Enqueue(e.Data);
            };
            Assert.True(proc.Start(), "Could not start recovery driver.");
            proc.BeginOutputReadLine();
            return session;
        }

        public async Task<string?> WaitForLineAsync(Func<string, bool> pred, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                while (_lines.TryDequeue(out var line))
                    if (pred(line)) return line;
                if (Proc.HasExited) return null;
                await Task.Delay(100);
            }
            return null;
        }

        public static long ProgressBytes(string line)
        {
            // "PROGRESS <bytes> <total>"
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var b) ? b : -1;
        }

        public void Kill(int millisecondsTimeout = 10000)
        {
            if (!Proc.HasExited) Proc.Kill();
            Assert.True(Proc.WaitForExit(millisecondsTimeout), "Killed driver did not terminate.");
            Assert.True(Proc.HasExited, "Driver process still alive after Kill.");
        }

        public void Dispose()
        {
            try { if (!Proc.HasExited) Proc.Kill(); } catch { }
            try { Proc.WaitForExit(5000); } catch { }
            try { Proc.Dispose(); } catch { }
        }
    }

    private static string MetaPath(DownloadFixture fx) =>
        Path.Combine(fx.TempDir, fx.Server.Profile.FileName + ".part.drmeta");

    private static async Task WaitForMetaCoverageAsync(string metaPath, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(metaPath))
                {
                    string json = await File.ReadAllTextAsync(metaPath);
                    if (json.Contains("\"SchemaVersion\":2") &&
                        json.Contains("\"CompletedRanges\":[[") &&
                        !json.Contains("\"CompletedRanges\":[]")) return;
                }
            }
            catch { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"No covering checkpoint appeared at {metaPath} in {timeout}.");
    }

    private static double MaxRatio(long size, int conns) =>
        1 + ((double)conns * 8 * 1024 * 1024 / size) + 0.1;

    // ---------- T-REC-01: genuine kill mid parallel download ----------

    [Fact]
    public async Task T_REC_01_Kill_Reuses_Verified_Bytes()
    {
        const long size = 48L * 1024 * 1024;
        const int conns = 2;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 2L * 1024 * 1024 });

        using (var driver = DriverSession.Launch(fx.Url, fx.TempDir, conns, "--checkpoint-interval-ms 100"))
        {
            Assert.NotNull(await driver.WaitForLineAsync(l => l == "READY", TimeSpan.FromSeconds(30)));
            string? hit = null;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(90))
            {
                hit = await driver.WaitForLineAsync(
                    l => l.StartsWith("PROGRESS") && DriverSession.ProgressBytes(l) >= 9L * 1024 * 1024,
                    TimeSpan.FromSeconds(5));
                if (hit != null) break;
            }
            Assert.NotNull(hit);
            await WaitForMetaCoverageAsync(MetaPath(fx), TimeSpan.FromSeconds(60));
            driver.Kill(); // genuine SIGKILL-equivalent: no cleanup runs
            output.WriteLine("OBSERVATION: driver killed mid-download; restarting in parent.");
        }

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, conns, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-01-KILL final content");

        long transmitted = fx.Server.TotalBytesTransmitted;
        double bound = MaxRatio(size, conns);
        output.WriteLine($"OBSERVATION: transmitted={transmitted} ({(double)transmitted / size:F2}x, bound={bound:F2}x).");
        Assert.True(transmitted < (long)(size * bound),
            $"Kill recovery wasted too much (transmitted {transmitted} for {size}-byte file; bound {bound:F2}x).");
    }

    // ---------- T-REC-02: kill early (maybe pre-checkpoint) + torn tmp ----------

    [Fact]
    public async Task T_REC_02_Early_Kill_Still_Recovers()
    {
        const long size = 24L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 4L * 1024 * 1024 });

        using (var driver = DriverSession.Launch(fx.Url, fx.TempDir, 2, "--checkpoint-interval-ms 100"))
        {
            Assert.NotNull(await driver.WaitForLineAsync(l => l == "READY", TimeSpan.FromSeconds(30)));
            Assert.NotNull(await driver.WaitForLineAsync(
                l => l.StartsWith("PROGRESS") && DriverSession.ProgressBytes(l) >= 1024 * 1024,
                TimeSpan.FromSeconds(60)));
            driver.Kill();
        }

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-02 early-kill content");
    }

    [Fact]
    public async Task T_REC_02_Torn_Tmp_Ignored_Snapshot_Used()
    {
        // Crash DURING checkpoint replacement leaves a torn .tmp next to a valid
        // snapshot: recovery must use the snapshot, never the tmp.
        const long size = 16L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 4L * 1024 * 1024 });

        using (var dl = new ParallelDownloader())
        {
            dl.CheckpointInterval = TimeSpan.FromMilliseconds(100);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            long engineBytes = 0;
            dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
            var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
            var sw = Stopwatch.StartNew();
            while (engineBytes < 4L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(45) && !task.IsCompleted)
                await Task.Delay(100);
            dl.Pause();
            Assert.Equal(DownloadState.Paused, dl.GetState());
            await cts.CancelAsync();
            try { await task; } catch (OperationCanceledException) { }
        } // dispose: files persist (Teardown never deletes)

        string tmp = MetaPath(fx) + ".tmp";
        await File.WriteAllTextAsync(tmp, "{\"SchemaVersion\":2,\"garbage\":");
        output.WriteLine("OBSERVATION: planted torn .tmp next to valid snapshot.");

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-02 torn-tmp content");
    }

    // ---------- T-REC-04 / T-REC-06: corrupt / missing metadata / part ----------

    private static async Task<string> PauseAndFreezeAsync(DownloadFixture fx, ITestOutputHelper output)
    {
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        var sw = Stopwatch.StartNew();
        while (engineBytes < 2L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(45) && !task.IsCompleted)
            await Task.Delay(100);
        dl.Pause();
        Assert.Equal(DownloadState.Paused, dl.GetState());
        // Freeze: end the run WITHOUT cancelling (cancel/teardown-with-delete
        // would destroy the checkpoint; Dispose/Teardown preserves files,
        // exactly like unexpected process termination).
        dl.Dispose();
        try { await task; } catch (OperationCanceledException) { }
        catch (Exception ex) { output.WriteLine($"OBSERVATION: frozen run ended with {ex.GetType().Name} (files preserved)."); }
        return Path.Combine(fx.TempDir, fx.Server.Profile.FileName);
    }

    [Fact]
    public async Task T_REC_04_Truncated_Metadata_Restarts_Safely()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        await PauseAndFreezeAsync(fx, output);
        string meta = MetaPath(fx);
        Assert.True(File.Exists(meta), "Expected a checkpoint to exist before corrupting it.");
        string json = await File.ReadAllTextAsync(meta);
        await File.WriteAllTextAsync(meta, json[..(json.Length / 2)]); // truncation

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-04 truncated-meta content");
    }

    [Fact]
    public async Task T_REC_04_BitFlip_Metadata_Restarts_Safely()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        await PauseAndFreezeAsync(fx, output);
        string meta = MetaPath(fx);
        byte[] bytes = await File.ReadAllBytesAsync(meta);
        bytes[bytes.Length / 2] ^= 0xFF; // checksum must now fail
        await File.WriteAllBytesAsync(meta, bytes);

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-04 bitflip-meta content");
    }

    [Fact]
    public async Task T_REC_04_Missing_Metadata_Restarts_Safely()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        await PauseAndFreezeAsync(fx, output);
        File.Delete(MetaPath(fx)); // part remains, metadata gone: trust nothing

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-04 missing-meta content");
    }

    [Fact]
    public async Task T_REC_06_Missing_Part_Restarts_Safely()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        await PauseAndFreezeAsync(fx, output);
        File.Delete(Path.Combine(fx.TempDir, fx.Server.Profile.FileName + ".part")); // metadata dangles

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-06 missing-part content");
    }

    // ---------- T-REC-07: blackout then restore ----------

    [Fact]
    public async Task T_REC_07_Blackout_Restores_To_Identical_File()
    {
        const long size = 16L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 4L * 1024 * 1024 });

        using var dl = new ParallelDownloader();
        var seen = new System.Collections.Concurrent.ConcurrentBag<DownloadState>();
        dl.StateChanged += s => seen.Add(s);
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        using var cts = new CancellationTokenSource(Timeout);
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        var sw = Stopwatch.StartNew();
        while (engineBytes < 2L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(45) && !task.IsCompleted)
            await Task.Delay(100);
        fx.Server.Profile.GlobalStatus = 503; // blackout: every request fails retryably
        fx.Server.Profile.RetryAfterSeconds = 1;
        await Task.Delay(TimeSpan.FromSeconds(6));
        fx.Server.Profile.GlobalStatus = null; // restore
        fx.Server.Profile.RetryAfterSeconds = null;
        string final = await task;
        Assert.Equal(DownloadState.Completed, dl.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-07 blackout content");
        output.WriteLine($"OBSERVATION: survived 6s 503 blackout; states seen: {string.Join(",", seen.Distinct())}.");
    }

    // ---------- T-STATE pause machine ----------

    [Fact]
    public async Task T_STATE_01_Pause_During_Active_Read()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 16L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        var sw = Stopwatch.StartNew();
        while (engineBytes < 2L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(60) && !task.IsCompleted)
            await Task.Delay(100);
        var pauseSw = Stopwatch.StartNew();
        dl.Pause(); // mid-read: must ack, checkpoint, and park workers
        output.WriteLine($"OBSERVATION: Pause() took {pauseSw.Elapsed.TotalSeconds:F1}s.");
        Assert.Equal(DownloadState.Paused, dl.GetState());
        Assert.True(File.Exists(MetaPath(fx)), "Pause must leave a recovery checkpoint.");
        dl.Resume();
        string final;
        try { final = await task; }
        finally
        {
            foreach (var r in fx.Server.Requests.Where(r => r.StatusCode == 206 && !string.IsNullOrEmpty(r.RangeHeader)))
                output.WriteLine($"RANGESERVED {r.RangeHeader} bytes={r.BytesWritten}");
        }
        Assert.Equal(DownloadState.Completed, dl.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-STATE-01 content");
    }

    [Fact]
    public async Task T_STATE_02_Pause_During_Retry_Backoff()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 16L * 1024 * 1024 });
        fx.Server.Profile.RangeFaults.Add(new RangeFault(0, 8L * 1024 * 1024 - 1, 500));
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        await Task.Delay(300); // land inside the first backoff
        dl.Pause();
        Assert.Equal(DownloadState.Paused, dl.GetState());
        dl.Resume();
        // 500s persist: resume must end in explicit failure, never a hang or false success.
        var ex = await DownloadFixture.ThrowsDownloadFailedAsync(() => task, output);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task T_STATE_03_Immediate_Resume()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        await Task.Delay(500);
        dl.Pause();
        dl.Resume(); // immediate: no settled state required
        string final = await task;
        Assert.Equal(DownloadState.Completed, dl.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-STATE-03 content");
    }

    [Fact]
    [Trait("Category", "Sensitive")]
    public async Task T_STATE_04_Rapid_Pause_Resume_Cycles()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 16L * 1024 * 1024, BytesPerSecond = 4L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        var seen = new System.Collections.Concurrent.ConcurrentBag<DownloadState>();
        dl.StateChanged += s => seen.Add(s);
        var trail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var t0 = Stopwatch.StartNew();
        dl.StateChanged += s => trail.Enqueue($"t+{t0.Elapsed.TotalSeconds:F1}s state={s}");
        dl.ProgressChanged += p => trail.Enqueue($"t+{t0.Elapsed.TotalSeconds:F1}s progress={p.TotalBytesDownloaded}/{p.TotalBytes} {p.StatusMessage}");
        using var cts = new CancellationTokenSource(Timeout);
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        // Watchdog: distinguishes engine stall (heartbeats keep flowing) from
        // machine-wide scheduling freezes (gaps in heartbeat timestamps).
        var watchdogCts = new CancellationTokenSource();
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!watchdogCts.Token.IsCancellationRequested)
                {
                    output.WriteLine($"WATCHDOG t+{t0.Elapsed.TotalSeconds:F0}s alive taskDone={task.IsCompleted} state={dl.GetState()}");
                    await Task.Delay(10000, watchdogCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        // Bounded sync wrappers: Pause()/Resume() are synchronous by UI contract.
        // If either ever blocks past its internal bounds, fail fast with a clear
        // signal instead of hanging to the test timeout (stall diagnosis).
        static async Task<bool> BoundedPauseAsync(ParallelDownloader d, ITestOutputHelper output, TimeSpan bound)
        {
            var sw = Stopwatch.StartNew();
            var opTask = Task.Run(() => d.Pause());
            bool finished = await Task.WhenAny(opTask, Task.Delay(bound)) == opTask;
            sw.Stop();
            var tel = d.GetPauseTelemetry();
            output.WriteLine($"OBSERVATION: Pause() took {sw.Elapsed.TotalSeconds:F1}s (finished={finished}, sem={tel.SemMs}ms ack={tel.AckMs}ms ckpt={tel.CheckpointMs}ms).");
            if (!finished) Assert.Fail($"Pause() blocked past {bound.TotalSeconds:F0}s (engine stall).");
            await opTask;
            return true;
        }
        static async Task BoundedResumeAsync(ParallelDownloader d, ITestOutputHelper output, TimeSpan bound)
        {
            var sw = Stopwatch.StartNew();
            var opTask = Task.Run(() => d.Resume());
            bool finished = await Task.WhenAny(opTask, Task.Delay(bound)) == opTask;
            sw.Stop();
            output.WriteLine($"OBSERVATION: Resume() took {sw.Elapsed.TotalSeconds:F1}s (finished={finished}).");
            if (!finished) Assert.Fail($"Resume() blocked past {bound.TotalSeconds:F0}s (engine stall).");
            await opTask;
        }
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        for (int i = 0; i < 5; i++)
        {
            // Event-driven cadence (not wall-clock): wait for forward progress so
            // loaded machines exercise the same transitions without races.
            var progSw = Stopwatch.StartNew();
            long baseBytes = engineBytes;
            while (!task.IsCompleted && engineBytes < baseBytes + 2L * 1024 * 1024 && progSw.Elapsed < TimeSpan.FromSeconds(90))
                await Task.Delay(100);
            if (task.IsCompleted) break;
            await BoundedPauseAsync(dl, output, TimeSpan.FromSeconds(60));
            var ps = dl.GetState();
            var tel = dl.GetPauseTelemetry();
            output.WriteLine($"OBSERVATION: cycle {i}: after Pause state={ps} (sem={tel.SemMs}ms ack={tel.AckMs}ms ckpt={tel.CheckpointMs}ms).");
            if (ps == DownloadState.Completed) break; // completion won the race: done
            await Task.Delay(100);
            await BoundedResumeAsync(dl, output, TimeSpan.FromSeconds(60));
            output.WriteLine($"OBSERVATION: cycle {i}: after Resume state={dl.GetState()}.");
        }
        foreach (var line in trail.TakeLast(40))
            output.WriteLine("TRAIL " + line);
        string final = await task;
        try { watchdogCts.Cancel(); } catch { }
        try { await watchdog; } catch { }
        Assert.Equal(DownloadState.Completed, dl.GetState());
        Assert.DoesNotContain(DownloadState.Failed, seen);
        fx.AssertFileHash(final, fx.ExpectedHash, "T-STATE-04 content");
    }

    [Fact]
    public async Task T_STATE_05_Cancel_While_Paused()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 16L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        var seen = new System.Collections.Concurrent.ConcurrentBag<DownloadState>();
        dl.StateChanged += s => seen.Add(s);
        using var cts = new CancellationTokenSource(Timeout);
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        await Task.Delay(800);
        dl.Pause();
        Assert.Equal(DownloadState.Paused, dl.GetState());
        dl.Cancel(); // cancellation takes precedence over pause
        await DownloadFixture.ThrowsCancelledAsync(() => task, output);
        Assert.Equal(DownloadState.Cancelled, dl.GetState());
        Assert.DoesNotContain(DownloadState.Completed, seen);
    }

    [Fact]
    public async Task T_STATE_06_Cancel_Racing_Pause()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 16L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        var seen = new System.Collections.Concurrent.ConcurrentBag<DownloadState>();
        dl.StateChanged += s => seen.Add(s);
        using var cts = new CancellationTokenSource(Timeout);
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        await Task.Delay(800);
        var pauseTask = Task.Run(() => dl.Pause());
        await Task.Delay(50);
        dl.Cancel();
        await pauseTask;
        await DownloadFixture.ThrowsCancelledAsync(() => task, output);
        Assert.Equal(DownloadState.Cancelled, dl.GetState());
        Assert.DoesNotContain(DownloadState.Completed, seen);
    }

    // ---------- T-NET read lifecycle ----------

    [Fact]
    public async Task T_NET_01_Repeated_Timeouts_Recover_Without_Corruption()
    {
        // Every response stalls after 1 MB with no EOF: each attempt must time
        // out, terminate its read before buffer reuse, and resume the remainder.
        const long size = 16L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            StallAfterBytes = 1L * 1024 * 1024,
        });
        using var dl = new ParallelDownloader { ReadTimeout = TimeSpan.FromSeconds(2) };
        using var cts = new CancellationTokenSource(Timeout);
        string final = await dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        Assert.Equal(DownloadState.Completed, dl.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-NET-01 content");
        int rangeGets = fx.Server.Requests.Count(r => r.StatusCode == 206 && !string.IsNullOrEmpty(r.RangeHeader));
        output.WriteLine($"OBSERVATION: {rangeGets} range attempts for 2 chunks (timeout/resume cycling).");
        Assert.True(rangeGets >= 10, "Expected repeated timeout/resume cycles.");
    }

    [Fact]
    public async Task T_NET_02_Cancel_While_Read_Pending()
    {
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 10L * 1024 * 1024,
            BytesPerSecond = 200L * 1024, // slow reads: cancel lands mid-ReadAsync
        });
        using var dl = new ParallelDownloader();
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        using var cts = new CancellationTokenSource(Timeout);
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        var sw = Stopwatch.StartNew();
        while (engineBytes < 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(60) && !task.IsCompleted)
            await Task.Delay(100);
        await cts.CancelAsync();
        await DownloadFixture.ThrowsCancelledAsync(() => task, output);
        Assert.Equal(DownloadState.Cancelled, dl.GetState());
    }

    // ---------- T-INT-COVERAGE: sparse resume requests only missing bytes ----------

    [Fact]
    public async Task T_INT_COVERAGE_Resume_Requests_Only_Missing_Ranges()
    {
        // Slow throttle + 5 chunks over 4 workers: the first chunk checkpoints
        // coverage ~16 s in while the tail is still flying, opening a wide,
        // deterministic pause window (clustered same-speed completions would
        // otherwise race finalize).
        const long size = 40L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 512L * 1024 });

        List<(long S, long E)> firstCompleted;
        long coveredBytes = 0;
        using (var dl = new ParallelDownloader())
        {
            dl.CheckpointInterval = TimeSpan.FromMilliseconds(100);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            var task = dl.StartAsync(fx.Url, fx.TempDir, 4, cts.Token);
            var coverSw = Stopwatch.StartNew();
            bool covered = false;
            while (coverSw.Elapsed < TimeSpan.FromSeconds(120))
            {
                if (task.IsFaulted) await task; // surface real errors, don't stall
                if (task.IsCompleted) break; // missed the window; Pause below will no-op
                try
                {
                    if (File.Exists(MetaPath(fx)))
                    {
                        string json = await File.ReadAllTextAsync(MetaPath(fx));
                        if (json.Contains("\"SchemaVersion\":2") &&
                            json.Contains("\"CompletedRanges\":[[") &&
                            !json.Contains("\"CompletedRanges\":[]")) { covered = true; break; }
                    }
                }
                catch { }
                await Task.Delay(50);
            }
            Assert.True(covered, "No covering checkpoint appeared before completion; widen the throttle window.");
            dl.Pause();
            Assert.Equal(DownloadState.Paused, dl.GetState());
            dl.Pause(); // idempotent re-pause must be a safe no-op
            Assert.Equal(DownloadState.Paused, dl.GetState());
            firstCompleted = ReadCompletedRanges(MetaPath(fx));
            Assert.NotEmpty(firstCompleted);
            coveredBytes = firstCompleted.Sum(s => s.E - s.S + 1);
            dl.Dispose(); // freeze like process end (Teardown keeps files)
            try { await task; } catch (OperationCanceledException) { }
            catch (Exception ex) { output.WriteLine($"OBSERVATION: frozen run ended with {ex.GetType().Name}."); }
        }

        // Server restart on the SAME port with the SAME profile: identical URL,
        // bytes and validators (resume stays valid), but a pristine request log —
        // run-1 stragglers are impossible by construction, so every logged range
        // is unambiguously the resumed run's. Also mirrors real server reboots.
        int port = fx.Server.BoundPort;
        await fx.Server.DisposeAsync();
        await using var server2 = await TestDownloadServer.StartOnPortAsync(fx.Server.Profile, port);
        using var dl2 = new ParallelDownloader();
        var resumedMessages = new System.Collections.Concurrent.ConcurrentBag<string>();
        dl2.ProgressChanged += p =>
        {
            if (!string.IsNullOrEmpty(p.StatusMessage)) resumedMessages.Add(p.StatusMessage);
        };
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 4, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-INT-COVERAGE content");
        Assert.Contains(resumedMessages, m => m.Contains("Resum"));

        // Requested spans after restart must be disjoint from verified first-run coverage.
        var all = server2.Requests.ToList();
        var t0 = all.Count > 0 ? all[0].Timestamp : DateTimeOffset.UtcNow;
        for (int i = 0; i < all.Count; i++)
        {
            var r = all[i];
            output.WriteLine($"REQLOG t+{(r.Timestamp - t0).TotalSeconds:F1}s {r.Method} {r.RangeHeader} status={r.StatusCode} bytes={r.BytesWritten} conn={r.ConnectionId} rport={r.RemotePort}");
        }
        foreach (var r in all)
        {
            if (r.StatusCode != 206 || string.IsNullOrEmpty(r.RangeHeader)) continue;
            var (s, e) = ParseRange(r.RangeHeader, size);
            foreach (var (cs, ce) in firstCompleted)
                Assert.True(e < cs || s > ce,
                    $"Second run re-requested [{s}-{e}] (bytes={r.BytesWritten}, conn={r.ConnectionId}), overlapping verified [{cs}-{ce}].");
        }
        long retransmitted = server2.TotalBytesTransmitted;
        long missing = size - coveredBytes;
        output.WriteLine($"OBSERVATION: resumed {firstCompleted.Count} verified span(s) with zero overlap; second run transmitted {retransmitted} bytes for {missing} missing bytes.");
        Assert.True(retransmitted < missing + 8L * 1024 * 1024,
            $"Second run fetched far more than the missing ranges (transmitted {retransmitted}, missing {missing}).");
    }

    [Fact] // Checkpoint unit: snapshot reaches disk with coverage and checksum.
    public async Task Checkpoint_Writes_Coverage_To_Disk()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024 });
        using var dl = new ParallelDownloader();
        var session = new DownloadSession(dl, dl.Client)
        {
            Url = fx.Url,
            TargetDirectory = fx.TempDir,
            TotalSize = 8L * 1024 * 1024,
            FileName = "test-payload.bin",
            FinalPath = Path.Combine(fx.TempDir, "test-payload.bin"),
            ETag = "\"v1\"",
            Mode = "segmented",
        };
        session.PartPath = session.FinalPath + ".part";
        session.MetaPath = session.PartPath + ".drmeta";
        using (var fs = new FileStream(session.PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(session.TotalSize);
        session.OpenExistingFile();
        session.Tracker = new RangeTracker(session.TotalSize);
        session.Tracker.CompleteRange(0, 8L * 1024 * 1024 - 1);
        session.Checkpoint(true);
        string json = await File.ReadAllTextAsync(session.MetaPath);
        output.WriteLine($"OBSERVATION: meta={json}");
        Assert.Contains("\"SchemaVersion\":2", json);
        Assert.Contains("\"CompletedRanges\":[[0,8388607]]", json);
        var loaded = RecoveryMetadata.TryLoad(session.MetaPath);
        Assert.NotNull(loaded);
        Assert.Equal("\"v1\"", loaded.ETag);
    }

    [Fact] // §12: collisions never silently overwrite; unique names are generated.
    public void ResolveUniquePath_Avoids_Overwrite()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DRRipperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string taken = Path.Combine(dir, "a.bin");
            File.WriteAllText(taken, "x");
            Assert.Equal(Path.Combine(dir, "b.bin"), DownloadSession.ResolveUniquePath(Path.Combine(dir, "b.bin")));
            Assert.Equal(Path.Combine(dir, "a (2).bin"), DownloadSession.ResolveUniquePath(taken));
            File.WriteAllText(Path.Combine(dir, "a (2).bin"), "x");
            Assert.Equal(Path.Combine(dir, "a (3).bin"), DownloadSession.ResolveUniquePath(taken));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact] // Recovery startup latency: time from StartAsync to first progress on a resumed run.
    public async Task Resume_Startup_Latency_Is_Small()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 2L * 1024 * 1024 });
        using (var dl = new ParallelDownloader())
        {
            dl.CheckpointInterval = TimeSpan.FromMilliseconds(100);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            long engineBytes = 0;
            dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
            var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
            var sw = Stopwatch.StartNew();
            while (engineBytes < 2L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(45) && !task.IsCompleted)
                await Task.Delay(100);
            dl.Pause();
            Assert.Equal(DownloadState.Paused, dl.GetState());
            dl.Dispose();
            try { await task; } catch (OperationCanceledException) { }
        }

        // Checkpoint durability evidence: size of the pause-time snapshot on disk.
        string pausedMeta = Path.Combine(fx.TempDir, fx.Server.Profile.FileName + ".part.drmeta");
        if (File.Exists(pausedMeta))
            output.WriteLine($"OBSERVATION: pause checkpoint file size = {new FileInfo(pausedMeta).Length} bytes.");

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var firstProgress = new TaskCompletionSource<long>();
        dl2.ProgressChanged += p => firstProgress.TrySetResult(p.TotalBytesDownloaded);
        var rsw = Stopwatch.StartNew();
        var task2 = dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        var completedFirst = await Task.WhenAny(firstProgress.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        rsw.Stop();
        Assert.True(completedFirst == firstProgress.Task, "No progress event observed on resumed run.");
        output.WriteLine($"OBSERVATION: resume startup latency (StartAsync to first progress) = {rsw.Elapsed.TotalSeconds:F2}s.");
        Assert.True(rsw.Elapsed < TimeSpan.FromSeconds(30), "Resume startup took too long.");
        string final = await task2;
        fx.AssertFileHash(final, fx.ExpectedHash, "Resume_Startup content");
    }

    private static List<(long S, long E)> ReadCompletedRanges(string metaPath)
    {
        var spans = new List<(long S, long E)>();
        string json = File.ReadAllText(metaPath);
        var m = System.Text.RegularExpressions.Regex.Matches(json, @"\[(\d+),(\d+)\]");
        foreach (System.Text.RegularExpressions.Match match in m)
            spans.Add((long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value)));
        return spans;
    }

    private static (long S, long E) ParseRange(string header, long size)
    {
        var spec = header[(header.IndexOf('=') + 1)..];
        var parts = spec.Split('-');
        long s = long.Parse(parts[0]);
        long e = parts[1].Length == 0 ? size - 1 : long.Parse(parts[1]);
        return (s, e);
    }
}
