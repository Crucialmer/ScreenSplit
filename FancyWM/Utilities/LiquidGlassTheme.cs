using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

using ModernWpf;

namespace FancyWM.Utilities
{
    /// <summary>
    /// macOS 26 (Tahoe) Liquid Glass dynamic theme engine and Windows 11 DWM backdrop helper.
    /// Synchronizes anisotropic specular borders, liquid refraction sheen, and semantic colors
    /// across Light and Dark modes without allocating per-frame resources.
    /// </summary>
    internal static class LiquidGlassTheme
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_MAINWINDOW = 2;       // Mica
        private const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic
        private const int DWMSBT_TABBEDWINDOW = 4;     // Mica Alt (subtle wallpaper-tinted glass)
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

        private static bool s_initialized;
        private static BitmapSource? s_cachedAppIcon;

        public static bool IsDarkTheme =>
            ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark;

        public static void Initialize()
        {
            if (s_initialized)
                return;

            s_initialized = true;
            ApplyResources(IsDarkTheme);
            ThemeManager.Current.ActualApplicationThemeChanged += (_, _) =>
            {
                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    dispatcher.BeginInvoke(() => ApplyResources(IsDarkTheme));
                }
            };
        }

        public static BitmapSource? GetAppIconImage()
        {
            if (s_cachedAppIcon != null)
                return s_cachedAppIcon;

            try
            {
                var uri = new Uri("pack://application:,,,/Icon.png", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 128;
                bmp.EndInit();
                bmp.Freeze();
                s_cachedAppIcon = bmp;
                return s_cachedAppIcon;
            }
            catch
            {
                try
                {
                    var uri = new Uri("pack://application:,,,/Resources/Icon.ico", UriKind.Absolute);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = uri;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    s_cachedAppIcon = bmp;
                    return s_cachedAppIcon;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static void ApplyResources(bool isDark)
        {
            if (Application.Current?.Resources is not ResourceDictionary res)
                return;

            // Core Liquid Glass surfaces
            SetFrozen(res, "LiquidGlassWindowBg", CreateSolid(isDark ? Color.FromArgb(0xEE, 0x16, 0x18, 0x1E) : Color.FromArgb(0xF5, 0xF5, 0xF7, 0xFB)));
            SetFrozen(res, "LiquidGlassSettingsShellBg", CreateSolid(isDark ? Color.FromArgb(0xF2, 0x14, 0x16, 0x1B) : Color.FromArgb(0xF5, 0xF2, 0xF4, 0xF8)));
            SetFrozen(res, "LiquidGlassSidebarBg", CreateSolid(isDark ? Color.FromArgb(0xD8, 0x1E, 0x21, 0x29) : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)));
            SetFrozen(res, "LiquidGlassTitleBarBg", CreateSolid(isDark ? Color.FromArgb(0x66, 0x24, 0x28, 0x33) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
            SetFrozen(res, "LiquidGlassCardBg", CreateSolid(isDark ? Color.FromArgb(0xC2, 0x21, 0x24, 0x2D) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
            SetFrozen(res, "LiquidGlassCardHoverBg", CreateSolid(isDark ? Color.FromArgb(0xDC, 0x28, 0x2C, 0x37) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)));
            SetFrozen(res, "LiquidGlassRecessedBg", CreateSolid(isDark ? Color.FromArgb(0xA8, 0x12, 0x14, 0x1A) : Color.FromArgb(0xC4, 0xEC, 0xEF, 0xF5)));
            SetFrozen(res, "LiquidGlassMenuBg", CreateSolid(isDark ? Color.FromArgb(0xF0, 0x1E, 0x20, 0x28) : Color.FromArgb(0xF2, 0xFC, 0xFD, 0xFF)));
            SetFrozen(res, "LiquidGlassToastBg", CreateSolid(isDark ? Color.FromArgb(0xE8, 0x1A, 0x1D, 0x25) : Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));

            // Anisotropic specular rim borders (macOS 26 Tahoe optical signature)
            SetFrozen(res, "LiquidGlassWindowBorder", CreateLinearGradient(
                new Point(0, 0), new Point(1, 1),
                isDark ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF),
                isDark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00),
                isDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00)));

            SetFrozen(res, "LiquidGlassCardBorder", CreateLinearGradient(
                new Point(0, 0), new Point(0.5, 1),
                isDark ? Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF),
                isDark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
                isDark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x00, 0x00, 0x00)));

            // Top specular refraction sheen
            SetFrozen(res, "LiquidGlassTopSheen", CreateLinearGradient(
                new Point(0.5, 0), new Point(0.5, 1),
                isDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF),
                isDark ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)));

            // Hairline separator
            SetFrozen(res, "LiquidGlassSeparator", CreateSolid(isDark ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x00, 0x00, 0x00)));

            // Typography colors
            SetFrozen(res, "LiquidGlassPrimaryFg", CreateSolid(isDark ? Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xEC, 0x11, 0x13, 0x18)));
            SetFrozen(res, "LiquidGlassSecondaryFg", CreateSolid(isDark ? Color.FromArgb(0xA6, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0x9C, 0x3C, 0x3C, 0x43)));
            SetFrozen(res, "LiquidGlassTertiaryFg", CreateSolid(isDark ? Color.FromArgb(0x6E, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0x6E, 0x3C, 0x3C, 0x43)));

            // Semantic accents (Apple macOS 26 system palette)
            var accent = isDark ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x00, 0x7A, 0xFF);
            var accentHover = isDark ? Color.FromRgb(0x2E, 0x99, 0xFF) : Color.FromRgb(0x14, 0x86, 0xFF);
            var accentPressed = isDark ? Color.FromRgb(0x00, 0x6A, 0xE0) : Color.FromRgb(0x00, 0x63, 0xD1);
            var emerald = isDark ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0x24, 0xC1, 0x4B);
            var emeraldHover = isDark ? Color.FromRgb(0x45, 0xDE, 0x6C) : Color.FromRgb(0x2E, 0xD0, 0x56);
            var destructive = isDark ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0xFF, 0x3B, 0x30);

            SetFrozen(res, "LiquidGlassAccentBrush", CreateSolid(accent));
            SetFrozen(res, "LiquidGlassAccentHoverBrush", CreateSolid(accentHover));
            SetFrozen(res, "LiquidGlassAccentPressedBrush", CreateSolid(accentPressed));
            SetFrozen(res, "LiquidGlassAccentTintBg", CreateSolid(Color.FromArgb(isDark ? (byte)0x2C : (byte)0x1A, accent.R, accent.G, accent.B)));
            SetFrozen(res, "LiquidGlassAccentTintBorder", CreateSolid(Color.FromArgb(isDark ? (byte)0x5C : (byte)0x38, accent.R, accent.G, accent.B)));

            SetFrozen(res, "LiquidGlassEmeraldBrush", CreateSolid(emerald));
            SetFrozen(res, "LiquidGlassEmeraldHoverBrush", CreateSolid(emeraldHover));
            SetFrozen(res, "LiquidGlassDestructiveBrush", CreateSolid(destructive));
            SetFrozen(res, "LiquidGlassDestructiveHoverBg", CreateSolid(Color.FromArgb(isDark ? (byte)0x2A : (byte)0x18, destructive.R, destructive.G, destructive.B)));

            // Interactive states (nav pills, glass buttons, controls)
            SetFrozen(res, "LiquidGlassControlBg", CreateSolid(isDark ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)));
            SetFrozen(res, "LiquidGlassControlHoverBg", CreateSolid(isDark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)));
            SetFrozen(res, "LiquidGlassControlPressedBg", CreateSolid(isDark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x94, 0xE6, 0xEA, 0xF2)));
            SetFrozen(res, "LiquidGlassNavHoverBg", CreateSolid(isDark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0x00, 0x00, 0x00)));
            SetFrozen(res, "LiquidGlassNavSelectedBg", CreateSolid(Color.FromArgb(isDark ? (byte)0x30 : (byte)0x1E, accent.R, accent.G, accent.B)));
            SetFrozen(res, "LiquidGlassNavSelectedBorder", CreateSolid(Color.FromArgb(isDark ? (byte)0x58 : (byte)0x3A, accent.R, accent.G, accent.B)));

            // Toggle switch track & progress/scrollbar
            SetFrozen(res, "LiquidGlassSwitchOffBg", CreateSolid(isDark ? Color.FromArgb(0x38, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0x28, 0x3C, 0x3C, 0x43)));
            SetFrozen(res, "LiquidGlassSwitchOffBorder", CreateSolid(isDark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00)));
            SetFrozen(res, "LiquidGlassProgressTrack", CreateSolid(isDark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00)));
            SetFrozen(res, "LiquidGlassScrollThumb", CreateSolid(isDark ? Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x3C, 0x00, 0x00, 0x00)));
            SetFrozen(res, "LiquidGlassScrollThumbHover", CreateSolid(isDark ? Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x60, 0x00, 0x00, 0x00)));

            // Snap preview overlay glass pill (dark frosted glass ensures crisp contrast over both light & dark apps)
            SetFrozen(res, "LiquidGlassOverlayPillBg", CreateSolid(isDark ? Color.FromArgb(0xDC, 0x16, 0x19, 0x22) : Color.FromArgb(0xE8, 0xFA, 0xFB, 0xFF)));
            SetFrozen(res, "LiquidGlassOverlayPillFg", CreateSolid(isDark ? Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xEE, 0x11, 0x14, 0x1A)));
            SetFrozen(res, "LiquidGlassOverlayPillBorder", CreateLinearGradient(
                new Point(0, 0), new Point(1, 1),
                isDark ? Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x55, accent.R, accent.G, accent.B),
                isDark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x28, 0x00, 0x00, 0x00)));
        }

        /// <summary>
        /// Applies Windows 11 native rounded corners, dark-mode title bar frame, and optional Mica Alt backdrop
        /// to a non-AllowsTransparency WindowChrome window. Safe no-op on Windows 10.
        /// </summary>
        public static void ApplyNativeWindowBackdrop(Window window, bool enableMicaBackdrop = true)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                if (hwnd == IntPtr.Zero)
                    return;

                int darkMode = IsDarkTheme ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int build = Environment.OSVersion.Version.Build;
                if (build >= 22000)
                {
                    int cornerPref = DWMWCP_ROUND;
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                    uint borderNone = DWMWA_COLOR_NONE;
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderNone, sizeof(uint));
                }

                if (enableMicaBackdrop && build >= 22621 && !window.AllowsTransparency)
                {
                    int backdrop = DWMSBT_TABBEDWINDOW;
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
            }
            catch
            {
                // Gracefully degrade on older Windows builds
            }
        }

        /// <summary>
        /// Plays a subtle macOS 26 spring-like fade and upward micro-lift entrance animation.
        /// </summary>
        public static void PlayEntranceAnimation(FrameworkElement element, TranslateTransform? translateTransform = null, double liftPixels = 10, int durationMs = 210)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(durationMs);

            element.Opacity = 0;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

            if (translateTransform != null)
            {
                translateTransform.Y = liftPixels;
                translateTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(liftPixels, 0, duration) { EasingFunction = ease });
            }
        }

        private static SolidColorBrush CreateSolid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush CreateLinearGradient(Point start, Point end, Color c0, Color c1, Color c2)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end,
                GradientStops =
                {
                    new GradientStop(c0, 0.0),
                    new GradientStop(c1, 0.45),
                    new GradientStop(c2, 1.0),
                }
            };
            brush.Freeze();
            return brush;
        }

        private static void SetFrozen(ResourceDictionary res, string key, Freezable value)
        {
            if (value.CanFreeze && !value.IsFrozen)
            {
                value.Freeze();
            }
            res[key] = value;
        }
    }
}
