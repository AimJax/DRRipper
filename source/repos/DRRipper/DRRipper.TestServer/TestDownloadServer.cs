using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRRipper.TestServer;

/// <summary>
/// Deterministic in-process Kestrel server for downloader tests.
/// Binds to an ephemeral localhost port; exposes the request log for assertions.
/// </summary>
public sealed class TestDownloadServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<RequestRecord> _log = [];
    private readonly object _logLock = new();

    public ServerProfile Profile { get; } = ServerProfile.Default();
    public string BaseAddress { get; private set; } = string.Empty;

    private TestDownloadServer(WebApplication app)
    {
        _app = app;
    }

    public static Task<TestDownloadServer> StartAsync(ServerProfile? profile = null, CancellationToken ct = default)
        => StartCoreAsync(profile, o =>
        {
            // Dynamic ports are unsupported on ListenLocalhost; bind both loopbacks explicitly.
            o.Listen(System.Net.IPAddress.Loopback, 0);
            o.Listen(System.Net.IPAddress.IPv6Loopback, 0);
        }, ct);

    /// <summary>Standalone-friendly startup on a fixed localhost port.</summary>
    public static Task<TestDownloadServer> StartOnPortAsync(ServerProfile? profile, int port, CancellationToken ct = default)
        => StartCoreAsync(profile, o => o.ListenLocalhost(port), ct);

    private static async Task<TestDownloadServer> StartCoreAsync(
        ServerProfile? profile, Action<KestrelServerOptions> listen, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(listen);
        // Keep logs quiet; test output owns the narrative.
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new TestDownloadServer(app);
        if (profile != null)
            CopyProfile(profile, server.Profile);

        app.MapGet("/file/{*name}", (HttpContext ctx) => ServeAsync(ctx, server.Profile, server.Log));
        app.MapMethods("/file/{*name}", new[] { HttpMethods.Head }, (HttpContext ctx) => ServeAsync(ctx, server.Profile, server.Log));
        app.MapGet("/__log", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            List<RequestRecord> snapshot;
            lock (server._logLock) snapshot = [.. server._log];
            return ctx.Response.WriteAsync(JsonSerializer.Serialize(snapshot));
        });
        app.MapPost("/__reset", (HttpContext ctx) =>
        {
            server.ClearLog();
            return Task.CompletedTask;
        });

        await app.StartAsync(ct);
        // Routes capture 'server', so post-start Profile mutations by tests take effect.
        server.BaseAddress = app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("Server bound to no addresses.");
        return server;
    }

    private static void CopyProfile(ServerProfile from, ServerProfile to)
    {
        to.FileSize = from.FileSize;
        to.Seed = from.Seed;
        to.FileName = from.FileName;
        to.ETag = from.ETag;
        to.LastModified = from.LastModified;
        to.SupportRanges = from.SupportRanges;
        to.SendContentLength = from.SendContentLength;
        to.TruncateAfterBytes = from.TruncateAfterBytes;
        to.BytesPerSecond = from.BytesPerSecond;
        to.InitialLatency = from.InitialLatency;
        to.GlobalStatus = from.GlobalStatus;
        to.RangeFaults.AddRange(from.RangeFaults);
        to.GzipResponses = from.GzipResponses;
        to.WrongContentRangeTotal = from.WrongContentRangeTotal;
        to.ShortBodyBytes = from.ShortBodyBytes;
        to.ConstantContent = from.ConstantContent;
        to.ConstantByte = from.ConstantByte;
    }

    public string FileUrl(string? name = null) => $"{BaseAddress}/file/{Uri.EscapeDataString(name ?? Profile.FileName)}";

    public IReadOnlyList<RequestRecord> Requests
    {
        get { lock (_logLock) return [.. _log]; }
    }

    public long TotalBytesTransmitted
    {
        get { lock (_logLock) return _log.Sum(r => r.BytesWritten); }
    }

    public int RequestCount
    {
        get { lock (_logLock) return _log.Count; }
    }

    public void ClearLog()
    {
        lock (_logLock) _log.Clear();
    }

    private void Log(RequestRecord record)
    {
        lock (_logLock) _log.Add(record);
    }

    private async Task HandleFileAsync(HttpContext ctx)
    {
        await ServeAsync(ctx, Profile, Log);
    }

    private static async Task ServeAsync(HttpContext ctx, ServerProfile profile, Action<RequestRecord> log)
    {
        var isHead = HttpMethods.IsHead(ctx.Request.Method);
        var rangeHeader = ctx.Request.Headers.Range.FirstOrDefault();
        var connectionId = ctx.Connection.Id;
        int status = 200;
        long bytesWritten = 0;

        try
        {
            if (profile.GlobalStatus is int global)
            {
                status = global;
                ctx.Response.StatusCode = status;
                return;
            }

            long size = profile.FileSize;
            ctx.Response.Headers.AcceptRanges = profile.SupportRanges ? "bytes" : "none";
            ctx.Response.Headers.ETag = profile.ETag;
            ctx.Response.Headers.LastModified = profile.LastModified.ToString("R");
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{profile.FileName}\"";

            long start = 0, end = size - 1;
            bool isRange = false;

            if (profile.SupportRanges && !string.IsNullOrEmpty(rangeHeader))
            {
                if (!TryParseRange(rangeHeader, size, out start, out end))
                {
                    status = 416; // Range Not Satisfiable
                    ctx.Response.StatusCode = status;
                    ctx.Response.Headers.ContentRange = $"bytes */{size}";
                    return;
                }
                isRange = true;

                foreach (var fault in profile.RangeFaults)
                {
                    if (start <= fault.End && end >= fault.Start)
                    {
                        status = fault.Status;
                        ctx.Response.StatusCode = status;
                        return;
                    }
                }
            }

            long length = Math.Max(0, end - start + 1);
            status = isRange ? 206 : 200;
            ctx.Response.StatusCode = status;
            if (isRange)
            {
                long reportedTotal = profile.WrongContentRangeTotal ?? size;
                ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{reportedTotal}";
            }

            byte[] body = DeterministicContent.Slice(start, (int)length, profile.Seed,
                profile.ConstantContent, profile.ConstantByte);

            if (!isHead && profile.ShortBodyBytes is long cap && body.Length > cap)
            {
                // Broken-server emulation: short body, truthful short Content-Length,
                // original Content-Range span retained, graceful completion (clean EOF).
                body = body[0..(int)cap];
            }

            bool gzip = profile.GzipResponses && AcceptsGzip(ctx);
            if (gzip)
            {
                using var ms = new MemoryStream();
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    gz.Write(body, 0, body.Length);
                body = ms.ToArray();
                ctx.Response.Headers.ContentEncoding = "gzip";
            }

            if (profile.SendContentLength)
                ctx.Response.ContentLength = body.Length;

            if (isHead)
                return;

            if (profile.InitialLatency is { } latency)
                await Task.Delay(latency, ctx.RequestAborted);

            long budget = profile.TruncateAfterBytes ?? long.MaxValue;
            long throttle = profile.BytesPerSecond ?? long.MaxValue;
            const int chunk = 64 * 1024;
            long offset = 0;
            while (offset < body.Length && offset < budget)
            {
                ctx.RequestAborted.ThrowIfCancellationRequested();
                int n = (int)Math.Min(chunk, Math.Min(body.Length - offset, budget - offset));
                long sliceStart = Environment.TickCount64;
                await ctx.Response.Body.WriteAsync(body.AsMemory((int)offset, n), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                offset += n;
                bytesWritten += n;
                if (throttle != long.MaxValue)
                {
                    double msOwed = n * 1000.0 / throttle;
                    double msSpent = Environment.TickCount64 - sliceStart;
                    if (msOwed > msSpent)
                        await Task.Delay(TimeSpan.FromMilliseconds(msOwed - msSpent), ctx.RequestAborted);
                }
            }

            if (offset < body.Length)
            {
                // Deliberate truncation: destroy the connection mid-body.
                ctx.Abort();
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away; nothing to do.
        }
        catch (Exception)
        {
            // Never leak test-server faults as hangs; abort and record.
            try { ctx.Abort(); } catch { }
        }
        finally
        {
            log(new RequestRecord(DateTimeOffset.UtcNow, ctx.Request.Method, ctx.Request.Path.ToString(),
                rangeHeader, status, bytesWritten, connectionId));
        }
    }

    private static bool AcceptsGzip(HttpContext ctx)
    {
        foreach (var v in ctx.Request.Headers.AcceptEncoding)
        {
            if (v != null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryParseRange(string? header, long size, out long start, out long end)
    {
        start = 0; end = size - 1;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;
        var spec = header["bytes=".Length..].Trim();
        // Single-range only; anything else is unsatisfiable for our purposes.
        if (spec.Contains(','))
            return false;
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            // Suffix range: last N bytes.
            if (!long.TryParse(last, out var suffix) || suffix <= 0)
                return false;
            start = Math.Max(0, size - suffix);
            end = size - 1;
            return true;
        }
        if (!long.TryParse(first, out start) || start < 0)
            return false;
        if (last.Length == 0)
        {
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(last, out end) || end < start)
                return false;
            end = Math.Min(end, size - 1);
        }
        if (start >= size)
            return false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
    }
}
