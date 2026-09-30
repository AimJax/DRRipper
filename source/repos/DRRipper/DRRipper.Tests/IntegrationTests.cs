using DRRipper.TestServer;
using Xunit.Abstractions;

namespace DRRipper.Tests;

/// <summary>
/// Baseline integration tests against the deterministic local server.
/// Tests assert CORRECT behaviour. Where AUDIT.md predicts failure, the test is
/// tagged Category=KnownFailure and left failing — failures are documented in
/// BASELINE.md, never disguised.
/// </summary>
public sealed class IntegrationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task T_INT_01_Normal_Download()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 20L * 1024 * 1024 });
        var (path, state) = await fx.DownloadAsync(connections: 4, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-01");
        Assert.Equal(20L * 1024 * 1024, new FileInfo(path).Length);
    }

    /// <summary>
    /// T-INT-02: well-formed gzipped range responses over highly compressible content.
    /// Result: PASS. The engine survives this case because HttpClient strips
    /// Content-Length when it transparently decompresses, so the engine falls back
    /// to the requested span (chunk.end - chunk.start + 1), which is exact.
    /// Residual hazard (AUDIT.md F-08, refined in BASELINE.md): the span fallback
    /// only saves cases where Content-Length is absent/stripped; any party that
    /// reports an encoded length as an identity length still corrupts offsets.
    /// </summary>
    [Fact]
    public async Task T_INT_02_Gzipped_Range_Responses()
    {
        // Server honours ranges but gzip-encodes bodies (AUDIT.md F-08), over
        // HIGHLY COMPRESSIBLE content so encoded vs decoded lengths diverge.
        // Correct behaviour: byte-identical file. Current engine trusts the
        // encoded Content-Length while writing decoded bytes (clamped) -> corrupt.
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 8L * 1024 * 1024,
            GzipResponses = true,
            ConstantContent = true,
        });
        var (path, state) = await fx.DownloadAsync(connections: 2, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        output.WriteLine($"OBSERVATION: {fx.Server.RequestCount} requests, {fx.Server.TotalBytesTransmitted} bytes transmitted.");
        foreach (var r in fx.Server.Requests.Take(10))
            output.WriteLine($"  {r.Method} range={r.RangeHeader} status={r.StatusCode} bytes={r.BytesWritten}");
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-02");
    }

    [Fact]
    [Trait("Category", "KnownFailure")]
    public async Task T_INT_03_Short_Chunk_Body_With_Clean_EOF()
    {
        // Every range response gracefully ends after 1 MB of an 8 MB chunk while
        // retaining the original Content-Range span (AUDIT.md F-01: clean EOF with
        // written > 0 is accepted as success). Correct behaviour: retry and finish
        // byte-identical.
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 20L * 1024 * 1024,
            ShortBodyBytes = 1L * 1024 * 1024,
        });
        var (path, state) = await fx.DownloadAsync(connections: 2, Timeout, output);
        output.WriteLine($"OBSERVATION: state after short-body transfer = {state}");
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-03");
        Assert.Equal(20L * 1024 * 1024, new FileInfo(path).Length);
    }

    [Fact]
    public async Task T_INT_03b_Aborted_Chunk_Retries_To_Completion()
    {
        // Same truncation, but delivered as a connection abort (RST) instead of
        // clean EOF: the client observes an error, the engine resumes the chunk
        // from the last written byte, and the file completes byte-identical.
        // PASS documents that the in-run retry path works for error-terminated
        // bodies (contrast T-INT-03, which only fails on clean EOF).
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile
        {
            FileSize = 20L * 1024 * 1024,
            TruncateAfterBytes = 1L * 1024 * 1024,
        });
        var (path, state) = await fx.DownloadAsync(connections: 2, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-03b");
    }

    [Fact]
    [Trait("Category", "KnownFailure")]
    public async Task T_INT_04_Persistent_500_On_One_Range()
    {
        // One 8 MB chunk always answers 500 (AUDIT.md F-02: infinite transient retry).
        // Bounded by a 35 s timeout so the test cannot hang; correct behaviour would
        // be a fast, explicit failure — not silent retry, and never Completed.
        const long size = 24L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = size });
        fx.Server.Profile.RangeFaults.Add(new RangeFault(8L * 1024 * 1024, 16L * 1024 * 1024 - 1, 500));
        var (path, state) = await fx.DownloadAsync(connections: 3, TimeSpan.FromSeconds(35), output);
        output.WriteLine($"OBSERVATION: state after 35 s of persistent 500 = {state}");
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-04");
    }

    [Fact]
    public async Task T_INT_05_Wrong_ContentRange_Total_Is_Ignored()
    {
        // Server returns 206 with a lying Content-Range total (AUDIT.md P-03).
        // Characterizes that the engine never validates it: file is still correct.
        // PASS here documents a validation gap, not correctness of the gap.
        const long size = 8L * 1024 * 1024;
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = size, WrongContentRangeTotal = size * 2 });
        var (path, state) = await fx.DownloadAsync(connections: 2, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-05");
        output.WriteLine("OBSERVATION: wrong Content-Range total went undetected (P-03).");
    }

    [Fact]
    public async Task T_INT_06_No_Range_Support_Falls_Back_To_Single_Stream()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024, SupportRanges = false });
        var (path, state) = await fx.DownloadAsync(connections: 4, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-06");
    }

    [Fact]
    public async Task T_INT_07_Unknown_Length_Chunked_Download()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 8L * 1024 * 1024, SendContentLength = false });
        var (path, state) = await fx.DownloadAsync(connections: 4, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-07");
    }

    [Fact]
    public async Task T_INT_08_Throttled_Server_Completes()
    {
        await using var fx = await DownloadFixture.CreateAsync(
            new ServerProfile { FileSize = 10L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 });
        var (path, state) = await fx.DownloadAsync(connections: 2, Timeout, output);
        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "T-INT-08");
    }
}
