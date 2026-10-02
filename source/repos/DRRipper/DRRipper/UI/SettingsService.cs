using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DRRipper.UI
{
    /// <summary>
    /// Versioned user settings (Ticket #006 §19). Persisted separately from the
    /// queue DB at %LOCALAPPDATA%\DRRipper\settings.json. Budgets and the DB path
    /// require restart (scheduler owns its allocator lifetime); ActiveDownloadLimit,
    /// per-file connections default, and UI refresh apply live.
    /// </summary>
    public sealed class AppSettings
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public int ActiveDownloadLimit { get; set; } = 3;
        public int ConnectionsPerFileDefault { get; set; } = 8;
        public int GlobalConnectionBudget { get; set; } = 16;
        public int PerHostConnectionBudget { get; set; } = 8;
        public string DefaultDownloadDirectory { get; set; } = string.Empty;
        public int UiRefreshMs { get; set; } = 1000;
        public bool RememberWindowPlacement { get; set; } = true;
        public double WindowWidth { get; set; } = 1100;
        public double WindowHeight { get; set; } = 640;

        public static AppSettings Defaults(string? defaultDirectory = null)
        {
            var s = new AppSettings();
            try
            {
                s.DefaultDownloadDirectory = defaultDirectory ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            catch { s.DefaultDownloadDirectory = string.Empty; }
            return s;
        }

        /// <summary>Clamps to safe ranges (never throws on user-edited files).</summary>
        public void Normalize()
        {
            Version = CurrentVersion;
            ActiveDownloadLimit = Math.Clamp(ActiveDownloadLimit, 1, 16);
            ConnectionsPerFileDefault = Math.Clamp(ConnectionsPerFileDefault, 1, 16);
            GlobalConnectionBudget = Math.Clamp(GlobalConnectionBudget, 1, 128);
            PerHostConnectionBudget = Math.Clamp(PerHostConnectionBudget, 1, GlobalConnectionBudget);
            UiRefreshMs = Math.Clamp(UiRefreshMs, 250, 5000);
            WindowWidth = Math.Clamp(WindowWidth, 640, 7680);
            WindowHeight = Math.Clamp(WindowHeight, 400, 4320);
            DefaultDownloadDirectory ??= string.Empty;
        }
    }

    /// <summary>
    /// Atomic, versioned settings persistence (Ticket #006 §19). Writes go to a
    /// unique temp file (flushed) then atomic move; a torn temp can never replace
    /// canonical settings. Corrupt/unknown versions fall back to defaults without
    /// touching the queue. Path injectable for tests.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string _path;

        /// <summary>True when the last load fell back to defaults.</summary>
        public bool LastLoadWasFallback { get; private set; }

        public SettingsService(string? path = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        }

        public string FilePath => _path;

        public static string DefaultPath()
        {
            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(baseDir)) baseDir = System.IO.Path.GetTempPath();
                return System.IO.Path.Combine(baseDir, "DRRipper", "settings.json");
            }
            catch { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DRRipper", "settings.json"); }
        }

        public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
        {
            LastLoadWasFallback = false;
            AppSettings? loaded = null;
            try
            {
                if (File.Exists(_path))
                {
                    string json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
                    loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                }
            }
            catch { loaded = null; }
            if (loaded == null || loaded.Version <= 0 || loaded.Version > AppSettings.CurrentVersion)
            {
                LastLoadWasFallback = File.Exists(_path); // missing file is a clean default, not a fallback
                if (loaded != null && loaded.Version > AppSettings.CurrentVersion)
                    LastLoadWasFallback = true;
                loaded = AppSettings.Defaults();
            }
            loaded.Normalize();
            return loaded;
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Normalize();
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            string tmp = _path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
                // Flush the temp file so a torn write cannot become canonical.
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                    fs.Flush(true);
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                // Best-effort sweep of torn temps for this settings file (single-
                // writer assumption; the atomic move above already won).
                try
                {
                    var d = Path.GetDirectoryName(_path);
                    var n = Path.GetFileName(_path);
                    if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(n) && Directory.Exists(d))
                    {
                        foreach (var f in Directory.EnumerateFiles(d, n + ".tmp.*"))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                }
                catch { }
            }
        }
    }
}
