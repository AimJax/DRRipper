using System;
using System.Collections.Generic;
using DRRipper.UI;
using Xunit;

namespace DRRipper.Tests
{
    /// <summary>
    /// Ticket #006.1 window placement tests (T-WINDOW-01..06). Pure logic, headless.
    /// </summary>
    public sealed class SchedulerWindowPlacementTests
    {
        private static List<WindowPlacement.WorkArea> Dual1080p() => new()
        {
            new WindowPlacement.WorkArea(0, 0, 1920, 1040),
            new WindowPlacement.WorkArea(1920, 0, 1920, 1040),
        };

        [Fact] // T-WINDOW-01: size/position round-trip.
        public void T_WINDOW_01_Size_Position_Roundtrip()
        {
            var settings = AppSettings.Defaults();
            var captured = new WindowPlacement { Width = 1400, Height = 900, Left = 200, Top = 120, Maximized = false };
            captured.ApplyTo(settings);
            Assert.Equal(1400, settings.WindowWidth);
            Assert.Equal(900, settings.WindowHeight);
            Assert.Equal(200, settings.WindowLeft);
            Assert.Equal(120, settings.WindowTop);
            Assert.False(settings.WindowMaximized);
            var restored = WindowPlacement.FromSettings(settings).Normalized(Dual1080p());
            Assert.Equal(1400, restored.Width);
            Assert.Equal(900, restored.Height);
            Assert.Equal(200, restored.Left);
            Assert.Equal(120, restored.Top);
            Assert.False(restored.Maximized);
        }

        [Fact] // T-WINDOW-02: maximized state round-trip.
        public void T_WINDOW_02_Maximized_Roundtrip()
        {
            var settings = AppSettings.Defaults();
            new WindowPlacement { Width = 1100, Height = 640, Maximized = true }.ApplyTo(settings);
            Assert.True(settings.WindowMaximized);
            var restored = WindowPlacement.FromSettings(settings).Normalized(Dual1080p());
            Assert.True(restored.Maximized);
        }

        [Fact] // T-WINDOW-03: minimized state is never restored on launch.
        public void T_WINDOW_03_Minimized_Never_Restored()
        {
            // By construction WindowPlacement has no Minimized flag: capture code
            // maps Minimized → restored bounds + Maximized=false (see MainWindow).
            // A placement claiming maximized=false with valid bounds restores Normal.
            var restored = new WindowPlacement { Width = 1100, Height = 640, Left = 100, Top = 100, Maximized = false }
                .Normalized(Dual1080p());
            Assert.False(restored.Maximized);
            Assert.Equal(100, restored.Left);
        }

        [Fact] // T-WINDOW-04: off-screen placement is corrected.
        public void T_WINDOW_04_Offscreen_Corrected()
        {
            // Unplugged second monitor: old coordinates on the missing screen.
            var restored = new WindowPlacement { Width = 1100, Height = 640, Left = 2500, Top = 100, Maximized = false }
                .Normalized(new List<WindowPlacement.WorkArea> { new(0, 0, 1920, 1040) });
            Assert.True(restored.Left >= 0 && restored.Left + restored.Width <= 1920 + 1,
                $"Left={restored.Left} still off-screen.");
            Assert.True(restored.Top >= 0 && restored.Top < 1040);
            // Maximized windows are exempt from position checks (OS places them).
            var maxOff = new WindowPlacement { Width = 1100, Height = 640, Left = 5000, Top = 5000, Maximized = true }
                .Normalized(new List<WindowPlacement.WorkArea> { new(0, 0, 1920, 1040) });
            Assert.True(maxOff.Maximized);
        }

        [Fact] // T-WINDOW-05: settings save includes newly captured placement.
        public async Task T_WINDOW_05_Save_Includes_Placement()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DRRipperWndTests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var path = System.IO.Path.Combine(dir, "settings.json");
                var svc = new SettingsService(path);
                var settings = AppSettings.Defaults();
                // Simulate exit-time capture AFTER load (ordering §18).
                new WindowPlacement { Width = 1500, Height = 950, Left = 42, Top = 24, Maximized = false }
                    .ApplyTo(settings);
                await svc.SaveAsync(settings);
                var reloaded = await new SettingsService(path).LoadAsync();
                Assert.Equal(1500, reloaded.WindowWidth);
                Assert.Equal(950, reloaded.WindowHeight);
                Assert.Equal(42, reloaded.WindowLeft);
                Assert.Equal(24, reloaded.WindowTop);
                Assert.False(reloaded.WindowMaximized);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact] // T-WINDOW-06: unicode/default settings remain unaffected.
        public void T_WINDOW_06_Settings_Unaffected()
        {
            var s = AppSettings.Defaults("D:\\média-日本語");
            Assert.Equal(CloseButtonBehavior.MinimizeToTray, s.CloseBehavior);
            Assert.True(s.MinimizeToTray);
            Assert.True(double.IsNaN(s.WindowLeft));
            s.Normalize();
            Assert.Equal(CloseButtonBehavior.MinimizeToTray, s.CloseBehavior);
            // Unknown enum values from hand-edited files fall back safely.
            s.CloseBehavior = (CloseButtonBehavior)99;
            s.Normalize();
            Assert.Equal(CloseButtonBehavior.MinimizeToTray, s.CloseBehavior);
        }
    }
}
