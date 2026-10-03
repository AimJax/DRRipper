using System.Diagnostics;
using DRRipper.Scheduler;

namespace DRRipper.Benchmarks;

/// <summary>
/// Real-world URL benchmark (Ticket #008 §43): downloads a user-supplied URL
/// with several connection counts and reports throughput + saturation curves.
/// The URL is NEVER persisted, NEVER logged with its query string, and never
/// committed anywhere — pass it at runtime only. No integrity hash is known
/// (unknown content), so results report bytes + status, never OK/CORRUPT.
/// Usage: dotnet run --project DRRipper.Benchmarks -c Release --
///   --url "https://host/file" [--conns-list 4,8,16,32] [--mode balanced|maximum]
/// </summary>
public static class UrlBench
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? rawUrl = GetArgOrNull(args, "--url");
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            Console.WriteLine("Missing --url <http(s) URL>.");
            return 2;
        }
        var connsList = GetArg(args, "--conns-list", "4,8,16,32").Split(',').Select(int.Parse).ToArray();
        var mode = GetArg(args, "--mode", "maximum");
        SchedulerBench.ApplyStackKnobs(args);

        string redacted = BrowserBridge.BrowserValidation.RedactUrl(rawUrl);
        Console.WriteLine($"UrlBench: url={redacted} conns=[{string.Join(',', connsList)}] mode={mode}");
        Console.WriteLine($"Environment: OS={Environment.OSVersion} cores={Environment.ProcessorCount} dotnet={Environment.Version}");

        foreach (var conns in connsList)
        {
            string dir = Path.Combine(Path.GetTempPath(), "DRRipperUrlBench", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var dl = new ParallelDownloader
                {
                    AdaptiveConcurrency = string.Equals(mode, "maximum", StringComparison.OrdinalIgnoreCase),
                };
                var samples = new List<(double Sec, double MB)>();
                var swGlobal = Stopwatch.StartNew();
                double peak = 0;
                dl.ProgressChanged += p =>
                {
                    if (p.CurrentSpeedMBps > peak) peak = p.CurrentSpeedMBps;
                    lock (samples) samples.Add((swGlobal.Elapsed.TotalSeconds, p.TotalBytesDownloaded / 1048576.0));
                };
                var swTtfb = Stopwatch.StartNew();
                bool firstByte = false;
                dl.ProgressChanged += p =>
                {
                    if (!firstByte && p.TotalBytesDownloaded > 0)
                    {
                        firstByte = true;
                        swTtfb.Stop();
                    }
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                var sw = Stopwatch.StartNew();
                string path;
                try
                {
                    path = await dl.StartAsync(rawUrl, dir, Math.Clamp(conns, 1, 64), cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  conn={conns,2}: FAILED ({ex.GetType().Name}: {TrimError(ex.Message)}) url={redacted}");
                    continue;
                }
                sw.Stop();
                long bytes;
                try { bytes = new FileInfo(path).Length; } catch { bytes = -1; }
                double mb = bytes / 1048576.0;
                // Saturation curve: time to reach 50%/90% of final bytes.
                double t50 = InterpolateTime(samples, mb * 0.5);
                double t90 = InterpolateTime(samples, mb * 0.9);
                Console.WriteLine($"  conn={conns,2}: {mb / Math.Max(sw.Elapsed.TotalSeconds, 1e-9),8:F1} MB/s  " +
                    $"ttfb={swTtfb.Elapsed.TotalSeconds,6:F2}s  t50={t50,7:F1}s  t90={t90,7:F1}s  " +
                    $"peak={peak,7:F1}  bytes={bytes}  url={redacted}");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        return 0;
    }

    private static double InterpolateTime(List<(double Sec, double MB)> samples, double targetMB)
    {
        lock (samples)
        {
            foreach (var (sec, mb) in samples)
                if (mb >= targetMB) return sec;
            return double.NaN;
        }
    }

    private static string TrimError(string message)
    {
        if (string.IsNullOrEmpty(message)) return "unknown error";
        // Never leak query strings from error text.
        var q = message.IndexOf('?');
        var shorted = q >= 0 ? message.Substring(0, q) + "?[redacted]" : message;
        shorted = shorted.Replace('\r', ' ').Replace('\n', ' ');
        return shorted.Length > 200 ? shorted.Substring(0, 200) : shorted;
    }

    private static string GetArg(string[] args, string name, string @default)
        => GetArgOrNull(args, name) ?? @default;

    private static string? GetArgOrNull(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
