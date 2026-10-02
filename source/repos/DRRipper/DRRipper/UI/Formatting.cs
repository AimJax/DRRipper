using System;

namespace DRRipper.UI
{
    /// <summary>
    /// Presentation-only formatting helpers (Ticket #006 §7/§10/§28).
    /// Pure functions over snapshots — no engine, scheduler, or UI access.
    /// ETA is never persisted; "—" marks unknown/inapplicable, never a misleading value.
    /// </summary>
    public static class Formatting
    {
        public static string FormatBytes(long bytes)
        {
            const long KB = 1024, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;
            if (bytes < 0) return "?";
            if (bytes >= TB) return $"{(double)bytes / TB:0.##} TB";
            if (bytes >= GB) return $"{(double)bytes / GB:0.##} GB";
            if (bytes >= MB) return $"{(double)bytes / MB:0.##} MB";
            if (bytes >= KB) return $"{(double)bytes / KB:0.##} KB";
            return bytes + " B";
        }

        public static string FormatRate(double bytesPerSecond)
        {
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0)
                return "—";
            const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
            if (bytesPerSecond >= GB) return $"{bytesPerSecond / GB:0.##} GB/s";
            if (bytesPerSecond >= MB) return $"{bytesPerSecond / MB:0.##} MB/s";
            if (bytesPerSecond >= KB) return $"{bytesPerSecond / KB:0.##} KB/s";
            return $"{bytesPerSecond:0} B/s";
        }

        /// <summary>
        /// ETA text for remaining bytes at a smoothed rate. Returns "—" for unknown
        /// size, near-zero speed, or absurd (&gt;30 day) results from a bad sample.
        /// </summary>
        public static string FormatEta(long? totalBytes, long completedBytes, double bytesPerSecond)
        {
            if (!totalBytes.HasValue || totalBytes.Value <= 0) return "—";
            long remaining = totalBytes.Value - completedBytes;
            if (remaining <= 0) return "0s";
            if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 1024) return "—"; // <1 KB/s: no ETA
            double seconds = remaining / bytesPerSecond;
            if (double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds > 30 * 24 * 3600) return "—";
            if (seconds < 60) return $"{seconds:0}s";
            if (seconds < 3600) return $"{seconds / 60:0}m {seconds % 60:0}s";
            if (seconds < 86400) return $"{seconds / 3600:0}h {(seconds % 3600) / 60:0}m";
            return $"{seconds / 86400:0}d {(seconds % 86400) / 3600:0}h";
        }

        public static string FormatProgress(long completedBytes, long totalBytes)
        {
            if (totalBytes <= 0) return "—";
            double pct = Math.Clamp(completedBytes * 100.0 / totalBytes, 0.0, 100.0);
            return $"{pct:0.0}%";
        }

        /// <summary>
        /// Concise user-facing error text (Ticket #006 §28): short reason, never a
        /// stack trace, never signed-URL query material.
        /// </summary>
        public static string FormatError(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "Failed";
            string r = reason.Trim();
            // Strip anything resembling query credentials before display.
            int q = r.IndexOf('?');
            if (q >= 0 && (r.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                           r.Contains("sig=", StringComparison.OrdinalIgnoreCase) ||
                           r.Contains("key=", StringComparison.OrdinalIgnoreCase)))
                r = r.Substring(0, q) + "?…";
            if (r.Length > 160) r = r.Substring(0, 157) + "…";
            return r;
        }
    }
}
