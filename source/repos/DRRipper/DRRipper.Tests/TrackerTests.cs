namespace DRRipper.Tests;

/// <summary>
/// Required tests M (exact-coverage bookkeeping) and N (unrecoverable
/// filesystem classification). Pure unit tests against engine internals;
/// no network, no deadlines needed beyond xUnit defaults.
/// </summary>
public sealed class TrackerAndClassifierTests
{
    [Fact] // M: in-order exact coverage completes the gate.
    public void M_RangeTracker_Exact_Coverage_Completes()
    {
        var t = new RangeTracker(100);
        t.CompleteRange(0, 29);
        t.CompleteRange(30, 69);
        t.CompleteRange(70, 99);
        Assert.Equal(100, t.CoveredBytes);
        Assert.True(t.IsComplete);
    }

    [Fact] // M: duplicates and overlaps never inflate coverage.
    public void M_RangeTracker_Duplicates_And_Overlaps_Do_Not_Inflate()
    {
        var t = new RangeTracker(100);
        t.CompleteRange(0, 49);
        t.CompleteRange(0, 49);      // exact duplicate
        t.CompleteRange(25, 74);     // overlap
        t.CompleteRange(50, 99);
        t.CompleteRange(0, 99);      // superset duplicate
        Assert.Equal(100, t.CoveredBytes);
        Assert.True(t.IsComplete);
    }

    [Fact] // M: out-of-order with adjacency merging still yields exact coverage.
    public void M_RangeTracker_OutOfOrder_Merges_Exactly()
    {
        var t = new RangeTracker(100);
        t.CompleteRange(50, 99);
        t.CompleteRange(0, 49);
        Assert.Equal(100, t.CoveredBytes);
        Assert.True(t.IsComplete);
    }

    [Fact] // M: partial coverage never satisfies the gate.
    public void M_RangeTracker_Partial_Coverage_Is_Incomplete()
    {
        var t = new RangeTracker(100);
        t.CompleteRange(0, 49);
        Assert.Equal(50, t.CoveredBytes);
        Assert.False(t.IsComplete);
    }

    [Fact] // M: out-of-bounds ranges are programming errors, rejected loudly.
    public void M_RangeTracker_OutOfBounds_Throws()
    {
        var t = new RangeTracker(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.CompleteRange(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.CompleteRange(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.CompleteRange(90, 80));
    }

    private sealed class HResultIOException : IOException
    {
        public HResultIOException(int hresult, string message) : base(message) { HResult = hresult; }
    }

    [Fact] // N: disk-full HResults are unrecoverable (must fail fast, never loop).
    public void N_DiskFull_Classified_Unrecoverable()
    {
        Assert.True(ParallelDownloader.IsUnrecoverableFilesystemError(
            new HResultIOException(unchecked((int)0x80070070), "disk full")));
        Assert.True(ParallelDownloader.IsUnrecoverableFilesystemError(
            new HResultIOException(unchecked((int)0x80070027), "handle disk full")));
    }

    [Fact] // N: access-denied is unrecoverable; sharing violations stay bounded-retryable.
    public void N_AccessDenied_Unrecoverable_SharingViolation_Retryable()
    {
        Assert.True(ParallelDownloader.IsUnrecoverableFilesystemError(
            new HResultIOException(unchecked((int)0x80070005), "denied")));
        Assert.True(ParallelDownloader.IsUnrecoverableFilesystemError(
            new UnauthorizedAccessException("denied")));
        Assert.False(ParallelDownloader.IsUnrecoverableFilesystemError(
            new HResultIOException(unchecked((int)0x80070020), "sharing violation")));
    }

    [Fact] // N (integration): a filesystem error at open fails fast within the deadline.
    public async Task N_LockedDestination_Fails_Fast()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new DRRipper.TestServer.ServerProfile { FileSize = 4L * 1024 * 1024 });
        // Hold the resolved destination path open with no sharing: preallocation must fail now, not hang.
        string lockedPath = Path.Combine(fx.TempDir, fx.Server.Profile.FileName);
        using var lockStream = new FileStream(lockedPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var dl = new ParallelDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() => dl.StartAsync(fx.Url, fx.TempDir, 2, cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Filesystem failure was retried instead of failing fast.");
    }
}
