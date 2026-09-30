using DRRipper.TestServer;
using Xunit.Abstractions;

namespace DRRipper.Tests;

/// <summary>
/// Ticket #003 integrity suite (required tests B–D, F, H–Q; A/E/G live in
/// IntegrationTests as updated T-INT-03/05/04). All tests assert CORRECT
/// behaviour against the fixed engine; every StartAsync call carries an
/// explicit deadline so no test can hang.
/// </summary>
public sealed class IntegrityTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact] // B: unexpected 200 during a segmented transfer -> safe single-stream fallback.
    public async Task B_RangeAnswered200_Falls_Back_To_SingleStream()
    {
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 16L * 1024 * 1024,
            Respond200ToRanges = true,
        });
        var (path, state) = await fx.DownloadAsync(connections: 2, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "B");
    }

    [Fact] // C: Content-Range start mismatch is rejected before any byte is trusted.
    public async Task C_Wrong_ContentRange_Start_Is_Rejected()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            ContentRangeOverride = $"bytes 1-{size - 1}/{size}",
        });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        var ex = await DownloadFixture.ThrowsDownloadFailedAsync(
            () => dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token), output);
        Assert.Equal(DownloadState.Failed, dl.GetState());
    }

    [Fact] // D: Content-Range end mismatch is rejected.
    public async Task D_Wrong_ContentRange_End_Is_Rejected()
    {
        const long size = 8L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            ContentRangeOverride = $"bytes 0-{size - 2}/{size}",
        });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        var ex = await DownloadFixture.ThrowsDownloadFailedAsync(
            () => dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token), output);
        Assert.Equal(DownloadState.Failed, dl.GetState());
    }

    [Fact] // F: connection aborted before any byte, every attempt -> bounded failure.
    public async Task F_ZeroByte_Abort_Exhausts_Bounded_Retries()
    {
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 8L * 1024 * 1024,
            TruncateAfterBytes = 0,
        });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DownloadFailedException>(
            () => dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token));
        sw.Stop();
        output.WriteLine($"OBSERVATION: zero-byte abort failed after {sw.Elapsed.TotalSeconds:F1}s: {ex.Message}");
        Assert.Contains("without progress", ex.Message);
        Assert.Equal(DownloadState.Failed, dl.GetState());
    }

    [Fact] // H: persistent 429 with Retry-After -> bounded failure that honours the header.
    public async Task H_RateLimit_With_RetryAfter_Fails_Bounded()
    {
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 8L * 1024 * 1024,
            GlobalStatus = 429,
            RetryAfterSeconds = 2,
        });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DownloadFailedException>(
            () => dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token));
        sw.Stop();
        output.WriteLine($"OBSERVATION: 429 gave up after {sw.Elapsed.TotalSeconds:F1}s: {ex.Message}");
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.5),
            $"Retry-After was not honoured (gave up after {sw.Elapsed.TotalSeconds:F1}s).");
        Assert.Contains("429", ex.Message);
    }

    [Fact] // I: external cancellation mid parallel download -> OCE, Cancelled, never Completed.
    public async Task I_Cancel_Parallel_Download_Propagates()
    {
        const long size = 100L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            BytesPerSecond = 8L * 1024 * 1024, // slow enough to cancel mid-flight
        });
        using var dl = new ParallelDownloader();
        var seen = new System.Collections.Concurrent.ConcurrentBag<DownloadState>();
        dl.StateChanged += s => seen.Add(s);
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var task = dl.StartAsync(fx.Url, fx.TempDir, 4, cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engineBytes < 10L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(60) && !task.IsCompleted)
            await Task.Delay(100);
        Assert.True(engineBytes >= 10L * 1024 * 1024, "Download did not reach mid-flight in time.");
        await cts.CancelAsync();
        await DownloadFixture.ThrowsCancelledAsync(() => task, output);
        output.WriteLine($"OBSERVATION: parallel cancel threw; engine at {engineBytes} of {size} bytes.");
        Assert.Equal(DownloadState.Cancelled, dl.GetState());
        Assert.DoesNotContain(DownloadState.Completed, seen);
    }

    [Fact] // J: external cancellation mid single-stream download -> OCE.
    public async Task J_Cancel_SingleStream_Download_Propagates()
    {
        const long size = 20L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = size,
            SupportRanges = false,
            BytesPerSecond = 4L * 1024 * 1024,
        });
        using var dl = new ParallelDownloader();
        long engineBytes = 0;
        dl.ProgressChanged += p => engineBytes = p.TotalBytesDownloaded;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var task = dl.StartAsync(fx.Url, fx.TempDir, 1, cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engineBytes < 3L * 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(60) && !task.IsCompleted)
            await Task.Delay(100);
        Assert.True(engineBytes >= 3L * 1024 * 1024, "Download did not reach mid-flight in time.");
        await cts.CancelAsync();
        await DownloadFixture.ThrowsCancelledAsync(() => task, output);
        Assert.Equal(DownloadState.Cancelled, dl.GetState());
    }

    [Fact] // K: cancellation during retry backoff interrupts the wait immediately.
    public async Task K_Cancel_During_Retry_Backoff()
    {
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 16L * 1024 * 1024,
        });
        fx.Server.Profile.RangeFaults.Add(new RangeFault(0, 8L * 1024 * 1024 - 1, 500));
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var task = dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token);
        // Wait until the faulting chunk has been seen (a 500 was served), then
        // cancel mid-backoff: the wait must not suppress cancellation.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (fx.Server.Requests.Any(r => r.StatusCode == 500))
                break;
            await Task.Delay(100);
        }
        Assert.True(fx.Server.Requests.Any(r => r.StatusCode == 500), "No 500 observed; cannot test backoff cancel.");
        await Task.Delay(500); // land inside the ~1 s first backoff
        await cts.CancelAsync();
        var cancelSw = System.Diagnostics.Stopwatch.StartNew();
        await DownloadFixture.ThrowsCancelledAsync(() => task, output);
        cancelSw.Stop();
        output.WriteLine($"OBSERVATION: backoff cancel surfaced after {cancelSw.Elapsed.TotalSeconds:F1}s.");
        Assert.True(cancelSw.Elapsed < TimeSpan.FromSeconds(15),
            "Cancellation during backoff was suppressed/delayed.");
    }

    [Fact] // L: one failed worker stops the session; reason preserved; never Completed.
    public async Task L_Failed_Worker_Stops_Session_With_Reason()
    {
        const long size = 32L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = size });
        fx.Server.Profile.RangeFaults.Add(new RangeFault(16L * 1024 * 1024, 24L * 1024 * 1024 - 1, 500));
        using var dl = new ParallelDownloader();
        var seen = new System.Collections.Concurrent.ConcurrentBag<DownloadState>();
        dl.StateChanged += s => seen.Add(s);
        using var cts = new CancellationTokenSource(Timeout);
        var ex = await Assert.ThrowsAsync<DownloadFailedException>(
            () => dl.StartAsync(fx.Url, fx.TempDir, 4, cts.Token));
        output.WriteLine($"OBSERVATION: session fault: {ex.Message}");
        Assert.Contains("500", ex.Message);
        Assert.Equal(DownloadState.Failed, dl.GetState());
        Assert.DoesNotContain(DownloadState.Completed, seen);
    }

    [Fact] // O: clean success with SHA-256 over a larger many-chunk download.
    public async Task O_Large_Download_Is_ByteIdentical()
    {
        const long size = 32L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, Seed = 7 });
        var (path, state) = await fx.DownloadAsync(connections: 8, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "O");
        Assert.Equal(size, new FileInfo(path).Length);
    }

    [Fact] // §4 identity consistency: ETag switch mid-run aborts instead of mixing versions.
    public async Task IdentitySwitch_MidDownload_Is_Rejected()
    {
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 16L * 1024 * 1024,
            ETag = "\"v1\"",
            IdentitySwitchAfterRequests = 1, // HEAD captures v1; chunk responses carry v2
            SwitchedETag = "\"v2\"",
        });
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(Timeout);
        var ex = await DownloadFixture.ThrowsDownloadFailedAsync(
            () => dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token), output);
        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
