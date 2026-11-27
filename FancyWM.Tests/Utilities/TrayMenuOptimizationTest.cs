using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class TrayMenuOptimizationTest
    {
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint WM_TRAYICON = 0x8065;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int WM_RBUTTONUP = 0x0205;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static void RunOnStaThread(Action action)
        {
            Exception captured = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (captured != null)
            {
                ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }

        [TestMethod]
        public void ApplyTrayMenuThemeSwitchesDarkAndLightBrushes()
        {
            RunOnStaThread(() =>
            {
                var menu = new ContextMenu();

                LiquidGlassTheme.ApplyTrayMenuTheme(menu, isDark: true, force: true);
                var darkBg = menu.Resources["LiquidGlassMenuBg"] as SolidColorBrush;
                var darkFg = menu.Resources["LiquidGlassPrimaryFg"] as SolidColorBrush;
                var darkHalo = menu.Resources["LiquidGlassStatusDotHalo"] as RadialGradientBrush;
                Assert.IsNotNull(darkBg);
                Assert.IsNotNull(darkFg);
                Assert.IsNotNull(darkHalo);
                Assert.AreEqual(Color.FromArgb(0xF2, 0x1E, 0x20, 0x28), darkBg.Color);
                Assert.AreEqual(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF), darkFg.Color);

                LiquidGlassTheme.ApplyTrayMenuTheme(menu, isDark: false, force: true);
                var lightBg = menu.Resources["LiquidGlassMenuBg"] as SolidColorBrush;
                var lightFg = menu.Resources["LiquidGlassPrimaryFg"] as SolidColorBrush;
                Assert.IsNotNull(lightBg);
                Assert.IsNotNull(lightFg);
                Assert.AreEqual(Color.FromArgb(0xF6, 0xFC, 0xFD, 0xFF), lightBg.Color);
                Assert.AreEqual(Color.FromArgb(0xEC, 0x11, 0x13, 0x18), lightFg.Color);
            });
        }

        [TestMethod]
        public void NativeTrayIconDeduplicatesRightClickAndForwardsSettingChange()
        {
            RunOnStaThread(() =>
            {
                using var icon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
                var menu = new ContextMenu();
                using var tray = new NativeTrayIcon(icon, "ScreenSplit (分屏管理器)", menu);

                int menuOpenCount = 0;
                int themeChangeCount = 0;
                tray.ContextMenuOpening += (_, _) => menuOpenCount++;
                tray.SystemThemeChanged += (_, _) => themeChangeCount++;

                _ = SendMessageW(tray.WindowHandle, WM_TRAYICON, IntPtr.Zero, (IntPtr)WM_RBUTTONUP);
                _ = SendMessageW(tray.WindowHandle, WM_TRAYICON, IntPtr.Zero, (IntPtr)WM_CONTEXTMENU);
                Assert.AreEqual(1, menuOpenCount, "Back-to-back WM_RBUTTONUP + WM_CONTEXTMENU must trigger only once.");
                menu.IsOpen = false;

                _ = SendMessageW(tray.WindowHandle, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
                Assert.AreEqual(1, themeChangeCount, "WM_SETTINGCHANGE should raise SystemThemeChanged.");
            });
        }
    }
}
