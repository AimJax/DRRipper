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
    /// Interrupts a download mid-flight. Cancellation is driven by the ENGINE's
    /// own progress events (not the server log, which only records completed
    /// responses and would overshoot to a full file).
    /// </summary>
    private static async Task<string> PartialDownloadAsync(
        DownloadFixture fx, long minEngineBytes, TimeSpan pollTimeout, ITestOutputHelper output)
    {
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engineBytes < minEngineBytes && sw.Elapsed < pollTimeout && !task.IsCompleted)
            await Task.Delay(100);
        output.WriteLine($"OBSERVATION: interrupting with engine at {engineBytes} bytes.");
        await cts.CancelAsync();
        string path;
        try
        {
            path = await task;
            output.WriteLine($"OBSERVATION: post-cancel StartAsync returned path; engine state={dl.GetState()}; file length={new FileInfo(path).Length}.");
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("OBSERVATION: post-cancel StartAsync threw OperationCanceledException.");
            path = Path.Combine(fx.TempDir, fx.Server.Profile.FileName);
        }
        return path;
    }

    [Fact]
    [Trait("Category", "KnownFailure")]
    public async Task T_REC_01_Restart_Retransmits_Everything()
    {
        // Correct behaviour: resume reuses verified bytes (retransmit ~0).
        // Current behaviour (AUDIT.md F-03): restart truncates and re-downloads fully.
        const long size = 24L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, BytesPerSecond = 4L * 1024 * 1024 });

        await PartialDownloadAsync(fx, minEngineBytes: 4L * 1024 * 1024,
            pollTimeout: TimeSpan.FromSeconds(60), output);

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-01 final content");

        long transmitted = fx.Server.TotalBytesTransmitted;
        output.WriteLine($"OBSERVATION: total transmitted={transmitted} for {size}-byte file (ratio={(double)transmitted / size:F2}).");
        Assert.True(transmitted < (long)(size * 1.1),
            $"T-REC-01: restart retransmitted the whole file (transmitted {transmitted} bytes for {size}-byte file; expected <110%).");
    }

    [Fact]
    public async Task T_REC_03_Content_Switch_Between_Runs_Yields_Clean_Redownload()
    {
        // Server payload changes (same size, new seed/ETag) between interruption
        // and restart. The engine keeps no validators (AUDIT.md F-04) but also keeps
        // no partial data (F-03 truncation), so the result is a clean full copy of v2.
        // PASS documents that recovery today works only by wasteful full re-download,
        // and that identity changes go completely undetected.
        const long size = 12L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, Seed = 1, ETag = "\"v1\"", BytesPerSecond = 4L * 1024 * 1024 });

        await PartialDownloadAsync(fx, minEngineBytes: 4L * 1024 * 1024,
            pollTimeout: TimeSpan.FromSeconds(60), output);

        fx.Server.Profile.Seed = 2;
        fx.Server.Profile.ETag = "\"v2\"";
        fx.Server.Profile.LastModified = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 2, cts2.Token);
        string v2hash = DeterministicContent.Sha256Hex(size, 2);
        fx.AssertFileHash(final, v2hash, "T-REC-03 final content is clean v2");
        output.WriteLine("OBSERVATION: identity switch undetected (no validators), masked by full re-download (F-03/F-04).");
    }

    [Fact]
    [Trait("Category", "KnownFailure")]
    public async Task T_REC_05_SingleStream_Restart_Redownloads_Fully()
    {
        // No-range server. Run 1 is genuinely interrupted mid-flight (engine
        // progress >= 3 MB of 8 MB); run 2 restarts the same file.
        // Correct behaviour: resume from the partial offset (retransmit ~0).
        // Current behaviour: StartAsync pre-truncates (FileMode.Create) and
        // re-downloads everything — final content is correct but no byte is reused.
        // (Refines AUDIT.md F-10: the stale-tail path is unreachable because both
        // single-stream entry points truncate first; the observable defect is
        // full retransmission, shared with F-03.)
        const long size = 8L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            SupportRanges = false,
            BytesPerSecond = 4L * 1024 * 1024,
        });

        await PartialDownloadAsync(fx, minEngineBytes: 3L * 1024 * 1024,
            pollTimeout: TimeSpan.FromSeconds(60), output);

        fx.Server.Profile.BytesPerSecond = null;
        using var dl2 = new ParallelDownloader();
        using var cts2 = new CancellationTokenSource(Timeout);
        string final = await dl2.StartAsync(fx.Url, fx.TempDir, 1, cts2.Token);
        fx.AssertFileHash(final, fx.ExpectedHash, "T-REC-05 final content");

        long transmitted = fx.Server.TotalBytesTransmitted;
        output.WriteLine($"OBSERVATION: total transmitted={transmitted} for {size}-byte single-stream file (ratio={(double)transmitted / size:F2}).");
        Assert.True(transmitted < (long)(size * 1.1),
            $"T-REC-05: restart retransmitted the whole file (transmitted {transmitted} bytes for {size}-byte file; expected <110%).");
    }
}
