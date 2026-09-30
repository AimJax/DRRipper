namespace DRRipper.TestServer;

/// <summary>Per-range error injection: requests whose range overlaps [Start, End] get Status.</summary>
public sealed record RangeFault(long Start, long End, int Status);

/// <summary>One observed HTTP request and its outcome.</summary>
public sealed record RequestRecord(
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    string? RangeHeader,
    int StatusCode,
    long BytesWritten,
    string ConnectionId);

/// <summary>
/// Mutable behaviour profile. Tests mutate this in-process between runs
/// (interruption, identity-switch scenarios). All members are plain data.
/// </summary>
public sealed class ServerProfile
{
    public long FileSize { get; set; } = 20L * 1024 * 1024;
    public int Seed { get; set; } = 1;
    public string FileName { get; set; } = "test-payload.bin";
    public string ETag { get; set; } = "\"payload-v1\"";
    public DateTimeOffset LastModified { get; set; } = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Advertise Accept-Ranges and honour Range requests. False = no-range mode (always 200).</summary>
    public bool SupportRanges { get; set; } = true;

    /// <summary>Send Content-Length. False = chunked, unknown-size mode.</summary>
    public bool SendContentLength { get; set; } = true;

    /// <summary>Abort the connection after this many body bytes (per response). Null = send fully.</summary>
    public long? TruncateAfterBytes { get; set; }

    /// <summary>
    /// Gracefully end the response after this many body bytes with a truthful
    /// short Content-Length (broken-server emulation). Unlike TruncateAfterBytes
    /// the connection closes cleanly, so clients observe EOF, not an error.
    /// </summary>
    public long? ShortBodyBytes { get; set; }

    /// <summary>When true, every content byte is ConstantByte (highly compressible).</summary>
    public bool ConstantContent { get; set; }
    public byte ConstantByte { get; set; } = 0x41;

    /// <summary>Pace body writes to this many bytes/second. Null = unthrottled.</summary>
    public long? BytesPerSecond { get; set; }

    /// <summary>Optional fixed latency before the response body starts.</summary>
    public TimeSpan? InitialLatency { get; set; }

    /// <summary>Status applied to every /file request (404/429/500/503 glue). Null = normal routing.</summary>
    public int? GlobalStatus { get; set; }

    /// <summary>Persistent per-range error injection (e.g. 500 for one chunk).</summary>
    public List<RangeFault> RangeFaults { get; } = [];

    /// <summary>Gzip-encode response bodies when the client offers gzip (compressed-range hazard probe).</summary>
    public bool GzipResponses { get; set; }

    /// <summary>Malformed-response probe: lie about the total in Content-Range. Null = truthful.</summary>
    public long? WrongContentRangeTotal { get; set; }

    public static ServerProfile Default() => new();
}
