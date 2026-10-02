using System.Diagnostics;
using System.Text;
using DRRipper.TestServer;

namespace DRRipper.Benchmarks;

/// <summary>
/// Deterministic throughput harness: downloads generated payloads from an
/// in-process TestDownloadServer and reports median metrics. Integrity is
/// verified OUTSIDE the timed region; CORRUPT runs never count as results
/// (see performance regression policy in DEVELOPMENT.md).
/// Usage: dotnet run --project DRRipper.Benchmarks -c Release --
///   [--sizes-mb 10,100,1024] [--conns 1,2,4,8,16] [--iters 3] [--out results/bench.md]
/// </summary>
public static class Program
{
    public sealed record RunResult(
        long SizeBytes, int Connections, int Iteration,
        double Seconds, double AvgMBps, double PeakObservedMBps,
        double CpuSeconds, long PeakWorkingSetBytes, int HandleDelta,
        long AllocatedBytes,
        int RequestCount, long BytesTransmitted, long RetransmittedBytes,
        double FinalizationMs, bool IntegrityOk);

    public static async Task<int> Main(string[] args)
    {
        string? schedJobs = GetArgOrNull(args, "--scheduler-jobs");
        if (schedJobs != null)
        {
            return await SchedulerBench.RunAsync(args);
        }

        var sizesMb = GetArg(args, "--sizes-mb", "10,100,1024").Split(',').Select(long.Parse).ToArray();
        var conns = GetArg(args, "--conns", "1,2,4,8,16").Split(',').Select(int.Parse).ToArray();
        int iters = int.Parse(GetArg(args, "--iters", "3"));
        string? outPath = GetArgOrNull(args, "--out");

        var results = new List<RunResult>();
        Console.WriteLine($"Sizes(MB): {string.Join(',', sizesMb)}  Conns: {string.Join(',', conns)}  Iters: {iters}");
        Console.WriteLine("Environment: " + DescribeEnvironment());

        foreach (var sizeMb in sizesMb)
        {
            long size = sizeMb * 1024 * 1024;
            await using var server = await TestDownloadServer.StartAsync(new ServerProfile { FileSize = size });
            Console.WriteLine($"\n== {sizeMb} MB from {server.BaseAddress} ==");
            foreach (var c in conns)
            {
                for (int i = 1; i <= iters; i++)
                {
                    var r = await RunOnce(server, size, c, i);
                    results.Add(r);
                    Console.WriteLine($"  conn={c,2} iter={i}: {r.AvgMBps,8:F1} MB/s  " +
                        $"peakObs={r.PeakObservedMBps,8:F1}  cpu={r.CpuSeconds,5:F1}s  " +
                        $"ws={r.PeakWorkingSetBytes / 1048576,5}MB  alloc={r.AllocatedBytes / 1048576,5}MB  reqs={r.RequestCount,3}  " +
                        $"reTx={r.RetransmittedBytes / 1024,7}KB  final={r.FinalizationMs,6:F0}ms  " +
                        (r.IntegrityOk ? "OK" : "CORRUPT"));
                }
            }
        }

        string md = RenderMarkdown(results, DescribeEnvironment());
        if (outPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            await File.WriteAllTextAsync(outPath, md);
            Console.WriteLine($"\nWrote {outPath}");
        }
        else
        {
            Console.WriteLine("\n" + md);
        }

        int corrupt = results.Count(r => !r.IntegrityOk);
        if (corrupt > 0)
        {
            Console.WriteLine($"WARNING: {corrupt} CORRUPT run(s) excluded from medians.");
            return 2;
        }
        return 0;
    }

    private static async Task<RunResult> RunOnce(TestDownloadServer server, long size, int connections, int iter)
    {
        string dir = Path.Combine(Path.GetTempPath(), "DRRipperBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        server.ClearLog();
        // Force a full GC so working-set/handle deltas reflect the run, not prior garbage.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var proc = Process.GetCurrentProcess();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        int handles0 = proc.HandleCount;
        // Allocations include the in-process test server (same process by design);
        // useful relatively across engine changes, not as absolute engine cost.
        long allocated0 = GC.GetAllocatedBytesForCurrentThread();

        double peakObs = 0;
        // Finalization starts when downloaded bytes stop growing (gate + flush +
        // rename tail). The progress timer keeps ticking through finalize, so the
        // last tick is NOT the transfer end — track the last byte-growth instead.
        DateTime lastByteGrowth = DateTime.UtcNow;
        long maxSeenBytes = 0;
        using var dl = new ParallelDownloader();
        dl.ProgressChanged += p =>
        {
            if (p.CurrentSpeedMBps > peakObs) peakObs = p.CurrentSpeedMBps;
            if (p.TotalBytesDownloaded > maxSeenBytes)
            {
                maxSeenBytes = p.TotalBytesDownloaded;
                lastByteGrowth = DateTime.UtcNow;
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var sw = Stopwatch.StartNew();
        string path = await dl.StartAsync(server.FileUrl(), dir, connections, cts.Token);
        sw.Stop();
        DateTime returned = DateTime.UtcNow;
        proc.Refresh();

        double seconds = sw.Elapsed.TotalSeconds;
        var log = server.Requests;
        long transmitted = log.Sum(r => r.BytesWritten);

        // Integrity OUTSIDE the timed region.
        string actual = DeterministicContent.Sha256HexFile(path);
        string expected = DeterministicContent.Sha256Hex(size, server.Profile.Seed,
            server.Profile.ConstantContent, server.Profile.ConstantByte);
        bool ok = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        var result = new RunResult(
            size, connections, iter,
            seconds, size / 1048576.0 / Math.Max(seconds, 1e-9), peakObs,
            (proc.TotalProcessorTime - cpu0).TotalSeconds,
            proc.PeakWorkingSet64, proc.HandleCount - handles0,
            GC.GetAllocatedBytesForCurrentThread() - allocated0,
            log.Count, transmitted, Math.Max(0, transmitted - size),
            (returned - lastByteGrowth).TotalMilliseconds, ok);

        try { Directory.Delete(dir, recursive: true); } catch { }
        return result;
    }

    private static void AppendMedianRow(StringBuilder sb, List<RunResult> runs, long size, int conn)
    {
        var ok = runs.Where(r => r.SizeBytes == size && r.Connections == conn && r.IntegrityOk).ToList();
        if (ok.Count == 0)
        {
            sb.AppendLine($"| {size / 1048576} | {conn} | CORRUPT | — | — | — | — | — |");
            return;
        }
        static double Med(IEnumerable<double> xs)
        {
            var a = xs.OrderBy(x => x).ToArray();
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
        }
        sb.AppendLine($"| {size / 1048576} | {conn} | {Med(ok.Select(r => r.AvgMBps)):F1} " +
            $"| {Med(ok.Select(r => r.PeakObservedMBps)):F1} | {Med(ok.Select(r => r.CpuSeconds)):F2} " +
            $"| {Med(ok.Select(r => (double)r.PeakWorkingSetBytes / 1048576)):F0} " +
            $"| {Med(ok.Select(r => (double)r.AllocatedBytes / 1048576)):F0} " +
            $"| {Med(ok.Select(r => (double)r.RequestCount)):F0} " +
            $"| {Med(ok.Select(r => (double)r.RetransmittedBytes / 1024)):F0} " +
            $"| {Med(ok.Select(r => r.FinalizationMs)):F0} |");
    }

    private static string RenderMarkdown(List<RunResult> results, string env)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DRRipper benchmark run");
        sb.AppendLine();
        sb.AppendLine($"- Date (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Environment: {env}");
        sb.AppendLine($"- Iterations per cell: {(results.Select(r => r.Iteration).DefaultIfEmpty(0).Max())}, median reported; CORRUPT runs excluded.");
        sb.AppendLine();
        sb.AppendLine("| Size (MB) | Conns | Median MB/s | Median peak-obs MB/s | Median CPU s | Median WS MB | Median alloc MB | Median reqs | Median reTx KB | Median final ms |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var size in results.Select(r => r.SizeBytes).Distinct().OrderBy(x => x))
            foreach (var c in results.Select(r => r.Connections).Distinct().OrderBy(x => x))
                AppendMedianRow(sb, results, size, c);
        return sb.ToString();
    }

    private static string DescribeEnvironment()
    {
        try
        {
            string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown-cpu";
            return $"OS={Environment.OSVersion} x64={Environment.Is64BitProcess} cores={Environment.ProcessorCount} cpu={cpu} " +
                $"dotnet={Environment.Version} localhost(Kestrel->downloader, same host)";
        }
        catch { return "unknown"; }
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
