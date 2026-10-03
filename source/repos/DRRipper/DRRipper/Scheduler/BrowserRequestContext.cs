using System.Net.Http;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Per-job HTTP request context for browser-handed downloads (Ticket #007 §18).
    /// Carries ONLY allowlisted, validated values (see BrowserBridge validation):
    /// an http/https referrer, a browser user-agent, and a small map of safe
    /// headers. Hop-by-hop/transport headers can never be represented here —
    /// <see cref="ApplyTo"/> only writes known-safe headers onto the request.
    /// Authorization values are applied to transport but NEVER rendered into
    /// strings, logs, or diagnostics (see <see cref="DescribeForDiagnostics"/>).
    /// </summary>
    public sealed class BrowserRequestContext
    {
        public string? Referrer { get; set; }
        public string? UserAgent { get; set; }

        /// <summary>Pre-filtered allowlist headers (Accept, Accept-Language, Authorization...).</summary>
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Applies the context to one outgoing request. Called for every
        /// transfer/probe request the session issues. Never throws.
        /// </summary>
        public void ApplyTo(HttpRequestMessage request)
        {
            if (request == null)
                return;
            try
            {
                if (!string.IsNullOrWhiteSpace(Referrer) &&
                    Uri.TryCreate(Referrer, UriKind.Absolute, out var refUri) &&
                    (string.Equals(refUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(refUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
                {
                    try { request.Headers.Referrer = refUri; } catch { }
                }
            }
            catch { }
            try
            {
                if (!string.IsNullOrWhiteSpace(UserAgent))
                {
                    try
                    {
                        request.Headers.UserAgent.Clear();
                        request.Headers.UserAgent.ParseAdd(UserAgent.Trim());
                    }
                    catch { }
                }
            }
            catch { }
            foreach (var kv in Headers)
            {
                try
                {
                    var name = kv.Key?.Trim() ?? string.Empty;
                    var value = kv.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                        continue;
                    // Belt and suspenders: only the transport-safe allowlist is
                    // ever written, even if the map was built by hand (§16).
                    if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            request.Headers.Accept.Clear();
                            request.Headers.Accept.ParseAdd(value);
                        }
                        catch { }
                    }
                    else if (string.Equals(name, "Accept-Language", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            request.Headers.Remove("Accept-Language");
                            request.Headers.TryAddWithoutValidation("Accept-Language", value);
                        }
                        catch { }
                    }
                    else if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0)
                        {
                            try
                            {
                                request.Headers.Remove("Authorization");
                                request.Headers.TryAddWithoutValidation("Authorization", value);
                            }
                            catch { }
                        }
                    }
                    // Anything else in the map is ignored (never forwarded).
                }
                catch { }
            }
        }

        /// <summary>
        /// Redacted one-line summary for diagnostics (§19): header NAMES and the
        /// referrer host only. Authorization/cookie VALUES never appear.
        /// </summary>
        public string DescribeForDiagnostics()
        {
            try
            {
                var parts = new List<string>();
                string referrerHost = "(none)";
                try
                {
                    if (!string.IsNullOrWhiteSpace(Referrer) &&
                        Uri.TryCreate(Referrer, UriKind.Absolute, out var u))
                        referrerHost = u.Host;
                }
                catch { }
                parts.Add("referrer=" + referrerHost);
                parts.Add("ua=" + (string.IsNullOrWhiteSpace(UserAgent) ? "(default)" : "(browser)"));
                var names = new List<string>();
                foreach (var k in Headers.Keys)
                {
                    if (string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase))
                        names.Add("Authorization:[redacted]");
                    else
                        names.Add(k);
                }
                parts.Add("headers=[" + string.Join(",", names) + "]");
                return string.Join(" ", parts);
            }
            catch { return "(context)"; }
        }
    }
}
