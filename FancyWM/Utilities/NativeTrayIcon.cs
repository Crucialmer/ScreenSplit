using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace FancyWM.Utilities
{
    /// <summary>
    /// Native Win32 Shell_NotifyIcon wrapper that uses standard NIF_TIP tooltips
    /// instead of Hardcodet's dual-layer NOTIFYICON_VERSION_4 WPF tooltip popup.
    /// </summary>
    internal sealed class NativeTrayIcon : IDisposable
    {
        private const int WM_NULL = 0x0000;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_TRAYICON = 0x8065;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const long ContextMenuDedupeMs = 120;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly HwndSource m_hwndSource;
        private readonly Icon m_icon;
        private readonly uint m_taskbarCreatedMsg;
        private readonly uint m_iconId = 1;
        private string m_toolTipText;
        private long m_lastContextMenuTickMs;
        private bool m_disposed;

        public event EventHandler? LeftClick;
        public event EventHandler? ContextMenuOpening;
        public event EventHandler? SystemThemeChanged;

        internal IntPtr WindowHandle => m_hwndSource.Handle;

        public ContextMenu? ContextMenu { get; set; }

        public bool IsAdded { get; private set; }

        public string ToolTipText
        {
            get => m_toolTipText;
            set
            {
                m_toolTipText = NormalizeTip(value);
                if (!m_disposed && IsAdded)
                {
                    var data = CreateNotifyIconData(NIF_TIP);
                    _ = Shell_NotifyIconW(NIM_MODIFY, ref data);
                }
            }
        }

        public NativeTrayIcon(Icon icon, string toolTipText, ContextMenu? contextMenu)
        {
            m_icon = (Icon)(icon ?? throw new ArgumentNullException(nameof(icon))).Clone();
            m_toolTipText = NormalizeTip(toolTipText);
            ContextMenu = contextMenu;
            m_taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

            var parameters = new HwndSourceParameters("ScreenSplitTrayHost")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                ExtendedWindowStyle = 0x00000080,        // WS_EX_TOOLWINDOW
            };
            m_hwndSource = new HwndSource(parameters);
            m_hwndSource.AddHook(WndProc);

            AddIcon();
        }

        private static string NormalizeTip(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return text.Length > 127 ? text[..127] : text;
        }

        private NOTIFYICONDATAW CreateNotifyIconData(uint flags)
        {
            return new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = m_hwndSource.Handle,
                uID = m_iconId,
                uFlags = flags,
                uCallbackMessage = WM_TRAYICON,
                hIcon = m_icon.Handle,
                szTip = m_toolTipText,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };
        }

        private void AddIcon()
        {
            var data = CreateNotifyIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            IsAdded = Shell_NotifyIconW(NIM_ADD, ref data);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (m_disposed)
            {
                return IntPtr.Zero;
            }

            if (m_taskbarCreatedMsg != 0 && (uint)msg == m_taskbarCreatedMsg)
            {
                AddIcon();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_SETTINGCHANGE)
            {
                SystemThemeChanged?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }

            if (msg == WM_TRAYICON)
            {
                int mouseMsg = unchecked((int)lParam.ToInt64()) & 0xFFFF;
                switch (mouseMsg)
                {
                    case WM_LBUTTONUP:
                    case WM_LBUTTONDBLCLK:
                        if (ContextMenu != null && ContextMenu.IsOpen)
                        {
                            ContextMenu.IsOpen = false;
                        }
                        LeftClick?.Invoke(this, EventArgs.Empty);
                        handled = true;
                        break;

                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU:
                        OpenContextMenu();
                        handled = true;
                        break;
                }
            }

            return IntPtr.Zero;
        }

        private void OpenContextMenu()
        {
            if (ContextMenu == null)
            {
                return;
            }

            long now = Environment.TickCount64;
            if ((now - m_lastContextMenuTickMs) < ContextMenuDedupeMs)
            {
                return;
            }
            m_lastContextMenuTickMs = now;

            ContextMenuOpening?.Invoke(this, EventArgs.Empty);
            _ = SetForegroundWindow(m_hwndSource.Handle);
            ContextMenu.Placement = PlacementMode.MousePoint;
            ContextMenu.IsOpen = true;
            _ = PostMessageW(m_hwndSource.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }
            m_disposed = true;

            if (IsAdded)
            {
                var data = CreateNotifyIconData(0);
                _ = Shell_NotifyIconW(NIM_DELETE, ref data);
                IsAdded = false;
            }

            m_hwndSource.RemoveHook(WndProc);
            m_hwndSource.Dispose();
            m_icon.Dispose();
        }
    }
}
