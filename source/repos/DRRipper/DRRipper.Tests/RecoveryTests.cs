using DRRipper.TestServer;
using Xunit.Abstractions;

namespace DRRipper.Tests;

/// <summary>
/// Interruption / restart characterization. "Application restart" is simulated
/// with a fresh ParallelDownloader over the same URL and directory (a new
/// process would observe identical on-disk state: partial file + .drmeta).
/// </summary>
public sealed class RecoveryTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Interrupts a download mid-flight WITHOUT destroying recoverable state:
    /// pause (durable checkpoint) + dispose (Teardown keeps files), exactly like
    /// unexpected process termination. In-process cancellation would delete
    /// partials by design and cannot test resume — the kill driver covers that.
    /// Cancellation here is driven by the ENGINE's own progress events (not the
    /// server log, which only records completed responses and would overshoot
    /// to a full file).
    /// </summary>
    private static async Task<string> PartialDownloadAsync(
        DownloadFixture fx, long minEngineBytes, TimeSpan pollTimeout, ITestOutputHelper output,
        int connections = 2, TimeSpan? checkpointInterval = null)
    {
        using var dl = new ParallelDownloader();
        if (checkpointInterval.HasValue) dl.CheckpointInterval = checkpointInterval.Value;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        var task = dl.StartAsync(fx.Url, fx.TempDir, connections, cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engineBytes < minEngineBytes && sw.Elapsed < pollTimeout && !task.IsCompleted)
            await Task.Delay(100);
        Assert.False(task.IsCompleted, "Download finished before it could be interrupted; increase size or throttle.");
        string metaPath = Path.Combine(fx.TempDir, fx.Server.Profile.FileName + ".part.drmeta");
        while (sw.Elapsed < pollTimeout && !task.IsCompleted && !CheckpointHasCoverage(metaPath))
            await Task.Delay(100);
        dl.Pause();
        Assert.Equal(DownloadState.Paused, dl.GetState());
        dl.Dispose(); // Teardown keeps .part + .drmeta (process-death equivalent)
        try { await task; } catch (OperationCanceledException) { }
        catch (Exception ex) { output.WriteLine($"OBSERVATION: frozen run ended with {ex.GetType().Name}."); }
        return Path.Combine(fx.TempDir, fx.Server.Profile.FileName);
    }

    private static bool CheckpointHasCoverage(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath)) return false;
            string json = File.ReadAllText(metaPath);
            if (!json.Contains("\"SchemaVersion\":2")) return false;
            if (json.Contains("\"CompletedRanges\":[[")
                && !json.Contains("\"CompletedRanges\":[]")) return true;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"PrefixOffset\":(\\d+)");
            return m.Success && long.Parse(m.Groups[1].Value) > 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task T_REC_01_Restart_Reuses_Verified_Bytes()
    {
        // Fixed behaviour (Ticket #004 §5): restart reuses validated ranges.
        // Bound is principled: full reuse + bounded in-flight waste (uncompleted
        // partial chunks are re-fetched by design) + margin.
        const long size = 48L * 1024 * 1024;
        const int conns = 2;
        double maxRatio = 1 + ((double)conns * 8 * 1024 * 1024 / size) + 0.1;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 2L * 1024 * 1024 });

        await PartialDownloadAsync(fx, minEngineBytes: 9L * 1024 * 1024,
            pollTimeout: TimeSpan.FromSeconds(90), output,
            connections: conns, checkpointInterval: TimeSpan.FromMilliseconds(100));

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, conns, cts2.Token);
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-01 final content");

        long transmitted = fx.Server.TotalBytesTransmitted;
        output.WriteLine($"OBSERVATION: total transmitted={transmitted} for {size}-byte file (ratio={(double)transmitted / size:F2}, bound={maxRatio:F2}).");
        Assert.True(transmitted < (long)(size * maxRatio),
            $"T-REC-01: restart failed to reuse verified bytes (transmitted {transmitted} bytes for {size}-byte file; bound {maxRatio:F2}x).");
    }

    [Fact]
    public async Task T_REC_03_Content_Switch_Triggers_Detected_Restart()
    {
        // Server payload changes (same size, new seed/ETag) between interruption
        // and restart. Fixed behaviour (Ticket #004 §6): validators disagree, so
        // the partial download is discarded and v2 is fetched cleanly — never a
        // V1/V2 hybrid. Detection is proven by retransmission ~= full file.
        const long size = 12L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, Seed = 1, ETag = "\"v1\"", BytesPerSecond = 2L * 1024 * 1024 });

        await PartialDownloadAsync(fx, minEngineBytes: 4L * 1024 * 1024,
            pollTimeout: TimeSpan.FromSeconds(90), output,
            connections: 2, checkpointInterval: TimeSpan.FromMilliseconds(100));

        fx.Server.Profile.Seed = 2;
        fx.Server.Profile.ETag = "\"v2\"";
        fx.Server.Profile.LastModified = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        string v2hash = DeterministicContent.Sha256Hex(size, 2);
        fx.AssertFileHash(final, v2hash, "T-REC-03 final content is clean v2");
        long transmitted = fx.Server.TotalBytesTransmitted;
        output.WriteLine($"OBSERVATION: transmitted={transmitted} for {size}-byte file after identity switch.");
        Assert.True(transmitted >= size,
            "Identity switch was not detected: restart reused bytes across versions.");
    }

    [Fact]
    public async Task T_REC_05_NoRange_Server_Safely_Restarts_Full_Download()
    {
        // No-range server (Ticket #004 §11B): arbitrary offset recovery is
        // impossible, so the CORRECT policy is a safe full restart — truncated
        // part first (never a stale tail), byte-identical final file. The old
        // retransmission assertion is retired: retransmission here is required,
        // not a defect.
        const long size = 16L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            SupportRanges = false,
            BytesPerSecond = 2L * 1024 * 1024,
        });

        await PartialDownloadAsync(fx, minEngineBytes: 4L * 1024 * 1024,
            pollTimeout: TimeSpan.FromSeconds(90), output,
            connections: 2, checkpointInterval: TimeSpan.FromMilliseconds(100));

        fx.Server.Profile.BytesPerSecond = null;
        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 1, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-05 final content");
        Assert.Equal(size, new FileInfo(final).Length);
        output.WriteLine($"OBSERVATION: no-range restart transmitted {fx.Server.TotalBytesTransmitted} bytes total (full re-download required by §11B).");
    }

    [Fact]
    public async Task T_REC_05b_Ranged_SingleStream_Reuses_Prefix()
    {
        // Same single-stream path, but the server DOES support ranges
        // (Ticket #004 §11A): the validated prefix must be reused via If-Range.
        const long size = 16L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            BytesPerSecond = 4L * 1024 * 1024,
        });

        await PartialDownloadAsync(fx, minEngineBytes: 4L * 1024 * 1024,
            pollTimeout: TimeSpan.FromSeconds(60), output,
            connections: 1, checkpointInterval: TimeSpan.FromMilliseconds(100));

        fx.Server.Profile.BytesPerSecond = null;
        // Capture the checkpointed offset BEFORE run 2 (finalize deletes metadata).
        long checkpointed = DownloadFixture.ReadPrefixOffset(DownloadFixture.PartMetaPath(fx));
        Assert.True(checkpointed > 0, "Run 1 left no reusable prefix; cannot test resume.");
        using var dl2 = new ParallelDownloader();
        var statuses = new System.Collections.Concurrent.ConcurrentBag<string>();
        dl2.ProgressChanged += p =>
        {
            if (!string.IsNullOrEmpty(p.StatusMessage)) statuses.Add(p.StatusMessage);
        };
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 1, cts2.Token);
        Assert.Equal(DownloadState.Completed, dl2.GetState());
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-05b final content");
        output.WriteLine($"OBSERVATION: run-2 states: {string.Join(" | ", statuses.Distinct().Take(8))}.");

        // Precise, race-free reuse proof: run 2 must issue exactly one ranged
        // GET starting at the checkpointed prefix offset (server-side byte
        // totals cannot prove this: aborted streams overcount bytes the client
        // never consumed).
        var resumeGets = fx.Server.Requests
            .Where(r => r.StatusCode == 206 && !string.IsNullOrEmpty(r.RangeHeader))
            .ToList();
        Assert.Single(resumeGets);
        var (rs, _) = DownloadFixture.ParseRangeHeader(resumeGets[0].RangeHeader!, size);
        output.WriteLine($"OBSERVATION: checkpointed prefix={checkpointed}, resumed from={rs}.");
        Assert.Equal(checkpointed, rs);
        output.WriteLine($"OBSERVATION: total transmitted={fx.Server.TotalBytesTransmitted} (includes abort-race overcount; not asserted).");

        long transmitted = fx.Server.TotalBytesTransmitted;
        output.WriteLine($"OBSERVATION: total transmitted={transmitted} for {size}-byte ranged prefix file (ratio={(double)transmitted / size:F2}).");
        Assert.True(transmitted < (long)(size * 1.1),
            $"T-REC-05b: prefix was not reused (transmitted {transmitted} bytes for {size}-byte file; expected <110%).");
    }
}
