using DRRipper.TestServer;
using Xunit.Abstractions;

namespace DRRipper.Tests;

/// <summary>
/// T-UNIT-01: chunk-boundary verification, black-box variant.
/// The chunking math lives inline in ParallelDownloader.StartAsync and is not
/// accessible to tests without changing production code (Ticket #002 forbids
/// that), so this test verifies the observable contract instead: the union of
/// successfully served ranges must cover [0, size) exactly once — no gaps,
/// no overlaps. Limitation documented in BASELINE.md.
/// </summary>
public sealed class ChunkBoundaryTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task T_UNIT_01_Ranges_Cover_File_Exactly_Once()
    {
        const long size = 9L * 1024 * 1024; // 8 MB chunk + 1 MB remainder exercises the tail
        await using var fx = await DownloadFixture.CreateAsync(new ServerProfile { FileSize = size });
        var (path, state) = await fx.DownloadAsync(connections: 2, Timeout, output);

        Assert.Equal(DownloadState.Completed, state);
        fx.AssertFileHash(path, fx.ExpectedHash, "T-UNIT-01");

        var spans = RangeLogAnalysis.SuccessfulRanges(fx.Server)
            .OrderBy(s => s.Start).ToList();
        output.WriteLine($"OBSERVATION: {spans.Count} successful range responses for {size} bytes.");
        foreach (var s in spans)
            output.WriteLine($"  range {s.Start}-{s.End}");

        Assert.NotEmpty(spans);
        Assert.Equal(0, spans[0].Start);
        long cursor = 0;
        foreach (var s in spans)
        {
            Assert.True(s.Start == cursor, $"Gap or overlap at {cursor}: next range starts at {s.Start}.");
            cursor = s.End + 1;
        }
        Assert.True(cursor == size, $"Final cursor {cursor} != file size {size}.");
    }
}
