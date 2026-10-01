using DRRipper;

namespace DRRipper.RecoveryDriver;

/// <summary>
/// Dedicated child-process driver for genuine process-kill recovery tests
/// (Ticket #004 §13). The test server lives in the PARENT test process, so
/// killing this process loses all in-memory session state while on-disk
/// (.part + .drmeta) state must suffice for recovery.
/// Protocol (stdout, flush per line): READY | PROGRESS &lt;bytes&gt; &lt;total&gt; |
/// PAUSED | DONE &lt;path&gt; | ERROR &lt;message&gt;
/// Args: --url &lt;url&gt; --dir &lt;dir&gt; --conns &lt;n&gt;
///       [--pause-after-bytes &lt;n&gt;] [--checkpoint-interval-ms &lt;n&gt;]
/// With --pause-after-bytes, pauses once at threshold, prints PAUSED, then
/// sleeps until killed (crash-during-pause scenario).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        string? url = Arg(args, "--url");
        string? dir = Arg(args, "--dir");
        int conns = int.Parse(Arg(args, "--conns") ?? "2");
        long pauseAfter = long.Parse(Arg(args, "--pause-after-bytes") ?? "-1");
        int ckptMs = int.Parse(Arg(args, "--checkpoint-interval-ms") ?? "-1");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(dir)) return 2;

        using var dl = new ParallelDownloader();
        if (ckptMs > 0) dl.CheckpointInterval = TimeSpan.FromMilliseconds(ckptMs);
        long lastReport = 0;
        bool paused = false;
        dl.ProgressChanged += p =>
        {
            if (p.TotalBytesDownloaded - lastReport >= 1024 * 1024)
            {
                lastReport = p.TotalBytesDownloaded;
                Console.WriteLine($"PROGRESS {p.TotalBytesDownloaded} {p.TotalBytes}");
            }
            if (!paused && pauseAfter > 0 && p.TotalBytesDownloaded >= pauseAfter)
            {
                paused = true;
                try
                {
                    dl.Pause();
                    Console.WriteLine("PAUSED");
                }
                catch (Exception ex) { Console.WriteLine($"ERROR pause: {ex.Message}"); }
            }
        };
        Console.WriteLine("READY");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            string path = await dl.StartAsync(url, dir, conns, cts.Token);
            Console.WriteLine($"DONE {path}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("ERROR cancelled");
            return 3;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
