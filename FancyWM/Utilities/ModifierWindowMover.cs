using System;

using FancyWM.DllImports;

using Serilog;

using WinMan;

namespace FancyWM.Utilities
{
    internal class ModifierWindowMover : IDisposable
    {
        public bool IsEnabled { get; set; }

        public bool AutoFocus { get; set; }

        private readonly ILogger m_logger = App.Current.Logger;
        private readonly LowLevelMouseHook m_mshk;
        private readonly IWorkspace m_workspace;
        private WindowDragger? m_windowDragger = null;

        public ModifierWindowMover(IWorkspace workspace, LowLevelMouseHook mshk)
        {
            m_workspace = workspace;
            m_mshk = mshk;
            m_mshk.ButtonStateChanged += OnMouseButtonStateChanged;
        }

        private void OnMouseButtonStateChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
        {
            e.Handled = false;
            if (e.Button != LowLevelMouseHook.MouseButton.Left)
            {
                return;
            }

            if (m_windowDragger is WindowDragger activeDragger)
            {
                if (!e.IsPressed)
                {
                    e.Handled = true;
                    m_windowDragger = null;
                    activeDragger.End();
                    return;
                }

                // 若意外残留旧拖拽器而新的左键按下到来，先安全清理再继续，防止永久卡死
                m_windowDragger = null;
                activeDragger.End();
            }

            if (!IsEnabled || !e.IsPressed || !IsMoveModifierPressed())
            {
                return;
            }

            try
            {
                var pt = new Point(e.X, e.Y);
                var activateWindow = AutoFocus || IsMoveActivateModifierPressed();
                var window = m_workspace.FindWindowFromPoint(pt);
                if (window == null)
                {
                    return;
                }

                var dragger = new WindowDragger(window);
                m_windowDragger = dragger;
                try
                {
                    dragger.Begin(activateWindow);
                }
                catch (Exception)
                {
                    dragger.End();
                    m_windowDragger = null;
                    return;
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                m_logger.Error(ex, "Occurred during drag mouse hook!");
            }
        }

        private static bool IsMoveModifierPressed()
        {
            // 在 WH_MOUSE_LL 钩子线程中直接无锁读取修饰键，严禁阻塞调用 WPF Dispatcher.Invoke 以免触发系统钩子超时被静默卸载
            return (PInvoke.GetAsyncKeyState(0x12 /* VK_MENU / Alt */) & 0x8000) != 0;
        }

        private static bool IsMoveActivateModifierPressed()
        {
            return (PInvoke.GetAsyncKeyState(0x11 /* VK_CONTROL */) & 0x8000) != 0;
        }

        public void Dispose()
        {
            m_mshk.ButtonStateChanged -= OnMouseButtonStateChanged;
        }
    }
}
