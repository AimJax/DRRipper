using System.Text.Json;
using Microsoft.Win32;

namespace DRRipper.BrowserBridge
{
    /// <summary>Supported browsers for native-host registration (§20–22).</summary>
    public enum BrowserKind
    {
        Chrome,
        Edge,
        Firefox,
    }

    /// <summary>Registration probe outcome (§24).</summary>
    public enum BrowserRegistrationState
    {
        NotInstalled,
        Installed,
        Error,
        Unsupported,
    }

    public sealed record BrowserRegistrationStatus(
        BrowserKind Browser,
        BrowserRegistrationState State,
        string? ManifestPath,
        string? Detail);

    /// <summary>
    /// Minimal registry abstraction so registration logic is unit-testable
    /// without touching HKCU (T-BROWSER-REG-01..05).
    /// </summary>
    public interface IRegistryView
    {
        void SetValue(string keyPath, string valueName, string value);
        string? GetValue(string keyPath, string valueName);
        void DeleteKey(string keyPath);
        bool KeyExists(string keyPath);
    }

    /// <summary>Production HKCU-backed registry view (§21: per-user, no admin).</summary>
    public sealed class WindowsRegistryView : IRegistryView
    {
        public void SetValue(string keyPath, string valueName, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, true);
            if (key == null)
                throw new InvalidOperationException("Cannot create registry key: " + keyPath);
            key.SetValue(valueName, value, RegistryValueKind.String);
        }

        public string? GetValue(string keyPath, string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
            return key?.GetValue(valueName) as string;
        }

        public void DeleteKey(string keyPath)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); } catch { }
        }

        public bool KeyExists(string keyPath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
            return key != null;
        }
    }

    /// <summary>In-memory registry double for tests.</summary>
    public sealed class MemoryRegistryView : IRegistryView
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public void SetValue(string keyPath, string valueName, string value)
        {
            if (!_keys.TryGetValue(keyPath, out var values))
                _keys[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            values[valueName] = value;
        }

        public string? GetValue(string keyPath, string valueName)
            => _keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName, out var v) ? v : null;

        public void DeleteKey(string keyPath) => _keys.Remove(keyPath);

        public bool KeyExists(string keyPath) => _keys.ContainsKey(keyPath);

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Snapshot() =>
            _keys.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Native-host manifest generation + per-user registration (§20–23).
    /// Manifests are written under %LOCALAPPDATA%\DRRipper\NativeHosts with the
    /// INSTALLED executable path (never a dev-machine path, §20); registry
    /// values point at those manifests. All operations are idempotent and
    /// reversible; uninstall removes only DRRipper-owned keys.
    /// </summary>
    public static class BrowserRegistration
    {
        public const string HostName = "com.dripper.host";
        public const string HostDescription = "DRRipper download handoff";

        public static string RegistryKeyPath(BrowserKind browser) => browser switch
        {
            BrowserKind.Chrome => @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
            BrowserKind.Edge => @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
            BrowserKind.Firefox => @"Software\Mozilla\NativeMessagingHosts\" + HostName,
            _ => throw new ArgumentOutOfRangeException(nameof(browser)),
        };

        public static string DefaultManifestDirectory()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "DRRipper", "NativeHosts");
        }

        public static string ManifestFileName(BrowserKind browser) => browser switch
        {
            BrowserKind.Firefox => HostName + ".firefox.json",
            _ => HostName + ".json",
        };

        /// <summary>
        /// Builds the host manifest JSON. allowedOrigin is the packaged extension
        /// origin (chromium) or extension id (firefox); never a wildcard (§23).
        /// </summary>
        public static string BuildHostManifestJson(BrowserKind browser, string hostExePath, string allowedOrigin)
        {
            if (string.IsNullOrWhiteSpace(hostExePath))
                throw new ArgumentException("Host path is required.", nameof(hostExePath));
            if (string.IsNullOrWhiteSpace(allowedOrigin) || allowedOrigin.Contains('*'))
                throw new ArgumentException("A concrete extension origin/id is required (no wildcards).", nameof(allowedOrigin));

            if (browser == BrowserKind.Firefox)
            {
                var ff = new Dictionary<string, object?>
                {
                    ["name"] = HostName,
                    ["description"] = HostDescription,
                    ["path"] = hostExePath,
                    ["type"] = "stdio",
                    ["allowed_extensions"] = new[] { allowedOrigin },
                };
                return JsonSerializer.Serialize(ff, new JsonSerializerOptions { WriteIndented = true });
            }

            var origin = allowedOrigin.StartsWith("chrome-extension://", StringComparison.Ordinal)
                ? allowedOrigin
                : "chrome-extension://" + allowedOrigin + "/";
            var manifest = new Dictionary<string, object?>
            {
                ["name"] = HostName,
                ["description"] = HostDescription,
                ["path"] = hostExePath,
                ["type"] = "stdio",
                ["allowed_origins"] = new[] { origin },
            };
            return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        public static BrowserRegistrationStatus GetStatus(
            BrowserKind browser, IRegistryView registry, string? manifestDir = null)
        {
            try
            {
                var keyPath = RegistryKeyPath(browser);
                var manifestPath = registry.GetValue(keyPath, string.Empty);
                if (string.IsNullOrWhiteSpace(manifestPath))
                    return new BrowserRegistrationStatus(browser, BrowserRegistrationState.NotInstalled, null, "No registration key.");
                if (!File.Exists(manifestPath))
                    return new BrowserRegistrationStatus(browser, BrowserRegistrationState.Error, manifestPath, "Manifest file missing.");
                return new BrowserRegistrationStatus(browser, BrowserRegistrationState.Installed, manifestPath, null);
            }
            catch (Exception ex)
            {
                return new BrowserRegistrationStatus(browser, BrowserRegistrationState.Error, null, TrimError(ex));
            }
        }

        public static BrowserRegistrationStatus Install(
            BrowserKind browser, string hostExePath, string allowedOrigin,
            IRegistryView registry, string? manifestDir = null)
        {
            try
            {
                var dir = manifestDir ?? DefaultManifestDirectory();
                Directory.CreateDirectory(dir);
                var manifestPath = Path.Combine(dir, ManifestFileName(browser));
                var json = BuildHostManifestJson(browser, hostExePath, allowedOrigin);
                var tmp = manifestPath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(tmp, json);
                File.Move(tmp, manifestPath, overwrite: true);
                registry.SetValue(RegistryKeyPath(browser), string.Empty, manifestPath);
                return GetStatus(browser, registry, manifestDir);
            }
            catch (Exception ex)
            {
                return new BrowserRegistrationStatus(browser, BrowserRegistrationState.Error, null, TrimError(ex));
            }
        }

        /// <summary>Removes ONLY the DRRipper-owned key + manifest (§21: reversible, scoped).</summary>
        public static void Uninstall(BrowserKind browser, IRegistryView registry, string? manifestDir = null)
        {
            try
            {
                var manifestPath = registry.GetValue(RegistryKeyPath(browser), string.Empty);
                registry.DeleteKey(RegistryKeyPath(browser));
                // Only delete the manifest file if it is ours (same directory we manage).
                if (!string.IsNullOrWhiteSpace(manifestPath))
                {
                    try
                    {
                        var dir = manifestDir ?? DefaultManifestDirectory();
                        var full = Path.GetFullPath(manifestPath);
                        if (full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(full, Path.Combine(Path.GetFullPath(dir), Path.GetFileName(full)), StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(full))
                                File.Delete(full);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string TrimError(Exception ex)
        {
            var m = (ex.Message ?? "unknown error").Replace('\r', ' ').Replace('\n', ' ');
            return m.Length > 200 ? m.Substring(0, 200) : m;
        }
    }
}
