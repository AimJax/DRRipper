using System.IO;
using DRRipper.BrowserBridge;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #007 registration tests (T-BROWSER-REG-01..05). Memory registry +
    /// temp manifest dirs only — never HKCU, never fixed dev paths.
    /// </summary>
    public sealed class BrowserRegistrationTests
    {
        private static string TempDir(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "DRRipperRegTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact] // T-BROWSER-REG-01: generated Chromium manifest is valid + constrained.
        public void T_BROWSER_REG_01_Chromium_Manifest()
        {
            var json = BrowserRegistration.BuildHostManifestJson(
                BrowserKind.Chrome, @"C:\Program Files\DRRipper\DRRipper.NativeHost.exe", "abcdefghijklmnopabcdefghijklmnop");
            Assert.Contains("com.dripper.host", json);
            Assert.Contains("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", json);
            Assert.Contains("stdio", json);
            Assert.DoesNotContain("*", json); // no wildcard origins (§23)
            var bareId = BrowserRegistration.BuildHostManifestJson(
                BrowserKind.Edge, @"C:\Program Files\DRRipper\DRRipper.NativeHost.exe", "edge-id-1234");
            Assert.Contains("chrome-extension://edge-id-1234/", bareId);
            var ff = BrowserRegistration.BuildHostManifestJson(
                BrowserKind.Firefox, @"C:\Program Files\DRRipper\DRRipper.NativeHost.exe", "dripper@example.invalid");
            Assert.Contains("allowed_extensions", ff);
            Assert.DoesNotContain("allowed_origins", ff);
        }

        [Fact] // T-BROWSER-REG-02: install is idempotent (same outcome twice).
        public void T_BROWSER_REG_02_Install_Idempotent()
        {
            var dir = TempDir(out var root);
            try
            {
                var registry = new MemoryRegistryView();
                var hostExe = Path.Combine(root, "DRRipper.NativeHost.exe");
                File.WriteAllText(hostExe, "stub");
                var first = BrowserRegistration.Install(
                    BrowserKind.Chrome, hostExe, "ext-id-0001", registry, dir);
                var second = BrowserRegistration.Install(
                    BrowserKind.Chrome, hostExe, "ext-id-0001", registry, dir);
                Assert.Equal(BrowserRegistrationState.Installed, first.State);
                Assert.Equal(BrowserRegistrationState.Installed, second.State);
                Assert.Equal(first.ManifestPath, second.ManifestPath);
                Assert.Equal(1, registry.Snapshot().Count); // one key, rewritten
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact] // T-BROWSER-REG-03: uninstall removes only DRRipper-owned registration.
        public void T_BROWSER_REG_03_Uninstall_Scoped()
        {
            var dir = TempDir(out var root);
            try
            {
                var registry = new MemoryRegistryView();
                registry.SetValue(@"Software\OtherVendor\Thing", string.Empty, "untouched");
                var hostExe = Path.Combine(root, "DRRipper.NativeHost.exe");
                File.WriteAllText(hostExe, "stub");
                BrowserRegistration.Install(BrowserKind.Edge, hostExe, "ext-id-2", registry, dir);
                Assert.True(registry.KeyExists(BrowserRegistration.RegistryKeyPath(BrowserKind.Edge)));
                BrowserRegistration.Uninstall(BrowserKind.Edge, registry, dir);
                Assert.False(registry.KeyExists(BrowserRegistration.RegistryKeyPath(BrowserKind.Edge)));
                Assert.Equal("untouched", registry.GetValue(@"Software\OtherVendor\Thing", string.Empty));
                // Second uninstall is a harmless no-op.
                BrowserRegistration.Uninstall(BrowserKind.Edge, registry, dir);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact] // T-BROWSER-REG-04: installed path with spaces is quoted-safe JSON.
        public void T_BROWSER_REG_04_Path_Escaping()
        {
            var dir = TempDir(out var root);
            try
            {
                var registry = new MemoryRegistryView();
                var hostExe = Path.Combine(root, "dir with spaces", "DRRipper.NativeHost.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(hostExe)!);
                File.WriteAllText(hostExe, "stub");
                var status = BrowserRegistration.Install(
                    BrowserKind.Chrome, hostExe, "ext-id-3", registry, dir);
                Assert.Equal(BrowserRegistrationState.Installed, status.State);
                var json = File.ReadAllText(status.ManifestPath!);
                Assert.Contains("dir with spaces", json);
                // Round-trips as valid JSON with the exact path.
                var doc = System.Text.Json.JsonDocument.Parse(json);
                Assert.Equal(hostExe, doc.RootElement.GetProperty("path").GetString());
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact] // T-BROWSER-REG-05: unknown browsers/ids fail safe, never half-register.
        public void T_BROWSER_REG_05_Unsupported_Safe()
        {
            var dir = TempDir(out var root);
            try
            {
                var registry = new MemoryRegistryView();
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    BrowserRegistration.RegistryKeyPath((BrowserKind)999));
                var wildcard = Record.Exception(() => BrowserRegistration.BuildHostManifestJson(
                    BrowserKind.Chrome, @"C:\x\DRRipper.NativeHost.exe", "*://*/"));
                Assert.NotNull(wildcard);
                Assert.True(registry.Snapshot().Count == 0);
                var empty = Record.Exception(() => BrowserRegistration.BuildHostManifestJson(
                    BrowserKind.Chrome, string.Empty, "ext-id"));
                Assert.NotNull(empty);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
