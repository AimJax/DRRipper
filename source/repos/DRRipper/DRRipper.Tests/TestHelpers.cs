using DRRipper.TestServer;
using Xunit.Abstractions;

namespace DRRipper.Tests;

/// <summary>
/// Shared helpers for baseline characterization tests.
/// Every engine call is bounded by an explicit CancellationToken timeout:
/// the current engine retries some failures forever (AUDIT.md F-11), so no
/// test may await StartAsync without a timeout.
/// </summary>
public sealed class DownloadFixture : IAsyncDisposable
{
    public TestDownloadServer Server { get; }
    public string TempDir { get; }

    private DownloadFixture(TestDownloadServer server, string tempDir)
    {
        Server = server;
        TempDir = tempDir;
    }

    public static async Task<DownloadFixture> CreateAsync(ServerProfile profile)
    {
        var server = await TestDownloadServer.StartAsync(profile);
        var dir = Path.Combine(Path.GetTempPath(), "DRRipperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new DownloadFixture(server, dir);
    }

    public string Url => Server.FileUrl();

    public string ExpectedHash => DeterministicContent.Sha256Hex(
        Server.Profile.FileSize, Server.Profile.Seed,
        Server.Profile.ConstantContent, Server.Profile.ConstantByte);

    /// <summary>Runs one download with an explicit timeout. Never hangs: cancellation bounds the engine.</summary>
    public async Task<(string Path, DownloadState FinalState)> DownloadAsync(
        int connections, TimeSpan timeout, ITestOutputHelper? output = null)
    {
        using var dl = new ParallelDownloader();
        DownloadState last = DownloadState.Idle;
        dl.StateChanged += s => last = s;
        if (output != null)
            dl.ProgressChanged += p => output.WriteLine($"progress={p.ProgressPercentage:F1}% speed={p.CurrentSpeedMBps:F2}MB/s state={p.StatusMessage}");
        using var cts = new CancellationTokenSource(timeout);
        string path;
        try
        {
            path = await dl.StartAsync(Url, TempDir, connections, cts.Token);
        }
        catch (OperationCanceledException)
        {
            output?.WriteLine("OBSERVATION: StartAsync threw OperationCanceledException.");
            throw;
        }
        output?.WriteLine($"OBSERVATION: StartAsync returned; final engine state={last}");
        return (path, last);
    }

    public void AssertFileHash(string path, string expectedHex, string context)
    {
        string actual = DeterministicContent.Sha256HexFile(path);
        Assert.True(string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase),
            $"{context}: SHA-256 mismatch. expected={expectedHex} actual={actual} file={path}");
    }

    public async ValueTask DisposeAsync()
    {
        try { await Server.DisposeAsync(); } catch { }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { }
    }
}

/// <summary>Range-coverage analysis over the server request log.</summary>
public static class RangeLogAnalysis
{
    public sealed record Span(long Start, long End);

    public static List<Span> SuccessfulRanges(TestDownloadServer server)
    {
        var spans = new List<Span>();
        foreach (var r in server.Requests)
        {
            if (r.StatusCode != 206 || string.IsNullOrEmpty(r.RangeHeader))
                continue;
            // "bytes=S-E"
            var spec = r.RangeHeader;
            var eq = spec.IndexOf('=');
            if (eq < 0) continue;
            var parts = spec[(eq + 1)..].Split('-');
            if (parts.Length != 2) continue;
            if (!long.TryParse(parts[0], out var s)) continue;
            long e = parts[1].Length == 0 ? long.MaxValue : long.Parse(parts[1]);
            spans.Add(new Span(s, e));
        }
        return spans;
    }
}
