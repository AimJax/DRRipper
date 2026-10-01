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

    /// <summary>Recovery metadata path for a fixture's download.</summary>
    public static string PartMetaPath(DownloadFixture fx) =>
        Path.Combine(fx.TempDir, fx.Server.Profile.FileName + ".part.drmeta");

    /// <summary>Parses "bytes=S-E" (open-ended E defaults to size-1).</summary>
    public static (long S, long E) ParseRangeHeader(string header, long size)
    {
        var spec = header[(header.IndexOf('=') + 1)..];
        var parts = spec.Split('-');
        long s = long.Parse(parts[0]);
        long e = parts[1].Length == 0 ? size - 1 : long.Parse(parts[1]);
        return (s, e);
    }

    /// <summary>Reads the persisted prefix offset from a v2 metadata file.</summary>
    public static long ReadPrefixOffset(string metaPath)
    {
        string json = File.ReadAllText(metaPath);
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"PrefixOffset\":(\\d+)");
        Assert.True(m.Success, $"No PrefixOffset in metadata at {metaPath}.");
        return long.Parse(m.Groups[1].Value);
    }

    /// <summary>
    /// Cancellation surfaces as OperationCanceledException or a derived type
    /// (e.g. TaskCanceledException from HttpClient). Assert the family.
    /// </summary>
    public static async Task<OperationCanceledException> ThrowsCancelledAsync(
        Func<Task<string>> download, Xunit.Abstractions.ITestOutputHelper? output = null)
    {
        try
        {
            string path = await download();
        }
        catch (Exception ex) when (ex is OperationCanceledException oce)
        {
            output?.WriteLine($"OBSERVATION: StartAsync threw {ex.GetType().Name} (cancellation family).");
            return oce;
        }
        Assert.Fail("Expected StartAsync to throw OperationCanceledException, but it returned successfully.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// xUnit's ThrowsAsync demands an exact type; the engine throws
    /// DownloadFailedException subclasses (e.g. NonRetryableHttpException).
    /// Assert the family, not the exact class, and return the failure.
    /// </summary>
    public static async Task<DownloadFailedException> ThrowsDownloadFailedAsync(
        Func<Task<string>> download, Xunit.Abstractions.ITestOutputHelper? output = null)
    {
        try
        {
            string path = await download();
        }
        catch (Exception ex) when (ex is DownloadFailedException dfe)
        {
            output?.WriteLine($"OBSERVATION: StartAsync threw {ex.GetType().Name}: {ex.Message}");
            return dfe;
        }
        Assert.Fail("Expected StartAsync to throw DownloadFailedException, but it returned successfully.");
        throw new InvalidOperationException("unreachable");
    }

    public async ValueTask DisposeAsync()
    {
        try { await Server.DisposeAsync(); } catch { }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { }
    }
}

/// <summary>Range-coverage analysis over the server request log.</summary>
public static class RangeLogAnalysis
{    public sealed record Span(long Start, long End);

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
