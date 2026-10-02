using System;
using System.IO;
using System.Threading.Tasks;
using DRRipper.UI;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>Ticket #006 settings tests (T-SETTINGS-01..06). Temp paths only.</summary>
    public sealed class SchedulerSettingsTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

        private static string TempPath(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "DRRipperSettingsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        private static async Task<T> WithBudget<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, $"Timed out: {what}");
            return await task;
        }

        private static async Task AwaitBudget(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Budget));
            Assert.True(winner == task, $"Timed out: {what}");
            await task;
        }

        [Fact] // T-SETTINGS-01: fresh defaults
        public void T_SETTINGS_01_Fresh_Defaults()
        {
            var s = AppSettings.Defaults("C:\\dl");
            Assert.Equal(1, s.Version);
            Assert.Equal(3, s.ActiveDownloadLimit);
            Assert.Equal(8, s.ConnectionsPerFileDefault);
            Assert.Equal(16, s.GlobalConnectionBudget);
            Assert.Equal(8, s.PerHostConnectionBudget);
            Assert.Equal("C:\\dl", s.DefaultDownloadDirectory);
            Assert.Equal(1000, s.UiRefreshMs);
        }

        [Fact] // T-SETTINGS-02: round-trip persistence
        public async Task T_SETTINGS_02_Roundtrip()
        {
            var path = TempPath(out var dir);
            try
            {
                var svc = new SettingsService(path);
                var src = AppSettings.Defaults("D:\\media");
                src.ActiveDownloadLimit = 5;
                src.GlobalConnectionBudget = 32;
                src.PerHostConnectionBudget = 12;
                src.UiRefreshMs = 500;
                await AwaitBudget(svc.SaveAsync(src), "save");
                var svc2 = new SettingsService(path);
                var loaded = await WithBudget(svc2.LoadAsync(), "load");
                Assert.False(svc2.LastLoadWasFallback);
                Assert.Equal(5, loaded.ActiveDownloadLimit);
                Assert.Equal(32, loaded.GlobalConnectionBudget);
                Assert.Equal(12, loaded.PerHostConnectionBudget);
                Assert.Equal(500, loaded.UiRefreshMs);
                Assert.Equal("D:\\media", loaded.DefaultDownloadDirectory);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-SETTINGS-03: corrupt settings safely reset (queue untouched conceptually — separate file)
        public async Task T_SETTINGS_03_Corrupt_Resets()
        {
            var path = TempPath(out var dir);
            try
            {
                await File.WriteAllTextAsync(path, "{ this is not json {{{");
                var svc = new SettingsService(path);
                var loaded = await WithBudget(svc.LoadAsync(), "load corrupt");
                Assert.True(svc.LastLoadWasFallback);
                Assert.Equal(3, loaded.ActiveDownloadLimit);
                Assert.Equal(16, loaded.GlobalConnectionBudget);
                // Truncated file likewise.
                await File.WriteAllTextAsync(path, "{\"Version\":1,\"ActiveDownloadLimit\":9");
                var svc2 = new SettingsService(path);
                var loaded2 = await WithBudget(svc2.LoadAsync(), "load truncated");
                Assert.True(svc2.LastLoadWasFallback);
                Assert.Equal(3, loaded2.ActiveDownloadLimit);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-SETTINGS-04: schema version mismatch handled
        public async Task T_SETTINGS_04_Version_Mismatch()
        {
            var path = TempPath(out var dir);
            try
            {
                // Future version: fall back, do not crash.
                await File.WriteAllTextAsync(path, "{\"Version\":999,\"ActiveDownloadLimit\":7}");
                var svc = new SettingsService(path);
                var loaded = await WithBudget(svc.LoadAsync(), "load future");
                Assert.True(svc.LastLoadWasFallback);
                Assert.Equal(1, loaded.Version);
                Assert.Equal(3, loaded.ActiveDownloadLimit);
                // Out-of-range values normalize instead of propagating.
                await File.WriteAllTextAsync(path, "{\"Version\":1,\"ActiveDownloadLimit\":-50,\"UiRefreshMs\":999999}");
                var svc2 = new SettingsService(path);
                var loaded2 = await WithBudget(svc2.LoadAsync(), "load wild");
                Assert.Equal(1, loaded2.ActiveDownloadLimit);
                Assert.Equal(5000, loaded2.UiRefreshMs);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-SETTINGS-05: interrupted write never replaces canonical settings
        public async Task T_SETTINGS_05_Atomic_Survives_Interrupted_Write()
        {
            var path = TempPath(out var dir);
            try
            {
                var svc = new SettingsService(path);
                var good = AppSettings.Defaults("D:\\good");
                good.ActiveDownloadLimit = 4;
                await AwaitBudget(svc.SaveAsync(good), "save good");
                // Simulate a crash mid-save: torn temp + stray temp files present.
                await File.WriteAllTextAsync(path + ".tmp.DEADBEEF", "{\"Version\":1,\"ActiveDownloadLimit\":");
                await File.WriteAllTextAsync(path + ".tmp.1234", "garbage");
                var svc2 = new SettingsService(path);
                var loaded = await WithBudget(svc2.LoadAsync(), "load after torn write");
                Assert.False(svc2.LastLoadWasFallback);
                Assert.Equal(4, loaded.ActiveDownloadLimit);
                Assert.Equal("D:\\good", loaded.DefaultDownloadDirectory);
                // A real save cleans up and replaces atomically.
                good.ActiveDownloadLimit = 6;
                await AwaitBudget(svc2.SaveAsync(good), "save again");
                Assert.False(File.Exists(path + ".tmp.DEADBEEF"));
                var loaded2 = await WithBudget(new SettingsService(path).LoadAsync(), "reload");
                Assert.Equal(6, loaded2.ActiveDownloadLimit);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-SETTINGS-06: unicode path roundtrip
        public async Task T_SETTINGS_06_Unicode_Roundtrip()
        {
            var path = TempPath(out var dir);
            try
            {
                var svc = new SettingsService(path);
                var src = AppSettings.Defaults("D:\\média-日本語\\загрузки");
                await AwaitBudget(svc.SaveAsync(src), "save");
                var loaded = await WithBudget(new SettingsService(path).LoadAsync(), "load");
                Assert.Equal("D:\\média-日本語\\загрузки", loaded.DefaultDownloadDirectory);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
