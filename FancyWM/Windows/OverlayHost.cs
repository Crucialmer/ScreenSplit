using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using FancyWM.DllImports;
using FancyWM.Utilities;

using Microsoft.Extensions.DependencyInjection;

using WinMan;

namespace FancyWM.Windows
{
    /// <summary>
    /// This is basically a workaround for showing overlay content that is ignored by the operating system.
    /// It uses two identially-positioned windows, one with WS_EX_TRANSPARENT and one without.
    /// </summary>
    partial class OverlayHost : DependencyObject
    {

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, GetWindowLongPtr_nIndex nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, GetWindowLongPtr_nIndex nIndex, IntPtr dwNewLong);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, GetWindowLongPtr_nIndex nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
            {
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            }
            else
            {
                return SetWindowLong32(hWnd, nIndex, dwNewLong);
            }
        }

        [Flags()]
        internal enum SetWindowPosFlags : uint
        {
            IgnoreResize = 0x0001,
            IgnoreMove = 0x0002,
            DoNotActivate = 0x0010,
            DoNotSendChangingEvent = 0x0400,
            AsynchronousWindowPosition = 0x4000,
        }

        public static readonly DependencyProperty NonHitTestableContentProperty = DependencyProperty.Register(
            nameof(NonHitTestableContent),
            typeof(Control),
            typeof(OverlayHost),
            new PropertyMetadata(null));

        public Control? NonHitTestableContent
        {
            get => (Control)GetValue(NonHitTestableContentProperty);
            set => SetValue(NonHitTestableContentProperty, value);
        }

        public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
            nameof(Content),
            typeof(Control),
            typeof(OverlayHost),
            new PropertyMetadata(null));

        public Control? Content
        {
            get => (Control)GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        public Func<IntPtr>? AnchorSource { get; set; }

        private readonly IDisplay m_display;
        private readonly OverlayWindow m_window;
        private readonly OverlayWindow m_nonHitTestableWindow;
        private readonly IntPtr m_hwnd;
        private readonly IntPtr m_nonHitTestableHwnd;
        private readonly LowLevelMouseHook? m_mshk;
        private bool m_isShown;

        public OverlayHost(IDisplay display)
        {
            m_display = display;

            m_window = new OverlayWindow(display)
            {
                Title = "FancyWMInteractiveOverlay",
            };
            m_nonHitTestableWindow = new OverlayWindow(display)
            {
                IsHitTestVisible = false,
                Title = "FancyWMNonInteractiveOverlay",
            };

            m_hwnd = new WindowInteropHelper(m_window).EnsureHandle();
            m_nonHitTestableHwnd = new WindowInteropHelper(m_nonHitTestableWindow).EnsureHandle();

            // Set an owner relationship so that m_hwnd is always rendered on top of m_nonHitTestableHwnd.
            _ = SetWindowLongPtr(new(m_hwnd), GetWindowLongPtr_nIndex.GWLP_HWNDPARENT, m_nonHitTestableHwnd);

            var hitExStyle = (WINDOWS_EX_STYLE)PInvoke.GetWindowLong(new(m_hwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE);
            _ = SetWindowLongPtr(new(m_hwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE,
                (int)(hitExStyle | WINDOWS_EX_STYLE.WS_EX_TOOLWINDOW | WINDOWS_EX_STYLE.WS_EX_NOACTIVATE | WINDOWS_EX_STYLE.WS_EX_TOPMOST | WINDOWS_EX_STYLE.WS_EX_LAYERED));

            var nonHitExStyle = (WINDOWS_EX_STYLE)PInvoke.GetWindowLong(new(m_nonHitTestableHwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE);
            _ = SetWindowLongPtr(new(m_nonHitTestableHwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE,
                (int)(nonHitExStyle | WINDOWS_EX_STYLE.WS_EX_TOOLWINDOW | WINDOWS_EX_STYLE.WS_EX_TRANSPARENT | WINDOWS_EX_STYLE.WS_EX_NOACTIVATE | WINDOWS_EX_STYLE.WS_EX_TOPMOST | WINDOWS_EX_STYLE.WS_EX_LAYERED));

            DisableWindow(m_hwnd);
            // Note: do not disable m_nonHitTestableHwnd with WS_DISABLED because that breaks DWM composition on transparent windows
            UpdatePositions();

            display.Workspace.CursorLocationChanged += OnCursorLocationChanged;
            display.Workspace.FocusedWindowChanged += OnFocusedWindowChanged;
            display.Workspace.WindowAdded += OnWindowAdded;
            display.Workspace.WindowRemoved += OnWindowRemoved;

            if (App.Current.Services.GetService<LowLevelMouseHook>() is LowLevelMouseHook mshk)
            {
                m_mshk = mshk;
                m_mshk.ButtonStateChanged += OnMouseButtonStateChanged;
            }
        }

        public void Show()
        {
            m_nonHitTestableWindow.Show();
            // Hit-testable window goes on top, because it after an interaction,
            // it will be raised above the non-hit testable window anyway.
            m_window.Show();
            m_isShown = true;

            async void UpdateLoop()
            {
                while (m_isShown)
                {
                    UpdatePositions();
                    await Task.Delay(1000);
                }
            }
            UpdateLoop();
        }

        internal void Hide()
        {
            m_isShown = false;
            m_nonHitTestableWindow.Hide();
            m_window.Hide();
        }

        public void Close()
        {
            m_isShown = false;
            AnchorSource = null;
            m_display.Workspace.CursorLocationChanged -= OnCursorLocationChanged;
            m_display.Workspace.FocusedWindowChanged -= OnFocusedWindowChanged;
            m_display.Workspace.WindowAdded -= OnWindowAdded;
            m_display.Workspace.WindowRemoved -= OnWindowRemoved;
            if (m_mshk != null)
            {
                m_mshk.ButtonStateChanged -= OnMouseButtonStateChanged;
            }
            Dispatcher.Invoke(() =>
            {
                m_nonHitTestableWindow.Visibility = Visibility.Collapsed;
                m_nonHitTestableWindow.AllowClose = true;
                m_nonHitTestableWindow.Close();
                m_window.Visibility = Visibility.Collapsed;
                m_window.AllowClose = true;
                m_window.Close();
            });
        }

        public void UpdatePositions()
        {
            var swpFlags = SetWindowPos_uFlags.SWP_NOMOVE | SetWindowPos_uFlags.SWP_NOSIZE | SetWindowPos_uFlags.SWP_NOACTIVATE | SetWindowPos_uFlags.SWP_NOSENDCHANGING;
            PInvoke.SetWindowPos(new(m_nonHitTestableHwnd), new(new IntPtr(-1)), 0, 0, 0, 0, swpFlags);
            PInvoke.SetWindowPos(new(m_hwnd), new(new IntPtr(-1)), 0, 0, 0, 0, swpFlags);
        }

        public void EnsureTopmost()
        {
            var swpFlags = SetWindowPos_uFlags.SWP_NOMOVE | SetWindowPos_uFlags.SWP_NOSIZE | SetWindowPos_uFlags.SWP_NOACTIVATE | SetWindowPos_uFlags.SWP_NOSENDCHANGING;
            PInvoke.SetWindowPos(new(m_nonHitTestableHwnd), new(new IntPtr(-1)), 0, 0, 0, 0, swpFlags);
        }

        public void SetResource(string key, object? value)
        {
            m_window.Resources[key] = value;
            m_nonHitTestableWindow.Resources[key] = value;
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == ContentProperty)
            {
                m_window.SetContent(Content!);
            }
            else if (e.Property == NonHitTestableContentProperty)
            {
                m_nonHitTestableWindow.SetContent(NonHitTestableContent!);
            }
        }

        private static void EnableWindow(IntPtr hwnd)
        {
            var oldValue = PInvoke.GetWindowLong(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE);
            var newValue = oldValue & ~(int)WINDOWS_STYLE.WS_DISABLED;
            if (oldValue != newValue)
            {
                _ = SetWindowLongPtr(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE, newValue);
            }
        }

        private static void DisableWindow(IntPtr hwnd)
        {
            var oldValue = PInvoke.GetWindowLong(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE);
            var newValue = oldValue | (int)WINDOWS_STYLE.WS_DISABLED;
            if (oldValue != newValue)
            {
                _ = SetWindowLongPtr(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE, newValue);
            }
        }

        private void OnWindowRemoved(object? sender, WindowChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void OnWindowAdded(object? sender, WindowChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void OnFocusedWindowChanged(object? sender, FocusedWindowChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void OnMouseButtonStateChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void DispatchUpdatePositions()
        {
            if (m_isShown)
            {
                Dispatcher.BeginInvoke(() => UpdatePositions());
            }
        }

        private volatile bool m_isScheduled = false;
        private void OnCursorLocationChanged(object? sender, CursorLocationChangedEventArgs e)
        {
            if (!m_isScheduled)
            {
                m_isScheduled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    m_isScheduled = false;
                    var screenPoint = m_display.Workspace.CursorLocation;
                    bool wasHit;
                    try
                    {
                        var point = m_window.PointFromScreen(new(screenPoint.X, screenPoint.Y));
                        wasHit = VisualTreeHelper.HitTest(m_window, point) != null;
                    }
                    catch (InvalidOperationException)
                    {
                        // This Visual is not connected to a PresentationSource.
                        wasHit = false;
                    }

                    if (wasHit)
                    {
                        EnableWindow(m_hwnd);
                    }
                    else
                    {
                        DisableWindow(m_hwnd);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }
}
