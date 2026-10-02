using System.Diagnostics;
using System.Text;
using DRRipper.Scheduler;
using DRRipper.TestServer;

namespace DRRipper.Benchmarks;

/// <summary>
/// Scheduler overhead benchmark (Ticket #005 §32): N concurrent files through
/// DownloadScheduler against a local server. Reports aggregate + per-file
/// throughput, CPU, working set, handles, DB writes/sec, and admission latency.
/// Compare jobs=1 against the Ticket #004.1 raw-engine baseline.
/// </summary>
public static class SchedulerBench
{
    public sealed record SchedResult(int Jobs, int Iteration, double Seconds, double AggMBps,
        double PerFileMBps, double CpuSeconds, long WsMB, int HandleDelta,
        double DbWritesPerSec, double AdmitLatencyMs, int Requests, double TransmittedMB, bool AllOk);

    public static async Task<int> RunAsync(string[] args)
    {
        var jobsList = GetArg(args, "--scheduler-jobs", "1,2,3,4,8").Split(',').Select(int.Parse).ToArray();
        long sizeMb = long.Parse(GetArg(args, "--size-mb", "100"));
        int conns = int.Parse(GetArg(args, "--conns", "4"));
        int iters = int.Parse(GetArg(args, "--iters", "3"));
        string? outPath = GetArgOrNull(args, "--out");

        Console.WriteLine($"SchedulerBench: size={sizeMb}MB/file conns={conns} jobs=[{string.Join(',', jobsList)}] iters={iters}");
        var results = new List<SchedResult>();
        foreach (var jobs in jobsList)
        {
            for (int i = 1; i <= iters; i++)
            {
                var r = await RunOnce(sizeMb * 1024 * 1024, conns, jobs, i);
                results.Add(r);
                Console.WriteLine($"  jobs={jobs,2} iter={i}: agg={r.AggMBps,7:F1} MB/s  " +
                    $"per-file={r.PerFileMBps,7:F1}  cpu={r.CpuSeconds,5:F1}s  ws={r.WsMB,5}MB  " +
                    $"hdlΔ={r.HandleDelta,4}  dbw={r.DbWritesPerSec,5:F1}/s  admit={r.AdmitLatencyMs,6:F0}ms  " +
                    $"reqs={r.Requests,3}  tx={r.TransmittedMB,7:F1}MB  " +
                    (r.AllOk ? "OK" : "CORRUPT"));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# DRRipper scheduler benchmark");
        sb.AppendLine();
        sb.AppendLine($"- Date (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- File size: {sizeMb} MB, conns/file: {conns}, iters: {iters}, median reported.");
        sb.AppendLine();
        sb.AppendLine("| Jobs | Median agg MB/s | Median per-file MB/s | Median CPU s | Median WS MB | Median hdlΔ | Median dbw/s | Median admit ms |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var jobs in jobsList)
        {
            var ok = results.Where(r => r.Jobs == jobs && r.AllOk).ToList();
            if (ok.Count == 0) { sb.AppendLine($"| {jobs} | CORRUPT | — | — | — | — | — | — |"); continue; }
            static double Med(IEnumerable<double> xs)
            {
                var a = xs.OrderBy(x => x).ToArray();
                return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
            }
            sb.AppendLine($"| {jobs} | {Med(ok.Select(r => r.AggMBps)):F1} | {Med(ok.Select(r => r.PerFileMBps)):F1} " +
                $"| {Med(ok.Select(r => r.CpuSeconds)):F2} | {Med(ok.Select(r => (double)r.WsMB)):F0} " +
                $"| {Med(ok.Select(r => (double)r.HandleDelta)):F0} | {Med(ok.Select(r => r.DbWritesPerSec)):F1} " +
                $"| {Med(ok.Select(r => r.AdmitLatencyMs)):F0} |");
        }
        if (outPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            await File.WriteAllTextAsync(outPath, sb.ToString());
            Console.WriteLine($"\nWrote {outPath}");
        }
        else Console.WriteLine("\n" + sb);
        return results.Any(r => !r.AllOk) ? 2 : 0;
    }

    private static async Task<SchedResult> RunOnce(long sizeBytes, int conns, int jobs, int iter)
    {
        await using var server = await TestDownloadServer.StartAsync(new ServerProfile { FileSize = sizeBytes });
        string dir = Path.Combine(Path.GetTempPath(), "DRRipperSchedBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dl = Path.Combine(dir, "dl");
        Directory.CreateDirectory(dl);
        string db = Path.Combine(dir, "queue.db");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        int handles0 = proc.HandleCount;

        var store = await JobStore.CreateAsync(db);
        long writes0 = store.WriteCount;
        using var sched = new DownloadScheduler(store, new SchedulerSettings
        {
            ActiveDownloadLimit = jobs,
            GlobalConnectionBudget = 16,
            PerHostConnectionBudget = 8,
        });
        await sched.StartAsync();
        var swAdmit = Stopwatch.StartNew();
        for (int j = 0; j < jobs; j++)
            await sched.EnqueueAsync(server.FileUrl() + $"?bench={iter}-{j}", dl, connectionsPerFile: conns);
        // Admission latency: enqueue -> first job Downloading.
        double admitMs = -1;
        {
            var swA = Stopwatch.StartNew();
            while (swA.Elapsed < TimeSpan.FromSeconds(60))
            {
                var all = await store.GetAllJobsAsync();
                if (all.Any(x => x.State is JobState.Downloading or JobState.Completed)) { admitMs = swA.Elapsed.TotalMilliseconds; break; }
                await Task.Delay(25);
            }
        }
        swAdmit.Stop();

        var sw = Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20)))
        {
            while (sw.Elapsed < TimeSpan.FromMinutes(19))
            {
                var all = await store.GetAllJobsAsync();
                if (all.Count == jobs && all.All(x => x.IsTerminal)) break;
                await Task.Delay(25, cts.Token);
            }
        }
        sw.Stop(); // timed region ends at terminal detection; integrity below is untimed (policy: CORRUPT excluded, never counted).
        bool allOk = false;
        {
            var all2 = await store.GetAllJobsAsync();
            allOk = all2.Count == jobs && all2.All(x => x.State == JobState.Completed);
            if (allOk)
            {
                string expected = DeterministicContent.Sha256Hex(sizeBytes, server.Profile.Seed,
                    server.Profile.ConstantContent, server.Profile.ConstantByte);
                foreach (var j in all2)
                {
                    if (!string.Equals(DeterministicContent.Sha256HexFile(j.ResolvedFinalPath!), expected, StringComparison.OrdinalIgnoreCase))
                    { allOk = false; break; }
                }
            }
        }
        proc.Refresh();
        double seconds = sw.Elapsed.TotalSeconds;
        double totalMB = jobs * sizeBytes / 1048576.0;
        double dbw = (store.WriteCount - writes0) / Math.Max(seconds, 1e-9);
        int reqs = server.Requests.Count;
        double txMB = server.TotalBytesTransmitted / 1048576.0;
        var result = new SchedResult(jobs, iter, seconds, totalMB / Math.Max(seconds, 1e-9),
            totalMB / Math.Max(seconds, 1e-9) / Math.Max(jobs, 1),
            (proc.TotalProcessorTime - cpu0).TotalSeconds, proc.PeakWorkingSet64 / 1048576,
            proc.HandleCount - handles0, dbw, admitMs, reqs, txMB, allOk);
        try { await sched.StopAsync(); } catch { }
        try { store.Dispose(); } catch { }
        try { Directory.Delete(dir, recursive: true); } catch { }
        return result;
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
