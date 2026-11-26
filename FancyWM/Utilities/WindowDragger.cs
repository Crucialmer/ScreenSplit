using System;
using System.Windows.Threading;

using WinMan;
using WinMan.Windows;

namespace FancyWM.Utilities
{
    internal class WindowDragger
    {
        public IWindow Window => m_window;

        private readonly Win32Window m_window;
        private readonly Dispatcher? m_uiDispatcher;
        private readonly int m_xOffset;
        private readonly int m_yOffset;

        public WindowDragger(IWindow window)
        {
            m_window = (Win32Window)window;
            m_uiDispatcher = App.Current?.Dispatcher;
            var rect = window.Position;
            m_xOffset = m_window.Workspace.CursorLocation.X - rect.Left;
            m_yOffset = m_window.Workspace.CursorLocation.Y - rect.Top;
        }

        private void DispatchOnUi(Action action)
        {
            if (m_uiDispatcher != null)
            {
                _ = m_uiDispatcher.InvokeAsync(action);
            }
            else
            {
                action();
            }
        }

        internal void Begin(bool activateWindow)
        {
            // 绝不在 WH_MOUSE_LL 低级鼠标钩子线程同步执行 SetWindowPos / RaisePositionChangeStart，
            // 先调度拖拽生命周期启动，避免与外部拖拽识别冲突或触发系统钩子超时被静默卸载。
            DispatchOnUi(() =>
            {
                try
                {
                    if (m_window.State != WindowState.Restored)
                    {
                        m_window.SetState(WindowState.Restored);
                    }
                    if (activateWindow)
                    {
                        FocusHelper.ForceActivate(m_window.Handle);
                    }
                    m_window.RaisePositionChangeStart();
                    m_window.Workspace.CursorLocationChanged += OnCursorLocationChanged;
                }
                catch
                {
                    m_window.Workspace.CursorLocationChanged -= OnCursorLocationChanged;
                }
            });
        }

        internal void End()
        {
            m_window.Workspace.CursorLocationChanged -= OnCursorLocationChanged;
            DispatchOnUi(() =>
            {
                try
                {
                    m_window.RaisePositionChangeEnd();
                }
                catch
                {
                }
            });
        }

        private void OnCursorLocationChanged(object? sender, CursorLocationChangedEventArgs e)
        {
            try
            {
                var current = m_window.Position;
                m_window.SetPosition(Rectangle.OffsetAndSize(e.NewLocation.X - m_xOffset, e.NewLocation.Y - m_yOffset, current.Width, current.Height));
            }
            catch (InvalidWindowReferenceException)
            {
                m_window.Workspace.CursorLocationChanged -= OnCursorLocationChanged;
            }
            catch (Exception)
            {
                m_window.Workspace.CursorLocationChanged -= OnCursorLocationChanged;
            }
        }
    }
}