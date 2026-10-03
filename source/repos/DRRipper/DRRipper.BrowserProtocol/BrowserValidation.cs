namespace DRRipper.BrowserBridge
{
    /// <summary>
    /// Untrusted-boundary validation for browser handoff data (Ticket #007 §4).
    /// Every rule here is unit-tested; both the native host and the desktop
    /// bridge apply the same checks (defense in depth).
    /// </summary>
    public static class BrowserValidation
    {
        /// <summary>Headers the browser may forward (§16). Closed allowlist.</summary>
        public static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Referer", "User-Agent", "Accept", "Accept-Language", "Authorization",
        };

        /// <summary>Transport-owned headers that must never be overridden (§16).</summary>
        public static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Content-Length", "Connection", "Transfer-Encoding", "TE",
            "Trailer", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization",
            "Keep-Alive", "Range", "If-Range", "Accept-Encoding",
            "Cookie", "Set-Cookie", "Content-Range",
        };

        public static readonly HashSet<string> AllowedSources = new(StringComparer.OrdinalIgnoreCase)
        {
            "Chrome", "Edge", "Firefox", "Chromium",
        };

        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>Only http/https job URLs are permitted (§4).</summary>
        public static bool TryNormalizeUrl(string? url, out string normalized, out string errorCode)
        {
            normalized = string.Empty;
            errorCode = BrowserErrorCodes.InvalidUrl;
            if (string.IsNullOrWhiteSpace(url))
                return false;
            url = url.Trim();
            if (url.Length > BrowserBridgeProtocol.MaxUrlLength)
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                return false;
            if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = BrowserErrorCodes.DisallowedScheme;
                return false;
            }
            normalized = u.ToString();
            return true;
        }

        public static bool IsValidRequestId(string? requestId)
            => !string.IsNullOrWhiteSpace(requestId) && requestId!.Length <= 128;

        public static bool TryNormalizeSource(string? source, out string normalized)
        {
            normalized = "Browser";
            if (string.IsNullOrWhiteSpace(source))
                return true; // optional; generic label
            source = source.Trim();
            if (source.Length > 32 || !AllowedSources.Contains(source))
                return false;
            normalized = source;
            return true;
        }

        /// <summary>Validates referrer; returns the host to persist (never the full URL, §15/§19).</summary>
        public static bool TryNormalizeReferrer(string? referrer, out string? referrerHost, out string errorCode)
        {
            referrerHost = null;
            errorCode = BrowserErrorCodes.InvalidReferrer;
            if (string.IsNullOrWhiteSpace(referrer))
            {
                errorCode = string.Empty;
                return true; // optional
            }
            referrer = referrer.Trim();
            if (referrer.Length > BrowserBridgeProtocol.MaxReferrerLength)
                return false;
            if (!Uri.TryCreate(referrer, UriKind.Absolute, out var u))
                return false;
            if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;
            referrerHost = u.Host;
            errorCode = string.Empty;
            return true;
        }

        /// <summary>
        /// Filters browser-supplied headers to the safe allowlist (§16).
        /// Returns false only for structurally invalid maps; disallowed names are
        /// dropped (reported via <paramref name="dropped"/>), never applied.
        /// </summary>
        public static bool TryFilterHeaders(
            IDictionary<string, string>? headers,
            out Dictionary<string, string> allowed,
            out List<string> dropped,
            out string errorCode)
        {
            allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dropped = new List<string>();
            errorCode = BrowserErrorCodes.InvalidHeaders;
            if (headers == null)
            {
                errorCode = string.Empty;
                return true;
            }
            if (headers.Count > BrowserBridgeProtocol.MaxHeaderCount)
                return false;
            foreach (var kv in headers)
            {
                if (kv.Key == null || kv.Value == null)
                    return false;
                var name = kv.Key.Trim();
                var value = kv.Value.Trim();
                if (name.Length == 0 || name.Length > BrowserBridgeProtocol.MaxHeaderNameLength)
                    return false;
                if (value.Length > BrowserBridgeProtocol.MaxHeaderValueLength)
                    return false;
                if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                    return false; // response-splitting guard
                if (AllowedHeaders.Contains(name))
                {
                    if (!allowed.ContainsKey(name))
                        allowed[name] = value;
                }
                else
                {
                    dropped.Add(name); // blocked or unknown: never forwarded
                }
            }
            errorCode = string.Empty;
            return true;
        }

        /// <summary>
        /// Validates cookie handoff shape (§17). Values are accepted structurally
        /// but NEVER forwarded to the engine in this ticket (deferred to #012);
        /// the bridge reports "cookies-deferred" instead of faking support.
        /// </summary>
        public static bool TryValidateCookies(
            IList<BrowserCookie>? cookies,
            out int count,
            out string errorCode)
        {
            count = 0;
            errorCode = BrowserErrorCodes.InvalidCookies;
            if (cookies == null || cookies.Count == 0)
            {
                errorCode = string.Empty;
                return true;
            }
            if (cookies.Count > BrowserBridgeProtocol.MaxCookieCount)
                return false;
            foreach (var c in cookies)
            {
                if (c == null || string.IsNullOrEmpty(c.Name))
                    return false;
                if (c.Name.Length > BrowserBridgeProtocol.MaxCookieNameLength ||
                    c.Value.Length > BrowserBridgeProtocol.MaxCookieValueLength)
                    return false;
                if (c.Name.IndexOfAny(new[] { '\r', '\n', '\0', ';', '=' }) >= 0)
                    return false;
                if (c.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                    return false;
            }
            count = cookies.Count;
            errorCode = string.Empty;
            return true;
        }

        /// <summary>
        /// Sanitizes a browser-suggested filename (§33). The result is a bare
        /// file name: no separators, no traversal, no device names, no trailing
        /// dots/spaces. Never returns null/empty (falls back to "download.bin").
        /// </summary>
        public static string SanitizeFileName(string? suggested)
        {
            if (string.IsNullOrWhiteSpace(suggested))
                return "download.bin";
            var name = suggested.Trim();
            // Strip any directory components the browser may have included.
            name = name.Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name.Substring(slash + 1);
            name = name.Replace("..", "_");
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Replace('/', '_').Replace('\\', '_');
            // ADS streams and drive-relative forms.
            var colon = name.IndexOf(':');
            if (colon >= 0)
                name = name.Substring(colon + 1);
            name = name.Trim().TrimEnd('.', ' ');
            if (name.Length > 255)
                name = name.Substring(0, 255).TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(name))
                return "download.bin";
            var stem = name;
            var dot = name.IndexOf('.');
            if (dot > 0)
                stem = name.Substring(0, dot);
            if (ReservedDeviceNames.Contains(stem))
                name = "_" + name;
            if (string.IsNullOrWhiteSpace(name))
                return "download.bin";
            return name;
        }

        /// <summary>Redacts a URL for diagnostics: path kept, query/userinfo stripped (§19).</summary>
        public static string RedactUrl(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return "(empty)";
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.IsAbsoluteUri)
                    return "(malformed-url)";
                var builder = new UriBuilder(u)
                {
                    Query = string.Empty,
                    Fragment = string.Empty,
                    UserName = string.Empty,
                    Password = string.Empty,
                };
                var s = builder.Uri.GetLeftPart(UriPartial.Path);
                if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.UserInfo))
                    s += "…[redacted]";
                return s;
            }
            catch { return "(malformed-url)"; }
        }
    }
}
