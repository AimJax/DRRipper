using System;
using System.Diagnostics;
using System.IO;

namespace DRRipper.UI
{
    /// <summary>Shell launching behind an interface (Ticket #006 §30). Testable via stubs.</summary>
    public interface IShellService
    {
        bool CanOpenFile(DownloadJobViewModel job);
        bool TryOpenFile(DownloadJobViewModel job, out string? error);
        bool TryOpenFolder(DownloadJobViewModel job, out string? error);
    }

    /// <summary>Clipboard behind an interface (WPF Clipboard requires STA UI thread).</summary>
    public interface IClipboardService
    {
        void SetText(string text);
    }

    /// <summary>
    /// Safe Windows shell launching: validated local paths only, shell-execute of
    /// the file itself or explorer folder selection. Never builds command lines
    /// from URL-derived text; never executes anything but the OS file association.
    /// </summary>
    public sealed class ShellService : IShellService
    {
        public bool CanOpenFile(DownloadJobViewModel job)
        {
            try
            {
                var path = job?.TargetPath;
                if (string.IsNullOrWhiteSpace(path)) return false;
                // Only offer Open File for an existing file (never directories, never missing).
                return File.Exists(System.IO.Path.GetFullPath(path));
            }
            catch { return false; }
        }

        public bool TryOpenFile(DownloadJobViewModel job, out string? error)
        {
            error = null;
            try
            {
                var path = System.IO.Path.GetFullPath(job.TargetPath);
                if (!File.Exists(path)) { error = "File does not exist."; return false; }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public bool TryOpenFolder(DownloadJobViewModel job, out string? error)
        {
            error = null;
            try
            {
                string? dir = null;
                string candidate = job.TargetPath;
                if (File.Exists(candidate))
                {
                    // Select the file in Explorer (safe, fixed verb — no URL-derived args).
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + candidate + "\"") { UseShellExecute = true });
                    return true;
                }
                if (Directory.Exists(candidate)) dir = candidate;
                else
                {
                    var parent = Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) dir = parent;
                }
                if (dir == null) { error = "Folder does not exist."; return false; }
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }

    /// <summary>Production clipboard (STA UI thread only — invoked from the view).</summary>
    public sealed class ClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
            System.Windows.Clipboard.SetText(text ?? string.Empty);
        }
    }
}
