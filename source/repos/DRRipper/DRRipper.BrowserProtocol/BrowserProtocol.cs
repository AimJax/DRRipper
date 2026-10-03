using System.Text.Json.Serialization;

namespace DRRipper.BrowserBridge
{
    /// <summary>
    /// Versioned browser→desktop enqueue contract (Ticket #007 §14).
    /// The SAME schema travels extension→native-host (Native Messaging JSON)
    /// and native-host→desktop (named-pipe JSON). Both ends re-validate:
    /// the pipe is local-only but still an untrusted boundary.
    /// </summary>
    public static class BrowserBridgeProtocol
    {
        public const int CurrentVersion = 1;
        public const string MessageTypeEnqueue = "enqueue";
        public const string MessageTypePing = "ping";

        /// <summary>Maximum accepted message payload (1 MB, §10). Ordinary handoffs are ~1 KB.</summary>
        public const int MaxMessageBytes = 1024 * 1024;

        /// <summary>Maximum URL length accepted (IE-era 2083 is too small for signed URLs; 8 KiB is plenty).</summary>
        public const int MaxUrlLength = 8192;

        /// <summary>Production named-pipe name (§11). Tests use unique names.</summary>
        public const string PipeName = "DRRipper.NativeBridge";

        /// <summary>Raw suggested filename cap before sanitization.</summary>
        public const int MaxRawFileNameLength = 1024;

        public const int MaxHeaderCount = 32;
        public const int MaxHeaderNameLength = 256;
        public const int MaxHeaderValueLength = 8192;
        public const int MaxCookieCount = 128;
        public const int MaxCookieNameLength = 4096;
        public const int MaxCookieValueLength = 4096;
        public const int MaxReferrerLength = 2048;
    }

    /// <summary>Browser → desktop enqueue request (§14). Never carries a target path (§32).</summary>
    public sealed class BrowserEnqueueRequest
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = BrowserBridgeProtocol.CurrentVersion;

        [JsonPropertyName("type")]
        public string Type { get; set; } = BrowserBridgeProtocol.MessageTypeEnqueue;

        /// <summary>Client-generated idempotency key (uuid). Retried handoffs reuse it (§27).</summary>
        [JsonPropertyName("requestId")]
        public string RequestId { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("suggestedFileName")]
        public string? SuggestedFileName { get; set; }

        /// <summary>Source page URL (full value validated, only host persisted).</summary>
        [JsonPropertyName("referrer")]
        public string? Referrer { get; set; }

        /// <summary>Human browser label, e.g. "Chrome", "Edge", "Firefox". Validated against a closed list.</summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("cookies")]
        public List<BrowserCookie>? Cookies { get; set; }
    }

    public sealed class BrowserCookie
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Desktop → browser acknowledgement (§14/§26).</summary>
    public sealed class BrowserEnqueueResponse
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = BrowserBridgeProtocol.CurrentVersion;

        [JsonPropertyName("requestId")]
        public string RequestId { get; set; } = string.Empty;

        [JsonPropertyName("accepted")]
        public bool Accepted { get; set; }

        [JsonPropertyName("jobId")]
        public string? JobId { get; set; }

        /// <summary>Machine-readable category (§26): null on success.</summary>
        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>True when this requestId was already accepted (no duplicate job, §27).</summary>
        [JsonPropertyName("duplicate")]
        public bool Duplicate { get; set; }

        /// <summary>Non-fatal notes, e.g. "cookies-deferred-to-012". Never contains secret values.</summary>
        [JsonPropertyName("warnings")]
        public List<string>? Warnings { get; set; }
    }

    /// <summary>Stable error categories surfaced to the extension (§26).</summary>
    public static class BrowserErrorCodes
    {
        public const string InvalidUrl = "invalid-url";
        public const string DisallowedScheme = "disallowed-scheme";
        public const string InvalidFileName = "invalid-filename";
        public const string InvalidHeaders = "invalid-headers";
        public const string InvalidCookies = "invalid-cookies";
        public const string InvalidReferrer = "invalid-referrer";
        public const string UnsupportedVersion = "protocol-version-mismatch";
        public const string MalformedMessage = "malformed-message";
        public const string MessageTooLarge = "message-too-large";
        public const string DesktopUnavailable = "desktop-unavailable";
        public const string InternalError = "internal-error";
    }
}
