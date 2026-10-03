using System.IO;
using System.Text.Json;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #007.1 extension packaging tests (T-EXT-PKG-01..09, T-EXT-JS-01..04).
    /// Validates the committed browser-extension tree: manifests parse, every
    /// referenced asset exists, PNGs are real and correctly sized, permissions
    /// are exactly the justified set, and the feedback/cancel-safety source
    /// properties hold. Pure file/parse checks — no browser, no registry.
    /// </summary>
    public sealed class ExtensionPackageTests
    {
        private static readonly string ExtensionDir = LocateExtensionDir();

        private static string LocateExtensionDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DRRipper.slnx")))
                {
                    var ext = Path.Combine(dir.FullName, "browser-extension");
                    if (Directory.Exists(ext))
                        return ext;
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "browser-extension directory not found above " + AppContext.BaseDirectory);
        }

        private static JsonDocument LoadManifest(string fileName)
        {
            var path = Path.Combine(ExtensionDir, fileName);
            Assert.True(File.Exists(path), "Missing manifest: " + fileName);
            var text = File.ReadAllText(path);
            return JsonDocument.Parse(text);
        }

        private static HashSet<string> PermissionsOf(JsonDocument manifest)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in manifest.RootElement.GetProperty("permissions").EnumerateArray())
                set.Add(p.GetString() ?? string.Empty);
            return set;
        }

        private static readonly HashSet<string> ExpectedPermissions = new(StringComparer.Ordinal)
        {
            "contextMenus", "downloads", "nativeMessaging", "storage", "notifications",
        };

        private static (int Width, int Height) PngDimensions(string path)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 32, "Empty/short file: " + path);
            byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
            Assert.True(signature.SequenceEqual(bytes[..8]), "Not a PNG file: " + path);
            // First chunk must be IHDR with width/height big-endian at 16..24.
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            // IEND trailer must exist (complete file, not truncated).
            var tail = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.True(tail.Contains("IEND"), "Truncated PNG (no IEND): " + path);
            return (width, height);
        }

        [Fact] // T-EXT-PKG-01: Chromium manifest parses as valid JSON with MV3 shape.
        public void T_EXT_PKG_01_Chromium_Manifest_Parses()
        {
            using var manifest = LoadManifest("manifest-chromium.json");
            var root = manifest.RootElement;
            Assert.Equal(3, root.GetProperty("manifest_version").GetInt32());
            Assert.True(root.TryGetProperty("background", out var bg));
            Assert.True(bg.TryGetProperty("service_worker", out var sw));
            Assert.Equal("background.js", sw.GetString());
            Assert.True(root.TryGetProperty("options_page", out _));
        }

        [Fact] // T-EXT-PKG-02: Firefox manifest parses as valid JSON with gecko id.
        public void T_EXT_PKG_02_Firefox_Manifest_Parses()
        {
            using var manifest = LoadManifest("manifest-firefox.json");
            var root = manifest.RootElement;
            Assert.Equal(3, root.GetProperty("manifest_version").GetInt32());
            Assert.True(root.TryGetProperty("background", out var bg));
            Assert.True(bg.TryGetProperty("scripts", out var scripts));
            var names = scripts.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("native.js", names);
            Assert.Contains("background.js", names);
            var gecko = root.GetProperty("browser_specific_settings").GetProperty("gecko");
            Assert.False(string.IsNullOrWhiteSpace(gecko.GetProperty("id").GetString()));
        }

        [Fact] // T-EXT-PKG-03: every manifest-referenced local file exists.
        public void T_EXT_PKG_03_Referenced_Files_Exist()
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in new[] { "manifest-chromium.json", "manifest-firefox.json" })
            {
                using var manifest = LoadManifest(file);
                var root = manifest.RootElement;
                foreach (var icon in root.GetProperty("icons").EnumerateObject())
                    referenced.Add(icon.Value.GetString()!);
                var bg = root.GetProperty("background");
                if (bg.TryGetProperty("service_worker", out var sw))
                    referenced.Add(sw.GetString()!);
                if (bg.TryGetProperty("scripts", out var scripts))
                    foreach (var s in scripts.EnumerateArray())
                        referenced.Add(s.GetString()!);
                if (root.TryGetProperty("options_page", out var op))
                    referenced.Add(op.GetString()!);
                if (root.TryGetProperty("options_ui", out var ui) &&
                    ui.TryGetProperty("page", out var pg))
                    referenced.Add(pg.GetString()!);
            }
            Assert.Contains("background.js", referenced);
            Assert.Contains("native.js", referenced);
            Assert.Contains("options.html", referenced);
            // options.js is loaded by options.html (not the manifest) — must
            // still ship with the package.
            Assert.True(File.Exists(Path.Combine(ExtensionDir, "options.js")), "Missing options.js");
            Assert.Contains("icons/dripper-16.png", referenced);
            Assert.Contains("icons/dripper-48.png", referenced);
            Assert.Contains("icons/dripper-128.png", referenced);
            foreach (var relative in referenced)
            {
                var full = Path.Combine(ExtensionDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(full), "Manifest references missing file: " + relative);
            }
        }

        [Theory] // T-EXT-PKG-04/05: PNGs are valid and exactly the manifest sizes.
        [InlineData("icons/dripper-16.png", 16)]
        [InlineData("icons/dripper-48.png", 48)]
        [InlineData("icons/dripper-128.png", 128)]
        public void T_EXT_PKG_04_05_Icons_Valid_And_Sized(string relative, int expected)
        {
            var full = Path.Combine(ExtensionDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "Missing icon: " + relative);
            var (width, height) = PngDimensions(full);
            Assert.Equal(expected, width);
            Assert.Equal(expected, height);
        }

        [Fact] // T-EXT-PKG-06: no manifest references missing files (sweep).
        public void T_EXT_PKG_06_No_Dangling_References()
        {
            foreach (var file in new[] { "manifest-chromium.json", "manifest-firefox.json" })
            {
                var text = File.ReadAllText(Path.Combine(ExtensionDir, file));
                // Every quoted local asset reference must resolve on disk.
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(text, "\"((?:icons/)?[\\w\\-]+\\.(?:png|js|html|svg))\""))
                {
                    var full = Path.Combine(ExtensionDir, m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(full), $"{file} references missing file: {m.Groups[1].Value}");
                }
            }
        }

        [Fact] // T-EXT-PKG-07: Chromium manifest includes notifications permission.
        public void T_EXT_PKG_07_Chromium_Notifications()
        {
            using var manifest = LoadManifest("manifest-chromium.json");
            Assert.Contains("notifications", PermissionsOf(manifest));
            Assert.Contains("nativeMessaging", PermissionsOf(manifest));
            Assert.Contains("downloads", PermissionsOf(manifest));
        }

        [Fact] // T-EXT-PKG-08: Firefox manifest includes notifications permission.
        public void T_EXT_PKG_08_Firefox_Notifications()
        {
            using var manifest = LoadManifest("manifest-firefox.json");
            Assert.Contains("notifications", PermissionsOf(manifest));
            Assert.Contains("nativeMessaging", PermissionsOf(manifest));
            Assert.Contains("downloads", PermissionsOf(manifest));
        }

        [Fact] // T-EXT-PKG-09: permission sets stay exactly the justified set.
        public void T_EXT_PKG_09_Permissions_Unchanged()
        {
            foreach (var file in new[] { "manifest-chromium.json", "manifest-firefox.json" })
            {
                using var manifest = LoadManifest(file);
                Assert.Equal(ExpectedPermissions, PermissionsOf(manifest));
            }
        }

        private static string ReadSource(string fileName)
        {
            var path = Path.Combine(ExtensionDir, fileName);
            Assert.True(File.Exists(path), "Missing source: " + fileName);
            return File.ReadAllText(path);
        }

        [Fact] // T-EXT-JS-01: cancellation strictly guarded by positive ack.
        public void T_EXT_JS_01_Cancel_Only_After_Ack()
        {
            var js = ReadSource("background.js");
            int ackIndex = js.IndexOf("result.accepted", StringComparison.Ordinal);
            int cancelIndex = js.IndexOf("downloads.cancel", StringComparison.Ordinal);
            Assert.True(ackIndex >= 0, "No accepted-guard found.");
            Assert.True(cancelIndex >= 0, "No cancel call found.");
            // The accepted check textually guards the cancel path (if-block above it).
            var guard = System.Text.RegularExpressions.Regex.Match(
                js, @"if\s*\(\s*result\.accepted\s*\)");
            Assert.True(guard.Success, "Cancel path is not guarded by 'if (result.accepted)'.");
            Assert.True(guard.Index < cancelIndex);
        }

        [Fact] // T-EXT-JS-02: notification feedback function exists and is best-effort.
        public void T_EXT_JS_02_Notify_Exists_Best_Effort()
        {
            var js = ReadSource("background.js");
            Assert.Contains("function notify(", js);
            Assert.Contains("chrome.notifications.create", js);
            // Failure must never propagate: the call lives in try/catch.
            int createIndex = js.IndexOf("chrome.notifications.create", StringComparison.Ordinal);
            int tryIndex = js.LastIndexOf("try", createIndex, StringComparison.Ordinal);
            Assert.True(tryIndex >= 0 && createIndex - tryIndex < 120);
        }

        [Fact] // T-EXT-JS-03: notification messages never carry raw URLs.
        public void T_EXT_JS_03_Notify_No_Raw_Urls()
        {
            var js = ReadSource("background.js");
            var matches = System.Text.RegularExpressions.Regex.Matches(js, @"notify\s*\(([^;]*?)\)");
            Assert.NotEmpty(matches);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var arg = m.Groups[1].Value;
                Assert.DoesNotContain("item.url", arg);
                Assert.DoesNotContain("info.linkUrl", arg);
                Assert.DoesNotContain("info.srcUrl", arg);
                Assert.DoesNotContain("finalUrl", arg);
            }
        }

        [Fact] // T-EXT-JS-04: no cookie harvesting introduced; deferral explicit.
        public void T_EXT_JS_04_No_Cookie_Harvesting()
        {
            var background = ReadSource("background.js");
            var native = ReadSource("native.js");
            foreach (var source in new[] { background, native })
            {
                Assert.DoesNotContain("chrome.cookies", source);
                Assert.DoesNotContain("document.cookie", source);
            }
            Assert.Contains("cookies: null", native); // explicit #012 deferral
        }
    }
}
