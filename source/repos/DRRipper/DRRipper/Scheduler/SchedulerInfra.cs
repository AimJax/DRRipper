using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DRRipper.Scheduler
{
    /// <summary>
    /// Reference-counted system sleep prevention (Ticket #005 §37).
    /// System-required state stays active while at least one transfer is genuinely
    /// active; released when none remain. Thread-safe. WPF-independent.
    /// </summary>
    internal sealed class SchedulerPowerManager
    {
        private const uint ES_CONTINUOUS = 0x80000000u;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001u;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private int _refCount;

        /// <summary>Current active holder count (diagnostics/tests).</summary>
        public int RefCount => Volatile.Read(ref _refCount);

        /// <summary>Acquire one active-transfer hold. Idempotent per caller discipline.</summary>
        public void Acquire()
        {
            var n = Interlocked.Increment(ref _refCount);
            if (n == 1)
            {
                try { SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_CONTINUOUS); } catch { }
            }
        }

        /// <summary>Release one hold. When the last hold releases, sleep prevention clears.</summary>
        public void Release()
        {
            var n = Interlocked.Decrement(ref _refCount);
            if (n <= 0)
            {
                Interlocked.Exchange(ref _refCount, 0);
                try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
            }
        }
    }

    /// <summary>
    /// URL diagnostics redaction (Ticket #005 §26). Full signed URLs (query,
    /// userinfo) must never reach ordinary logs; engine keeps the exact URL for
    /// download, diagnostics use the redacted form.
    /// </summary>
    internal static class UrlRedactor
    {
        /// <summary>Redacts query string, fragment, and userinfo for safe logging.</summary>
        public static string Redact(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "(empty)";
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
                // Indicate redaction when something was stripped.
                if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.UserInfo))
                    s += "…[redacted]";
                return s;
            }
            catch { return "(malformed-url)"; }
        }
    }

    /// <summary>Well-known scheduler paths (Ticket #005 §5). No hardcoded E:\ paths.</summary>
    public static class SchedulerPaths
    {
        /// <summary>Production queue database: %LOCALAPPDATA%\DRRipper\queue.db.</summary>
        public static string DefaultDatabasePath()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "DRRipper", "queue.db");
        }
    }
}
