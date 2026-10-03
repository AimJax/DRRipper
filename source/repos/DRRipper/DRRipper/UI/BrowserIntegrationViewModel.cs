using System.IO;
using DRRipper.BrowserBridge;
using DRRipper.Scheduler;

namespace DRRipper.UI
{
    /// <summary>
    /// Browser-integration settings support (Ticket #007 §24/§47): per-browser
    /// registration status, install/repair/uninstall, and a "test connection"
    /// probe against the local desktop bridge. All registry/pipe mechanics live
    /// in <see cref="BrowserRegistration"/> + <see cref="SingleInstanceGuard"/>
    /// (unit-tested); this class only orchestrates them for the dialog.
    /// WPF-free and headless-constructible (service injectable).
    /// </summary>
    public interface IBrowserIntegrationService
    {
        BrowserRegistrationStatus GetStatus(BrowserKind browser);
        BrowserRegistrationStatus Install(BrowserKind browser, string extensionIdOrOrigin);
        void Uninstall(BrowserKind browser);
        Task<bool> TestBridgeAsync();
        string HostExePath { get; }
    }

    public sealed class BrowserIntegrationService : IBrowserIntegrationService
    {
        private readonly IRegistryView _registry;
        private readonly string _manifestDir;
        private readonly string _pipeName;

        public BrowserIntegrationService(
            IRegistryView? registry = null,
            string? manifestDir = null,
            string? pipeName = null)
        {
            _registry = registry ?? new WindowsRegistryView();
            _manifestDir = manifestDir ?? BrowserRegistration.DefaultManifestDirectory();
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? BrowserBridgeService.DefaultPipeName : pipeName;
        }

        public string HostExePath => DiscoverHostExePath();

        public BrowserRegistrationStatus GetStatus(BrowserKind browser)
        {
            try { return BrowserRegistration.GetStatus(browser, _registry, _manifestDir); }
            catch (Exception ex) { return new BrowserRegistrationStatus(browser, BrowserRegistrationState.Error, null, Trim(ex)); }
        }

        public BrowserRegistrationStatus Install(BrowserKind browser, string extensionIdOrOrigin)
        {
            if (string.IsNullOrWhiteSpace(extensionIdOrOrigin))
                return new BrowserRegistrationStatus(browser, BrowserRegistrationState.Error, null,
                    "Enter the extension ID first (see installation instructions).");
            var hostExe = DiscoverHostExePath();
            if (string.IsNullOrWhiteSpace(hostExe))
                return new BrowserRegistrationStatus(browser, BrowserRegistrationState.Error, null,
                    "Native host executable not found next to DRRipper.exe.");
            return BrowserRegistration.Install(browser, hostExe, extensionIdOrOrigin.Trim(), _registry, _manifestDir);
        }

        public void Uninstall(BrowserKind browser)
        {
            try { BrowserRegistration.Uninstall(browser, _registry, _manifestDir); } catch { }
        }

        public async Task<bool> TestBridgeAsync()
        {
            try
            {
                return await SingleInstanceGuard.PingBridgeAsync(_pipeName, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch { return false; }
        }

        internal static string DiscoverHostExePath()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var candidate = Path.Combine(baseDir, "DRRipper.NativeHost.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }
            return string.Empty;
        }

        private static string Trim(Exception ex)
        {
            var m = (ex.Message ?? "unknown error").Replace('\r', ' ').Replace('\n', ' ');
            return m.Length > 200 ? m.Substring(0, 200) : m;
        }
    }

    /// <summary>Dialog model for the Browser Integration section (§24).</summary>
    public sealed class BrowserIntegrationViewModel : ObservableObject
    {
        private readonly IBrowserIntegrationService _service;
        private string _extensionId = string.Empty;
        private string _chromeStatus = "—";
        private string _edgeStatus = "—";
        private string _firefoxStatus = "—";
        private string _bridgeStatus = "—";
        private string _diagnostics = "Press Refresh to probe the current state.";

        public BrowserIntegrationViewModel(IBrowserIntegrationService? service = null)
        {
            _service = service ?? new BrowserIntegrationService();
            RefreshCommand = new RelayCommand(() => Refresh());
            TestBridgeCommand = new RelayCommand(() => _ = TestBridgeAsync());
            InstallCommand = new RelayCommand(p => Install(ParseBrowser(p)));
            UninstallCommand = new RelayCommand(p => Uninstall(ParseBrowser(p)));
        }

        public System.Windows.Input.ICommand RefreshCommand { get; }
        public System.Windows.Input.ICommand TestBridgeCommand { get; }
        public System.Windows.Input.ICommand InstallCommand { get; }
        public System.Windows.Input.ICommand UninstallCommand { get; }

        public string ExtensionId { get => _extensionId; set => Set(ref _extensionId, value ?? string.Empty); }
        public string ChromeStatus { get => _chromeStatus; private set => Set(ref _chromeStatus, value); }
        public string EdgeStatus { get => _edgeStatus; private set => Set(ref _edgeStatus, value); }
        public string FirefoxStatus { get => _firefoxStatus; private set => Set(ref _firefoxStatus, value); }
        public string BridgeStatus { get => _bridgeStatus; private set => Set(ref _bridgeStatus, value); }
        public string Diagnostics { get => _diagnostics; private set => Set(ref _diagnostics, value); }

        public void Refresh()
        {
            try
            {
                ChromeStatus = Format(_service.GetStatus(BrowserKind.Chrome));
                EdgeStatus = Format(_service.GetStatus(BrowserKind.Edge));
                FirefoxStatus = Format(_service.GetStatus(BrowserKind.Firefox));
                Diagnostics = "Native host: " + (string.IsNullOrEmpty(_service.HostExePath)
                    ? "not found (expected next to DRRipper.exe)"
                    : _service.HostExePath);
            }
            catch (Exception ex) { Diagnostics = "Refresh failed: " + ex.Message; }
        }

        private async Task TestBridgeAsync()
        {
            try
            {
                BridgeStatus = "Probing…";
                bool ok = await _service.TestBridgeAsync().ConfigureAwait(false);
                BridgeStatus = ok ? "Desktop bridge reachable." : "Desktop bridge NOT reachable (is DRRipper running?).";
            }
            catch (Exception ex) { BridgeStatus = "Probe failed: " + ex.Message; }
        }

        private void Install(BrowserKind browser)
        {
            try
            {
                var status = _service.Install(browser, ExtensionId);
                Apply(browser, status);
            }
            catch (Exception ex) { Diagnostics = "Install failed: " + ex.Message; }
        }

        private void Uninstall(BrowserKind browser)
        {
            try
            {
                _service.Uninstall(browser);
                Refresh();
                Diagnostics = browser + " registration removed.";
            }
            catch (Exception ex) { Diagnostics = "Uninstall failed: " + ex.Message; }
        }

        private void Apply(BrowserKind browser, BrowserRegistrationStatus status)
        {
            var text = Format(status);
            switch (browser)
            {
                case BrowserKind.Chrome: ChromeStatus = text; break;
                case BrowserKind.Edge: EdgeStatus = text; break;
                case BrowserKind.Firefox: FirefoxStatus = text; break;
            }
            if (status.State == BrowserRegistrationState.Error)
                Diagnostics = browser + ": " + (status.Detail ?? "error");
            else
                Diagnostics = browser + ": " + text;
        }

        private static string Format(BrowserRegistrationStatus status) => status.State switch
        {
            BrowserRegistrationState.Installed => "Installed" + (string.IsNullOrEmpty(status.ManifestPath) ? "" : " (" + status.ManifestPath + ")"),
            BrowserRegistrationState.NotInstalled => "Not installed",
            BrowserRegistrationState.Unsupported => "Unsupported",
            _ => "Error" + (string.IsNullOrEmpty(status.Detail) ? "" : ": " + status.Detail),
        };

        private static BrowserKind ParseBrowser(object? parameter)
        {
            if (parameter is BrowserKind kind)
                return kind;
            var s = parameter?.ToString() ?? string.Empty;
            if (string.Equals(s, "Edge", StringComparison.OrdinalIgnoreCase))
                return BrowserKind.Edge;
            if (string.Equals(s, "Firefox", StringComparison.OrdinalIgnoreCase))
                return BrowserKind.Firefox;
            return BrowserKind.Chrome;
        }
    }
}
