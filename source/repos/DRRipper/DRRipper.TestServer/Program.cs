using DRRipper.TestServer;

// Standalone mode: `dotnet run --project DRRipper.TestServer -- --port 5099 --size-mb 100`
// Serves deterministic content for manual testing. Automated tests and benchmarks
// prefer in-process use via TestDownloadServer.StartAsync (ephemeral port).
var port = 5099;
long sizeMb = 100;
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--size-mb") sizeMb = long.Parse(args[i + 1]);
}

await using var server = await TestDownloadServer.StartOnPortAsync(
    new ServerProfile { FileSize = sizeMb * 1024 * 1024 }, port);
Console.WriteLine($"DRRipper.TestServer serving {sizeMb} MB deterministic payload at {server.FileUrl()}");
Console.WriteLine("Press Ctrl+C to stop.");
await Task.Delay(Timeout.Infinite);
