using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;

using WinMan;
using WinMan.Windows;
using FancyWM.Layouts.Tiling;
using FancyWM.Layouts;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Reactive.Linq;
using System.Diagnostics;
using FancyWM.DllImports;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace FancyWM
{
    public enum ScreenSnapZone
    {
        None,
        LeftHalf,
        RightHalf,
        TopHalf,
        BottomHalf,
        TopLeftQuarter,
        TopRightQuarter,
        BottomLeftQuarter,
        BottomRightQuarter
    }

    internal partial class TilingService
    {
        private void RestoreOriginalLayout()
        {
            using (m_backendLock.EnterScope())
            {
                foreach (var desktop in m_workspace.VirtualDesktopManager.Desktops)
                {
                    try
                    {
                        var tree = m_backend.GetTree(desktop);
                        if (tree == null)
                            continue;

                        foreach (var window in tree.Root!.Windows)
                        {
                            var originalPosition = m_backend.GetOriginalPosition(window.WindowReference);
                            try
                            {
                                window.WindowReference.SetPosition(originalPosition);
                            }
                            catch (InvalidWindowReferenceException)
                            {
                                continue;
                            }
                            catch (InvalidOperationException) when (window.WindowReference.State != WindowState.Restored)
                            {
                                continue;
                            }
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        continue;
                    }
                    catch (InvalidOperationException e)
                    {
                        m_logger.Warning(e, "Exception thrown while restoring the original window layout!");
                    }
                }
            }
        }

        private TimeSpan m_lastUpdateLayout = TimeSpan.Zero;

        private void UpdateTree(DesktopTree tree)
        {
            tree.WorkArea = m_display.WorkArea;

            bool constraintsSatisfied = false;
            while (!constraintsSatisfied)
            {
                tree.Measure();
                try
                {
                    tree.Arrange();
                    constraintsSatisfied = true;
                }
                catch (UnsatisfiableFlexConstraintsException)
                {
                    var largestWindow = tree.Root!.Windows.OrderByDescending(x => x.GenerationID).First();
                    m_logger.Warning($"The arrange pass failed! Floating window {largestWindow.WindowReference.DebugString()} in an attempt to find a permissible arrangement!");
                    using (m_floatingSetLock.EnterScope())
                    {
                        m_floatingSet.Add(largestWindow.WindowReference);
                    }
                    DetectChanges(largestWindow.WindowReference);
                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(TilingError.NoValidPlacementExists, largestWindow.WindowReference));
                }
            }
        }

        private async Task UpdateLayoutAsync()
        {
            if (!Active)
                return;

            double refreshRate = m_display.RefreshRate > 0 ? m_display.RefreshRate : 60.0;
            if (m_currentInteraction != UserInteraction.None && m_sw.Elapsed - m_lastUpdateLayout <= TimeSpan.FromSeconds(1.0 / (refreshRate * 2.0)))
            {
                return;
            }
            m_lastUpdateLayout = m_sw.Elapsed;

            IVirtualDesktop desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;

            List<TilingNode> snapshot;
            IReadOnlyCollection<TilingNode> focusedPath;
            TilingNode? focusedNode;
            DesktopTree tree;

            using (m_backendLock.EnterScope())
            {
                try
                {
                    var treeOrNull = m_backend.GetTree(desktop);
                    if (treeOrNull == null)
                        return;
                    tree = treeOrNull;
                }
                catch (KeyNotFoundException)
                {
                    m_logger.Warning($"Current desktop {desktop} is not registered with backend, aborting...");
                    return;
                }

                UpdateTree(tree);

                snapshot = tree.Root!.Nodes.Skip(1).ToList();
                focusedNode = m_backend.GetFocus(desktop);
                focusedPath = (IReadOnlyCollection<TilingNode>?)focusedNode?.PathToRoot?.ToList() ?? [];
            }

            async ValueTask RepositionAsync()
            {
                try
                {
                    Freeze();
                    IList<WindowNode> snapshotWindows;
                    using (m_ignoreRepositionSetLock.EnterScope())
                    {
                        snapshotWindows = snapshot.OfType<WindowNode>().Where(x => !m_ignoreRepositionSet.Contains(x.WindowReference)).ToList();
                    }

                    bool useSmoothing = m_animateWindowMovement && m_currentInteraction != UserInteraction.Resizing;
                    await UpdateWindowPositionsAsync(snapshotWindows, useSmoothing);
                }
                finally
                {
                    Unfreeze();
                }
            }

            if (m_focusSettleCts == null)
            {
                m_gui.FocusRectangle = GetFocusRectangle(focusedNode);
            }

            var repositionTask = RepositionAsync();

            m_gui.UpdateOverlay(snapshot, focusedPath);
            var previewInfo = GetPreviewInfo();
            m_gui.PreviewTitle = previewInfo.Title;
            m_gui.PreviewRectangle = previewInfo.Rect;

            if (m_showPreviewFocus)
            {
                // TODO: Can we just use focusedNode here?
                var previewWindows = m_workspace.VirtualDesktopManager.Desktops
                    .Select(desktop => m_backend.GetFocus(desktop))
                    .OfType<WindowNode>()
                    .Select(x => x.WindowReference)
                    .ToHashSet();
                m_gui.PreviewWindows = previewWindows;
            }
            else
            {
                m_gui.PreviewWindows = EmptyWindowSet;
            }

            await repositionTask;
        }

        private async Task UpdateWindowPositionsAsync(IEnumerable<WindowNode> snapshot, bool useSmoothing)
        {
            var targets = CalculateRepositionTargets(snapshot);
            foreach (var target in targets)
            {
                if (target.OriginalPosition != target.ComputedPosition)
                {
                    m_logger.Information("Relocating window {Window} from {OriginalPosition} to {ComputedPosition}",
                        target.Window.DebugString(),
                        target.OriginalPosition, target.ComputedPosition);
                }
                else
                {
                    m_logger.Information("Window {Window} location is {ComputedPosition}",
                        target.Window.DebugString(),
                        target.ComputedPosition);
                }
            }

            HashSet<IWindow>? newWindows = null;
            using (m_newWindowSetLock.EnterScope())
            {
                if (m_newWindowSet.Count > 0)
                {
                    newWindows = [.. m_newWindowSet];
                    m_newWindowSet.Clear();
                }
            }

            if (useSmoothing)
            {
                var focusRectangle = m_gui.FocusRectangle;
                m_gui.FocusRectangle = null;

                TransitionTargetGroup transitionGroup;
                if (newWindows != null)
                {
                    await TransitionTargetGroup.PerformTransitionAsync(targets.Where(x => newWindows!.Contains(x.Window)).ToList());
                    transitionGroup = new TransitionTargetGroup(m_animationThread, targets.Where(x => !newWindows!.Contains(x.Window)));
                }
                else
                {
                    transitionGroup = new TransitionTargetGroup(m_animationThread, targets);
                }
                await transitionGroup.PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));

                if (m_showFocus)
                {
                    UpdateFocusRectangleDirect();
                }
            }
            else
            {
                await TransitionTargetGroup.PerformTransitionAsync(targets);
            }
        }

        private List<TransitionTarget> CalculateRepositionTargets(IEnumerable<WindowNode> snapshot)
        {
            var targets = new List<TransitionTarget>();
            foreach (var window in snapshot)
            {
                try
                {
                    var currentPosition = window.WindowReference.Position;
                    if (!window.WindowReference.CanResize)
                    {
                        m_logger.Warning("Unresizable window {Window} will be moved only", window.WindowReference.DebugString());
                        var targetRect = ShrinkTo(window.ComputedRectangle, currentPosition.Width, currentPosition.Height);
                        if (targetRect == currentPosition)
                        {
                            continue;
                        }
                        targets.Add(new TransitionTarget(window.WindowReference, currentPosition, targetRect));
                    }
                    else
                    {
                        m_logger.Debug("Updating position of window {Window}", window.WindowReference.DebugString());
                        var rect = window.ComputedRectangle;
                        var frame = GetAccurateFrameMargins(window.WindowReference);
                        var adjustedRect = new Rectangle(
                            left: rect.Left - frame.Left,
                            top: rect.Top - frame.Top,
                            right: rect.Right + frame.Right,
                            bottom: rect.Bottom + frame.Bottom);

                        if (adjustedRect == currentPosition)
                        {
                            continue;
                        }

                        targets.Add(new TransitionTarget(window.WindowReference, currentPosition, adjustedRect));

                        var minSize = window.WindowReference.MinSize;
                        if (minSize.HasValue)
                        {
                            if (minSize.Value.X > adjustedRect.Width)
                            {
                                m_logger.Warning("New width for {Window} is smaller than the value reported by WM_GETMINMAXINFO ({ComputedWidth} < {MinimumWidth})",
                                    window.WindowReference.DebugString(), adjustedRect.Width, minSize.Value.X);
                            }
                            if (minSize.Value.Y > adjustedRect.Height)
                            {
                                m_logger.Warning("New height for {Window} is smaller than the value reported by WM_GETMINMAXINFO ({ComputedHeight} < {MinimumHeight})",
                                    window.WindowReference.DebugString(), adjustedRect.Height, minSize.Value.Y);
                            }
                        }
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    // Ignore
                }
                catch (Win32Exception e)
                {
                    m_logger.Error(e, "Failed to calculate reposition targets");
                }
            }
            return targets;
        }

        private bool CanShowFocusRectangle()
        {
            return false;
        }

        private void InvalidateFocusRectangle(bool immediate = false)
        {
            if (!m_dispatcher.CheckAccess())
            {
                m_dispatcher.InvokeAsync(() => InvalidateFocusRectangle(immediate), System.Windows.Threading.DispatcherPriority.Send);
                return;
            }

            m_focusSettleCts?.Cancel();
            m_focusSettleCts?.Dispose();
            m_focusSettleCts = null;

            if (!m_active || !CanShowFocusRectangle())
            {
                m_gui.FocusRectangle = null;
                m_currentFocusedTarget = null;
                return;
            }

            var focused = m_workspace.FocusedWindow;
            if (focused == null || !focused.IsAlive)
            {
                m_gui.FocusRectangle = null;
                m_currentFocusedTarget = null;
                return;
            }

            if (immediate)
            {
                m_currentFocusedTarget = focused;
                UpdateFocusRectangleDirect();
                return;
            }

            // 0ms Clear: When focus changes, immediately hide the highlight rectangle.
            // This resets IsFocusRectangleVisible = false with zero latency, wiping any DropShadow
            // shader cache pixels from the old window and preventing cross-window cut-through / ghosts.
            m_gui.FocusRectangle = null;
            m_currentFocusedTarget = focused;

            // DWM Settle Delay (~50ms / 2-3 frames):
            // Wait for Windows DWM to elevate the background window in Z-order and complete
            // its physical composition / redraw before we snap and fade in the focus glow.
            var cts = new CancellationTokenSource();
            m_focusSettleCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, cts.Token);
                    if (cts.Token.IsCancellationRequested)
                        return;

                    await m_dispatcher.InvokeAsync(() =>
                    {
                        if (cts.Token.IsCancellationRequested)
                            return;

                        m_focusSettleCts?.Dispose();
                        m_focusSettleCts = null;

                        if (!m_active || !CanShowFocusRectangle())
                            return;

                        if (Equals(m_workspace.FocusedWindow, focused))
                        {
                            UpdateFocusRectangleDirect();
                        }
                    }, System.Windows.Threading.DispatcherPriority.Render);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                }
            });
        }

        private void UpdateFocusRectangle()
        {
            InvalidateFocusRectangle(immediate: false);
        }

        private void UpdateFocusRectangleDirect()
        {
            if (!m_dispatcher.CheckAccess())
            {
                m_dispatcher.InvokeAsync(UpdateFocusRectangleDirect, System.Windows.Threading.DispatcherPriority.Render);
                return;
            }

            if (!m_active || !CanShowFocusRectangle())
            {
                m_gui.FocusRectangle = null;
                return;
            }

            TilingNode? focusedNode = null;
            try
            {
                var desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;
                using (m_backendLock.EnterScope())
                {
                    focusedNode = m_backend.GetFocus(desktop);
                }
            }
            catch
            {
            }

            m_gui.FocusRectangle = GetFocusRectangle(focusedNode);
        }

        private Rectangle? GetFocusRectangle(TilingNode? focusedNode)
        {
            if (!CanShowFocusRectangle())
            {
                return null;
            }

            if (focusedNode is WindowNode focusedWindow)
            {
                if (m_movingWindow != null && Equals(m_movingWindow, focusedWindow.WindowReference))
                {
                    var moveRect = GetVisibleWindowRectangle(focusedWindow.WindowReference);
                    if (moveRect.Width <= 0 || moveRect.Height <= 0)
                    {
                        moveRect = focusedWindow.WindowReference.Position;
                    }
                    if (moveRect.Width > 0 && moveRect.Height > 0 && m_display.Bounds.Contains(moveRect.Center))
                    {
                        return moveRect;
                    }
                    return null;
                }

                return focusedWindow.ComputedRectangle;
            }

            try
            {
                var focused = m_workspace.FocusedWindow;
                if (focused == null || !focused.IsAlive)
                {
                    return null;
                }

                if (focused.State != WindowState.Restored)
                {
                    return null;
                }

                if (focused.IsTopmost)
                {
                    return null;
                }

                if (m_workspace.VirtualDesktopManager.IsWindowPinned(focused))
                {
                    return null;
                }

                if (focused is Win32Window win32Window)
                {
                    var cls = win32Window.ClassName;
                    if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    {
                        return null;
                    }
                }

                try
                {
                    var proc = focused.GetProcess();
                    if (proc.Id == Environment.ProcessId ||
                        proc.ProcessName.Equals("ScreenSplit", StringComparison.OrdinalIgnoreCase) ||
                        proc.ProcessName.Equals("FancyWM", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }
                catch
                {
                }

                if (m_exclusionMatchers.Any(matcher => matcher.Matches(focused)))
                {
                    return null;
                }

                var rect = GetVisibleWindowRectangle(focused);
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    rect = focused.Position;
                }

                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return null;
                }

                if (!m_display.Bounds.Contains(rect.Center))
                {
                    return null;
                }

                return rect;
            }
            catch (InvalidWindowReferenceException)
            {
                return null;
            }
            catch (Win32Exception)
            {
                return null;
            }
            catch (Exception ex)
            {
                m_logger.Debug(ex, "Failed to compute focus rectangle for focused window");
                return null;
            }
        }

        private void SetPreviewRectangle(Rectangle? rect, string? title = null)
        {
            if (m_lastPreviewRect == rect && string.Equals(m_lastPreviewTitle, title, StringComparison.Ordinal))
            {
                return;
            }
            m_lastPreviewRect = rect;
            m_lastPreviewTitle = title;

            if (m_dispatcher.CheckAccess())
            {
                m_gui.PreviewTitle = title;
                m_gui.PreviewRectangle = rect;
            }
            else
            {
                m_dispatcher.InvokeAsync(() =>
                {
                    m_gui.PreviewTitle = title;
                    m_gui.PreviewRectangle = rect;
                });
            }
        }

        private bool IsWindowSnapped(IWindow window, Rectangle pos, Rectangle wa)
        {
            if (IsWindowSnappedGeometry(pos, wa))
                return true;

            using (m_snappedWindowsLock.EnterScope())
            {
                if (m_snappedWindows.Contains(window) && (pos.Width >= wa.Width * 0.45 || pos.Height >= wa.Height * 0.45))
                    return true;
            }
            return false;
        }

        private static bool IsWindowSnappedGeometry(Rectangle pos, Rectangle wa)
        {
            if (pos.Width <= 0 || pos.Height <= 0) return false;

            int halfW = wa.Width / 2;
            int halfH = wa.Height / 2;
            int rightW = wa.Width - halfW;
            int bottomH = wa.Height - halfH;

            int tol = 35; // Tolerance for window borders, shadows and DPI scaling

            // Left Half
            if (Math.Abs(pos.Left - wa.Left) <= tol &&
                Math.Abs(pos.Top - wa.Top) <= tol &&
                Math.Abs(pos.Width - halfW) <= tol &&
                Math.Abs(pos.Height - wa.Height) <= tol)
                return true;

            // Right Half
            if (Math.Abs(pos.Right - wa.Right) <= tol &&
                Math.Abs(pos.Top - wa.Top) <= tol &&
                Math.Abs(pos.Width - rightW) <= tol &&
                Math.Abs(pos.Height - wa.Height) <= tol)
                return true;

            // Top Half
            if (Math.Abs(pos.Left - wa.Left) <= tol &&
                Math.Abs(pos.Top - wa.Top) <= tol &&
                Math.Abs(pos.Width - wa.Width) <= tol &&
                Math.Abs(pos.Height - halfH) <= tol)
                return true;

            // Bottom Half
            if (Math.Abs(pos.Left - wa.Left) <= tol &&
                Math.Abs(pos.Bottom - wa.Bottom) <= tol &&
                Math.Abs(pos.Width - wa.Width) <= tol &&
                Math.Abs(pos.Height - bottomH) <= tol)
                return true;

            // Top-Left Quarter
            if (Math.Abs(pos.Left - wa.Left) <= tol &&
                Math.Abs(pos.Top - wa.Top) <= tol &&
                Math.Abs(pos.Width - halfW) <= tol &&
                Math.Abs(pos.Height - halfH) <= tol)
                return true;

            // Top-Right Quarter
            if (Math.Abs(pos.Right - wa.Right) <= tol &&
                Math.Abs(pos.Top - wa.Top) <= tol &&
                Math.Abs(pos.Width - rightW) <= tol &&
                Math.Abs(pos.Height - halfH) <= tol)
                return true;

            // Bottom-Left Quarter
            if (Math.Abs(pos.Left - wa.Left) <= tol &&
                Math.Abs(pos.Bottom - wa.Bottom) <= tol &&
                Math.Abs(pos.Width - halfW) <= tol &&
                Math.Abs(pos.Height - bottomH) <= tol)
                return true;

            // Bottom-Right Quarter
            if (Math.Abs(pos.Right - wa.Right) <= tol &&
                Math.Abs(pos.Bottom - wa.Bottom) <= tol &&
                Math.Abs(pos.Width - rightW) <= tol &&
                Math.Abs(pos.Height - bottomH) <= tol)
                return true;

            return false;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPosNative(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(
            IntPtr hWnd,
            IntPtr lprcUpdate,
            IntPtr hrgnUpdate,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        private const uint SWP_NOSIZE_FLAG = 0x0001;
        private const uint SWP_NOMOVE_FLAG = 0x0002;
        private const uint SWP_NOZORDER_FLAG = 0x0004;
        private const uint SWP_NOACTIVATE_FLAG = 0x0010;
        private const uint SWP_FRAMECHANGED_FLAG = 0x0020;
        private const uint SWP_NOCOPYBITS_FLAG = 0x0100;
        private const uint SWP_ASYNCWINDOWPOS_FLAG = 0x4000;

        private const uint RDW_INVALIDATE_FLAG = 0x0001;
        private const uint RDW_ERASE_FLAG = 0x0004;
        private const uint RDW_ALLCHILDREN_FLAG = 0x0080;
        private const uint RDW_FRAME_FLAG = 0x0400;

        private const uint WM_NULL_MSG = 0x0000;
        private const uint WM_NCHITTEST_MSG = 0x0084;
        private const uint SMTO_NORMAL_FLAG = 0x0000;
        private const uint SMTO_ABORTIFHUNG_FLAG = 0x0002;

        private const int HT_CLIENT = 1;
        private const int HT_CAPTION = 2;
        private const int HT_SYSMENU = 3;
        private const int HT_MINBUTTON = 8;
        private const int HT_MAXBUTTON = 9;
        private const int HT_LEFT = 10;
        private const int HT_RIGHT = 11;
        private const int HT_TOP = 12;
        private const int HT_TOPLEFT = 13;
        private const int HT_TOPRIGHT = 14;
        private const int HT_BOTTOM = 15;
        private const int HT_BOTTOMLEFT = 16;
        private const int HT_BOTTOMRIGHT = 17;
        private const int HT_CLOSE = 20;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT_NATIVE
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hCursor;
            public POINT_NATIVE ptScreenPos;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT_NATIVE lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT_NATIVE point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        private const uint GA_ROOT_FLAG = 2;

        private Point GetLiveCursorLocation()
        {
            if (m_workspace is Win32Workspace && GetCursorPos(out var pt))
            {
                return new Point(pt.X, pt.Y);
            }
            return m_workspace.CursorLocation;
        }

        private static uint GetSafeWindowDpi(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return 0;
            try
            {
                return GetDpiForWindow(hwnd);
            }
            catch
            {
                return 0;
            }
        }

        private static readonly Lazy<IntPtr[]> s_resizeCursors = new(() =>
        [
            LoadCursor(IntPtr.Zero, (IntPtr)32642), // IDC_SIZENWSE
            LoadCursor(IntPtr.Zero, (IntPtr)32643), // IDC_SIZENESW
            LoadCursor(IntPtr.Zero, (IntPtr)32644), // IDC_SIZEWE
            LoadCursor(IntPtr.Zero, (IntPtr)32645), // IDC_SIZENS
        ]);

        private static bool IsResizeCursorActive()
        {
            try
            {
                var ci = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(ref ci) && ci.hCursor != IntPtr.Zero)
                {
                    var resizeCursors = s_resizeCursors.Value;
                    for (int i = 0; i < resizeCursors.Length; i++)
                    {
                        if (resizeCursors[i] != IntPtr.Zero && ci.hCursor == resizeCursors[i])
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

        private static readonly ConcurrentDictionary<IntPtr, Rectangle> s_hwndFloatingMemory = new();
        private static readonly ConcurrentDictionary<string, (int Width, int Height)> s_processFloatingMemory = new();
        private static readonly ConcurrentDictionary<IntPtr, byte> s_snappedHwnds = new();
        private static readonly ConcurrentDictionary<IntPtr, Rectangle> s_hwndPreSnapMemory = new();
        private static readonly ConcurrentDictionary<IntPtr, CancellationTokenSource> s_snapSettleTokens = new();

        private static void CancelPendingSnapSettle(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && s_snapSettleTokens.TryRemove(hwnd, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private static bool WaitForMoveSizeExit(IntPtr hwnd, uint timeoutMs = 35)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                return SendMessageTimeout(
                    hwnd,
                    WM_NULL_MSG,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    SMTO_NORMAL_FLAG | SMTO_ABORTIFHUNG_FLAG,
                    timeoutMs,
                    out _) != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private static Rectangle GetRawWindowRectangle(IWindow window)
        {
            IntPtr hwnd = window.Handle;
            if (hwnd != IntPtr.Zero &&
                PInvoke.GetWindowRect(new(hwnd), out RECT rc) &&
                rc.right > rc.left && rc.bottom > rc.top)
            {
                return new Rectangle(rc.left, rc.top, rc.right, rc.bottom);
            }
            return window.Position;
        }

        private static bool TryGetNcHitTest(IntPtr hwnd, Point cursor, out int hitTest)
        {
            hitTest = 0;
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                nint lParam = ((nint)(short)cursor.Y << 16) | (ushort)(short)cursor.X;
                if (SendMessageTimeout(
                    hwnd,
                    WM_NCHITTEST_MSG,
                    IntPtr.Zero,
                    lParam,
                    SMTO_NORMAL_FLAG | SMTO_ABORTIFHUNG_FLAG,
                    20,
                    out nint result) != IntPtr.Zero)
                {
                    hitTest = unchecked((int)result);
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private int GetResizeJitterTolerance()
        {
            double scale = m_display.Scaling > 0 ? m_display.Scaling : 1.0;
            return Math.Max(6, (int)Math.Round(6 * scale));
        }

        private void ScheduleIgnoreRepositionRemoval(IWindow window, int delayMs = 45)
        {
            _ = m_dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(delayMs);
                if (!Equals(m_movingWindow, window))
                {
                    using (m_ignoreRepositionSetLock.EnterScope())
                    {
                        m_ignoreRepositionSet.Remove(window);
                    }
                }
            });
        }

        private void StartExternalDragWatchdog()
        {
            if (m_dispatcher.CheckAccess())
            {
                if (m_isExternalDrag) m_externalDragWatchdogTimer.Start();
            }
            else
            {
                _ = m_dispatcher.InvokeAsync(() =>
                {
                    if (m_isExternalDrag) m_externalDragWatchdogTimer.Start();
                });
            }
        }

        private void StopExternalDragWatchdog()
        {
            if (m_dispatcher.CheckAccess())
            {
                m_externalDragWatchdogTimer.Stop();
            }
            else
            {
                _ = m_dispatcher.InvokeAsync(() => m_externalDragWatchdogTimer.Stop());
            }
        }

        private static Rectangle GetAccurateFrameMargins(IWindow window)
        {
            IntPtr hwnd = window.Handle;
            if (hwnd != IntPtr.Zero && !IsZoomed(hwnd) && !IsIconic(hwnd))
            {
                try
                {
                    if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT visRc, Marshal.SizeOf<RECT>()) == 0 &&
                        PInvoke.GetWindowRect(new(hwnd), out RECT rawRc) &&
                        visRc.right > visRc.left && visRc.bottom > visRc.top &&
                        rawRc.right > rawRc.left && rawRc.bottom > rawRc.top)
                    {
                        int left = Math.Max(0, visRc.left - rawRc.left);
                        int top = Math.Max(0, visRc.top - rawRc.top);
                        int right = Math.Max(0, rawRc.right - visRc.right);
                        int bottom = Math.Max(0, rawRc.bottom - visRc.bottom);

                        // Guard against abnormal transient values (e.g. offscreen cloaked windows)
                        if (left <= 64 && top <= 64 && right <= 64 && bottom <= 64)
                        {
                            return new Rectangle(left, top, right, bottom);
                        }
                    }
                }
                catch
                {
                }
            }

            var fallback = window.FrameMargins;
            return new Rectangle(
                Math.Max(0, fallback.Left),
                Math.Max(0, fallback.Top),
                Math.Max(0, fallback.Right),
                Math.Max(0, fallback.Bottom));
        }

        private static bool IsStrictlyInsideBounds(Point pt, Rectangle bounds) =>
            pt.X >= bounds.Left && pt.X < bounds.Right && pt.Y >= bounds.Top && pt.Y < bounds.Bottom;

        private static long SquaredDistanceToBounds(Point pt, Rectangle bounds)
        {
            long dx = 0;
            if (pt.X < bounds.Left) dx = bounds.Left - pt.X;
            else if (pt.X >= bounds.Right) dx = pt.X - (bounds.Right - 1);

            long dy = 0;
            if (pt.Y < bounds.Top) dy = bounds.Top - pt.Y;
            else if (pt.Y >= bounds.Bottom) dy = pt.Y - (bounds.Bottom - 1);

            return dx * dx + dy * dy;
        }

        private bool IsPointOwnedByThisDisplay(Point pt, out Point clampedPt)
        {
            var bounds = m_display.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                clampedPt = pt;
                return false;
            }

            if (IsStrictlyInsideBounds(pt, bounds))
            {
                clampedPt = pt;
                return true;
            }

            try
            {
                var displays = m_workspace.DisplayManager.Displays;
                foreach (var d in displays)
                {
                    if (!d.Equals(m_display) && IsStrictlyInsideBounds(pt, d.Bounds))
                    {
                        clampedPt = pt;
                        return false;
                    }
                }

                long myDist = SquaredDistanceToBounds(pt, bounds);
                foreach (var d in displays)
                {
                    if (!d.Equals(m_display) && d.Bounds.Width > 0 && d.Bounds.Height > 0)
                    {
                        long otherDist = SquaredDistanceToBounds(pt, d.Bounds);
                        if (otherDist < myDist)
                        {
                            clampedPt = pt;
                            return false;
                        }
                    }
                }
            }
            catch
            {
            }

            clampedPt = new Point(
                Math.Clamp(pt.X, bounds.Left, bounds.Right - 1),
                Math.Clamp(pt.Y, bounds.Top, bounds.Bottom - 1));
            return true;
        }

        private static void RecordFloatingMemory(IWindow window, Rectangle pos)
        {
            try
            {
                IntPtr hwnd = window.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    if (s_hwndFloatingMemory.TryGetValue(hwnd, out var existing) && existing == pos)
                    {
                        return;
                    }
                    SetProp(hwnd, "SS_FloatW", (IntPtr)pos.Width);
                    SetProp(hwnd, "SS_FloatH", (IntPtr)pos.Height);
                    SetProp(hwnd, "SS_FloatX", (IntPtr)pos.Left);
                    SetProp(hwnd, "SS_FloatY", (IntPtr)pos.Top);
                    s_hwndFloatingMemory[hwnd] = pos;
                }

                string procName = "";
                try { procName = window.GetCachedProcessName()?.ToLowerInvariant() ?? ""; } catch { }
                if (!string.IsNullOrEmpty(procName))
                {
                    s_processFloatingMemory[procName] = (pos.Width, pos.Height);
                }
            }
            catch
            {
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        private static Rectangle? GetWindowRestoredRectangle(IWindow window)
        {
            try
            {
                WINDOWPLACEMENT wp = new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (GetWindowPlacement(window.Handle, ref wp))
                {
                    var rc = wp.rcNormalPosition;
                    int width = rc.right - rc.left;
                    int height = rc.bottom - rc.top;
                    if (width > 0 && height > 0)
                    {
                        return new Rectangle(rc.left, rc.top, rc.right, rc.bottom);
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool IsValidFloatingDimensions(int w, int h, Rectangle wa)
        {
            if (w < 120 || h < 100) return false;
            // 完整支持用户自定义的任意大小（包括铺满全屏 100% 宽度与高度的自定义窗口尺寸），不作人为排除
            return true;
        }

        private static bool IsValidFloatingRect(Rectangle rect, Rectangle wa)
        {
            if (!IsValidFloatingDimensions(rect.Width, rect.Height, wa)) return false;
            if (IsWindowSnappedGeometry(rect, wa)) return false;
            return true;
        }

        private Rectangle GetBestFloatingRectangle(IWindow window, Rectangle wa, Rectangle fallbackPos)
        {
            // 1. Check Win32 HWND Property (SetProp / GetProp) - 硬件级窗口属性持久化
            try
            {
                IntPtr hwnd = window.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int propW = (int)(long)GetProp(hwnd, "SS_FloatW");
                    int propH = (int)(long)GetProp(hwnd, "SS_FloatH");
                    if (IsValidFloatingDimensions(propW, propH, wa))
                    {
                        int propX = (int)(long)GetProp(hwnd, "SS_FloatX");
                        int propY = (int)(long)GetProp(hwnd, "SS_FloatY");
                        if (propX < wa.Left || propX + propW > wa.Right) propX = wa.Left + (wa.Width - propW) / 2;
                        if (propY < wa.Top || propY + propH > wa.Bottom) propY = wa.Top + (wa.Height - propH) / 2;
                        var propRect = Rectangle.OffsetAndSize(propX, propY, propW, propH);
                        if (!IsWindowSnappedGeometry(propRect, wa))
                        {
                            return propRect;
                        }
                    }
                }
            }
            catch { }

            // 2. Check Raw HWND In-Memory Cache
            if (s_hwndFloatingMemory.TryGetValue(window.Handle, out var hwndRect) &&
                IsValidFloatingRect(hwndRect, wa))
            {
                return hwndRect;
            }

            // 3. Check existing pre-snap memory
            using (m_preSnapRectanglesLock.EnterScope())
            {
                if (m_preSnapRectangles.TryGetValue(window, out var preRect) &&
                    IsValidFloatingRect(preRect, wa))
                {
                    return preRect;
                }
            }

            // 4. Check active drag original rect (captured before current move)
            using (m_preSnapRectanglesLock.EnterScope())
            {
                if (m_activeDragOriginalRect.TryGetValue(window, out var origRect) &&
                    IsValidFloatingRect(origRect, wa))
                {
                    return origRect;
                }
            }

            // 5. Check continuous floating memory
            using (m_lastFloatingRectanglesLock.EnterScope())
            {
                if (m_lastFloatingRectangles.TryGetValue(window, out var lastFloat) &&
                    IsValidFloatingRect(lastFloat, wa))
                {
                    return lastFloat;
                }
            }

            // 6. Check Process-level persistent memory (e.g. "notepad" -> last floating size)
            try
            {
                string procName = "";
                try { procName = window.GetCachedProcessName()?.ToLowerInvariant() ?? ""; } catch { }
                if (!string.IsNullOrEmpty(procName) && s_processFloatingMemory.TryGetValue(procName, out var procSize))
                {
                    if (IsValidFloatingDimensions(procSize.Width, procSize.Height, wa))
                    {
                        int targetX = wa.Left + (wa.Width - procSize.Width) / 2;
                        int targetY = wa.Top + (wa.Height - procSize.Height) / 2;
                        return Rectangle.OffsetAndSize(targetX, targetY, procSize.Width, procSize.Height);
                    }
                }
            }
            catch { }

            // 7. Check Windows OS placement rcNormalPosition
            var wpRect = GetWindowRestoredRectangle(window);
            if (wpRect.HasValue && IsValidFloatingRect(wpRect.Value, wa))
            {
                return wpRect.Value;
            }

            // 8. Fallback: intelligent default size (golden ratio)
            bool isPortrait = fallbackPos.Width > 0 && fallbackPos.Height > 0 && fallbackPos.Width < fallbackPos.Height;
            int defaultW = isPortrait ? (int)(wa.Height * 0.45) : (int)(wa.Width * 0.60);
            int defaultH = isPortrait ? (int)(wa.Height * 0.75) : (int)(wa.Height * 0.65);
            return Rectangle.OffsetAndSize(
                wa.Left + (wa.Width - defaultW) / 2,
                wa.Top + (wa.Height - defaultH) / 2,
                defaultW, defaultH);
        }

        private static Rectangle GetVisibleWindowRectangle(IWindow window)
        {
            try
            {
                if (DwmGetWindowAttribute(window.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frameRc, Marshal.SizeOf<RECT>()) == 0)
                {
                    if (frameRc.right > frameRc.left && frameRc.bottom > frameRc.top)
                    {
                        return new Rectangle(frameRc.left, frameRc.top, frameRc.right, frameRc.bottom);
                    }
                }
            }
            catch
            {
            }

            if (PInvoke.GetWindowRect(new(window.Handle), out RECT win32Rc))
            {
                var frame = window.FrameMargins;
                return new Rectangle(
                    win32Rc.left + frame.Left,
                    win32Rc.top + frame.Top,
                    win32Rc.right - frame.Right,
                    win32Rc.bottom - frame.Bottom);
            }

            return window.Position;
        }

        private ScreenSnapZone GetSnapZone(Point pt, Rectangle wa)
        {
            double scale = m_display.Scaling > 0 ? m_display.Scaling : 1.0;
            // 贴靠最边缘触发阈值：保留 3dp 稳健物理边框容差，避免 100% DPI 或多屏临界下偶发 1px 漏判定
            int edgeMargin = Math.Max(3, (int)Math.Round(3.0 * scale));
            // 顶角微区：在必须先贴靠外边缘的基础上给予 8dp 角落触发范围，兼顾手感与自由区纯净
            int cornerExtent = Math.Max(8, (int)Math.Round(8.0 * scale));

            bool nearLeft = pt.X <= wa.Left + edgeMargin;
            bool nearRight = pt.X >= wa.Right - edgeMargin;
            bool nearTop = pt.Y <= wa.Top + edgeMargin;
            bool nearBottom = pt.Y >= wa.Bottom - edgeMargin;

            // 1. Four Corners (1/4 Layout) - 严格最顶点极限微区（必须贴靠边缘且处于最顶点 1~2px 范围内）
            // Top-Left Quarter
            if ((nearTop && nearLeft) ||
                (nearTop && pt.X <= wa.Left + cornerExtent) ||
                (nearLeft && pt.Y <= wa.Top + cornerExtent))
            {
                return ScreenSnapZone.TopLeftQuarter;
            }

            // Top-Right Quarter
            if ((nearTop && nearRight) ||
                (nearTop && pt.X >= wa.Right - cornerExtent) ||
                (nearRight && pt.Y <= wa.Top + cornerExtent))
            {
                return ScreenSnapZone.TopRightQuarter;
            }

            // Bottom-Left Quarter
            if ((nearBottom && nearLeft) ||
                (nearBottom && pt.X <= wa.Left + cornerExtent) ||
                (nearLeft && pt.Y >= wa.Bottom - cornerExtent))
            {
                return ScreenSnapZone.BottomLeftQuarter;
            }

            // Bottom-Right Quarter
            if ((nearBottom && nearRight) ||
                (nearBottom && pt.X >= wa.Right - cornerExtent) ||
                (nearRight && pt.Y >= wa.Bottom - cornerExtent))
            {
                return ScreenSnapZone.BottomRightQuarter;
            }

            // 2. Four Edges (1/2 Layout) - 仅在超出最顶点微区后的最外层 1px 物理边缘触发半屏
            if (nearLeft)
            {
                return ScreenSnapZone.LeftHalf;
            }
            if (nearRight)
            {
                return ScreenSnapZone.RightHalf;
            }
            if (nearTop)
            {
                return ScreenSnapZone.TopHalf;
            }
            if (nearBottom)
            {
                return ScreenSnapZone.BottomHalf;
            }

            return ScreenSnapZone.None;
        }

        private Rectangle? GetSnapRectangle(ScreenSnapZone zone, Rectangle wa)
        {
            int halfW = wa.Width / 2;
            int halfH = wa.Height / 2;
            int rightW = wa.Width - halfW;
            int bottomH = wa.Height - halfH;

            return zone switch
            {
                ScreenSnapZone.LeftHalf =>
                    Rectangle.OffsetAndSize(wa.Left, wa.Top, halfW, wa.Height),
                ScreenSnapZone.RightHalf =>
                    Rectangle.OffsetAndSize(wa.Left + halfW, wa.Top, rightW, wa.Height),
                ScreenSnapZone.TopHalf =>
                    Rectangle.OffsetAndSize(wa.Left, wa.Top, wa.Width, halfH),
                ScreenSnapZone.BottomHalf =>
                    Rectangle.OffsetAndSize(wa.Left, wa.Top + halfH, wa.Width, bottomH),
                ScreenSnapZone.TopLeftQuarter =>
                    Rectangle.OffsetAndSize(wa.Left, wa.Top, halfW, halfH),
                ScreenSnapZone.TopRightQuarter =>
                    Rectangle.OffsetAndSize(wa.Left + halfW, wa.Top, rightW, halfH),
                ScreenSnapZone.BottomLeftQuarter =>
                    Rectangle.OffsetAndSize(wa.Left, wa.Top + halfH, halfW, bottomH),
                ScreenSnapZone.BottomRightQuarter =>
                    Rectangle.OffsetAndSize(wa.Left + halfW, wa.Top + halfH, rightW, bottomH),
                _ => null,
            };
        }

        private void ApplyWindowGeometryCore(IWindow window, Rectangle targetRect, bool isResponsive)
        {
            IntPtr hwnd = window.Handle;
            if (hwnd == IntPtr.Zero || !window.IsAlive)
                return;

            if (IsZoomed(hwnd) || window.State != WindowState.Restored)
            {
                window.SetState(WindowState.Restored);
                if (isResponsive)
                {
                    isResponsive = WaitForMoveSizeExit(hwnd, 30);
                }
            }

            var frame = GetAccurateFrameMargins(window);
            var adjustedRect = new Rectangle(
                left: targetRect.Left - frame.Left,
                top: targetRect.Top - frame.Top,
                right: targetRect.Right + frame.Right,
                bottom: targetRect.Bottom + frame.Bottom);

            uint swpFlags = SWP_NOZORDER_FLAG | SWP_NOACTIVATE_FLAG | SWP_NOCOPYBITS_FLAG | SWP_FRAMECHANGED_FLAG;
            if (!window.CanResize)
            {
                swpFlags |= SWP_NOSIZE_FLAG;
            }
            if (!window.CanMove)
            {
                swpFlags |= SWP_NOMOVE_FLAG;
            }
            if (!isResponsive)
            {
                swpFlags |= SWP_ASYNCWINDOWPOS_FLAG;
            }

            SetWindowPosNative(
                hwnd,
                IntPtr.Zero,
                adjustedRect.Left,
                adjustedRect.Top,
                adjustedRect.Width,
                adjustedRect.Height,
                swpFlags);

            RedrawWindow(
                hwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                RDW_INVALIDATE_FLAG | RDW_ERASE_FLAG | RDW_FRAME_FLAG | RDW_ALLCHILDREN_FLAG);
        }

        private void ApplyWindowGeometryAsyncSafe(IWindow window, Rectangle targetRect)
        {
            try
            {
                IntPtr hwnd = window.Handle;
                CancelPendingSnapSettle(hwnd);
                ApplyWindowGeometryCore(window, targetRect, isResponsive: false);
            }
            catch (Exception ex)
            {
                m_logger.Warning(ex, "Failed to apply async window geometry for {Window}", window.DebugString());
            }
        }

        private void ApplyWindowSnap(IWindow window, Rectangle targetRect, bool isUnsnapRestore = false)
        {
            try
            {
                IntPtr hwnd = window.Handle;
                CancelPendingSnapSettle(hwnd);

                // 确保目标窗口已完全退出 Win32 模态拖拽循环 (WM_EXITSIZEMOVE)，
                // 避免在 in_size_move 尚未复位时下发缩放导致内容区偏移而右上角关闭按钮停留在外框原位。
                bool isResponsive = WaitForMoveSizeExit(hwnd, 40);
                ApplyWindowGeometryCore(window, targetRect, isResponsive);

                var cts = new CancellationTokenSource();
                s_snapSettleTokens[hwnd] = cts;
                var token = cts.Token;

                // 二次帧同步与防回弹校准：若用户在期间开始了新拖动则立即取消；
                // 对从分屏拖出还原的窗口强制触发 SWP_FRAMECHANGED + 子窗口重绘，彻底消除顶部/左侧空白偏移。
                m_dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(35, token);
                        if (token.IsCancellationRequested || !window.IsAlive)
                            return;

                        if (m_movingWindow != null ||
                            (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0 ||
                            (PInvoke.GetAsyncKeyState(2) & 0x8000) != 0)
                        {
                            return;
                        }

                        var currentVis = GetVisibleWindowRectangle(window);
                        bool geometryDrifted = Math.Abs(currentVis.Left - targetRect.Left) > 1 ||
                                               Math.Abs(currentVis.Top - targetRect.Top) > 1 ||
                                               Math.Abs(currentVis.Width - targetRect.Width) > 1 ||
                                               Math.Abs(currentVis.Height - targetRect.Height) > 1;

                        bool responsiveNow = WaitForMoveSizeExit(hwnd, 25);
                        if (geometryDrifted)
                        {
                            ApplyWindowGeometryCore(window, targetRect, responsiveNow);
                        }
                        else if (isUnsnapRestore)
                        {
                            uint syncFlags = SWP_NOMOVE_FLAG | SWP_NOSIZE_FLAG | SWP_NOZORDER_FLAG |
                                             SWP_NOACTIVATE_FLAG | SWP_FRAMECHANGED_FLAG | SWP_NOCOPYBITS_FLAG;
                            if (!responsiveNow)
                            {
                                syncFlags |= SWP_ASYNCWINDOWPOS_FLAG;
                            }
                            SetWindowPosNative(hwnd, IntPtr.Zero, 0, 0, 0, 0, syncFlags);
                            RedrawWindow(
                                hwnd,
                                IntPtr.Zero,
                                IntPtr.Zero,
                                RDW_INVALIDATE_FLAG | RDW_ERASE_FLAG | RDW_FRAME_FLAG | RDW_ALLCHILDREN_FLAG);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        if (s_snapSettleTokens.TryGetValue(hwnd, out var current) && ReferenceEquals(current, cts))
                        {
                            s_snapSettleTokens.TryRemove(hwnd, out _);
                        }
                        try { cts.Dispose(); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                m_logger.Warning(ex, "Failed to apply window snap for {Window}", window.DebugString());
            }
        }

        private bool TryGetRecentLeftDown(out Point point, out bool wasResizeCursor)
        {
            if (m_lastLeftDownTime != TimeSpan.Zero &&
                (m_sw.Elapsed - m_lastLeftDownTime) <= TimeSpan.FromMilliseconds(500))
            {
                point = m_lastLeftDownPoint;
                wasResizeCursor = m_lastLeftDownIsResizeCursor;
                return true;
            }
            point = default;
            wasResizeCursor = false;
            return false;
        }

        private static bool IsResizeHitTest(int hitTest) =>
            hitTest >= HT_LEFT && hitTest <= HT_BOTTOMRIGHT;

        private static bool IsCaptionHitTest(int hitTest) =>
            hitTest == HT_CAPTION || hitTest == HT_SYSMENU ||
            (hitTest >= HT_MINBUTTON && hitTest <= HT_MAXBUTTON) || hitTest == HT_CLOSE;

        private static bool IsPointOnGeometricBorder(
            Rectangle visiblePos,
            Rectangle rawPos,
            WinMan.Point pt,
            double scale,
            bool strictTopForClient)
        {
            int outerMargin = Math.Max(8, (int)Math.Round(9 * scale));
            int innerSideMargin = Math.Max(4, (int)Math.Round(5 * scale));
            int innerBottomMargin = Math.Max(5, (int)Math.Round(6 * scale));
            int topOuterMargin = strictTopForClient
                ? Math.Max(4, (int)Math.Round(5 * scale))
                : Math.Max(4, (int)Math.Round(5 * scale));
            int topInnerMargin = strictTopForClient
                ? Math.Max(1, (int)Math.Round(2 * scale))
                : Math.Max(3, (int)Math.Round(4 * scale));

            bool isTopBorder = (pt.Y >= visiblePos.Top - topOuterMargin && pt.Y <= visiblePos.Top + topInnerMargin &&
                                pt.X >= visiblePos.Left - outerMargin && pt.X <= visiblePos.Right + outerMargin) ||
                               (pt.Y >= rawPos.Top - topOuterMargin && pt.Y <= rawPos.Top + topInnerMargin &&
                                pt.X >= rawPos.Left - outerMargin && pt.X <= rawPos.Right + outerMargin);
            if (isTopBorder)
                return true;

            bool isBottomBorder = (pt.Y >= visiblePos.Bottom - innerBottomMargin && pt.Y <= visiblePos.Bottom + outerMargin &&
                                   pt.X >= visiblePos.Left - outerMargin && pt.X <= visiblePos.Right + outerMargin) ||
                                  (pt.Y >= rawPos.Bottom - innerBottomMargin && pt.Y <= rawPos.Bottom + outerMargin &&
                                   pt.X >= rawPos.Left - outerMargin && pt.X <= rawPos.Right + outerMargin);
            if (isBottomBorder)
                return true;

            bool isLeftBorder = (pt.X >= visiblePos.Left - outerMargin && pt.X <= visiblePos.Left + innerSideMargin &&
                                 pt.Y >= visiblePos.Top - outerMargin && pt.Y <= visiblePos.Bottom + outerMargin) ||
                                (pt.X >= rawPos.Left - innerSideMargin && pt.X <= rawPos.Left + innerSideMargin &&
                                 pt.Y >= rawPos.Top - outerMargin && pt.Y <= rawPos.Bottom + outerMargin);
            if (isLeftBorder)
                return true;

            bool isRightBorder = (pt.X >= visiblePos.Right - innerSideMargin && pt.X <= visiblePos.Right + outerMargin &&
                                  pt.Y >= visiblePos.Top - outerMargin && pt.Y <= visiblePos.Bottom + outerMargin) ||
                                 (pt.X >= rawPos.Right - innerSideMargin && pt.X <= rawPos.Right + innerSideMargin &&
                                  pt.Y >= rawPos.Top - outerMargin && pt.Y <= rawPos.Bottom + outerMargin);
            return isRightBorder;
        }

        private static bool IsResizeBorderClick(
            IntPtr hwnd,
            Rectangle visiblePos,
            Rectangle rawPos,
            WinMan.Point cursor,
            Point? mouseDownPoint,
            bool mouseDownWasResizeCursor,
            double scale,
            out bool isConfirmedResize)
        {
            bool hasDownHitTest = false;
            int downHitTest = 0;
            if (mouseDownPoint.HasValue)
            {
                hasDownHitTest = TryGetNcHitTest(hwnd, mouseDownPoint.Value, out downHitTest);
                if (hasDownHitTest)
                {
                    if (IsResizeHitTest(downHitTest))
                    {
                        isConfirmedResize = true;
                        return true;
                    }
                    if (IsCaptionHitTest(downHitTest))
                    {
                        isConfirmedResize = false;
                        return false;
                    }
                }
            }

            bool hasCursorHitTest = TryGetNcHitTest(hwnd, cursor, out int cursorHitTest);
            if (hasCursorHitTest)
            {
                if (IsResizeHitTest(cursorHitTest))
                {
                    isConfirmedResize = true;
                    return true;
                }
                if (!hasDownHitTest && IsCaptionHitTest(cursorHitTest))
                {
                    isConfirmedResize = false;
                    return false;
                }
            }

            if (mouseDownWasResizeCursor || IsResizeCursorActive())
            {
                isConfirmedResize = true;
                return true;
            }

            if (visiblePos.Width <= 0 || visiblePos.Height <= 0)
            {
                if (rawPos.Width <= 0 || rawPos.Height <= 0)
                {
                    isConfirmedResize = false;
                    return false;
                }
                visiblePos = rawPos;
            }
            if (rawPos.Width <= 0 || rawPos.Height <= 0)
            {
                rawPos = visiblePos;
            }

            int effectiveHitTest = hasDownHitTest ? downHitTest : cursorHitTest;
            bool hasEffectiveHitTest = hasDownHitTest || hasCursorHitTest;
            bool strictTopForClient = hasEffectiveHitTest && effectiveHitTest == HT_CLIENT;

            isConfirmedResize = false;
            int outerMargin = Math.Max(8, (int)Math.Round(9 * scale));
            if (mouseDownPoint.HasValue)
            {
                var mp = mouseDownPoint.Value;
                bool nearRawBox = mp.X >= rawPos.Left - outerMargin && mp.X <= rawPos.Right + outerMargin &&
                                  mp.Y >= rawPos.Top - outerMargin && mp.Y <= rawPos.Bottom + outerMargin;
                if (nearRawBox && IsPointOnGeometricBorder(visiblePos, rawPos, mp, scale, strictTopForClient))
                {
                    return true;
                }
            }
            return IsPointOnGeometricBorder(visiblePos, rawPos, cursor, scale, strictTopForClient);
        }

        private bool TryRebaseForWindowDpiChange(IWindow window, Rectangle curRawPos)
        {
            if (m_activeDragWindowDpi == 0 || curRawPos.Width <= 0 || curRawPos.Height <= 0)
            {
                return false;
            }

            uint newDpi = GetSafeWindowDpi(window.Handle);
            if (newDpi != 0 && newDpi != m_activeDragWindowDpi)
            {
                m_logger.Information(
                    "[Drag] Window {Window} DPI changed ({OldDpi} -> {NewDpi}), rebasing drag start rect to {Rect}",
                    window.DebugString(), m_activeDragWindowDpi, newDpi, curRawPos);
                m_activeDragWindowDpi = newDpi;
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_activeDragInitialRawRect[window] = curRawPos;
                }
                return true;
            }

            return false;
        }

        private static bool HasResizeDeformation(Rectangle rawStartRect, Rectangle curRawRect, int sizeTol)
        {
            if (rawStartRect.Width <= 0 || rawStartRect.Height <= 0 ||
                curRawRect.Width <= 0 || curRawRect.Height <= 0)
            {
                return false;
            }

            int dW = Math.Abs(curRawRect.Width - rawStartRect.Width);
            int dH = Math.Abs(curRawRect.Height - rawStartRect.Height);
            if (dW > sizeTol || dH > sizeTol)
            {
                return true;
            }

            // 单边锚定形变快检：即使在松手/拉伸初期变化量仅 >= 2px（尚未达到 DPI 抖动容差），
            // 只要有一侧边缘保持不动而对侧边缘伸长或缩短，即可 100% 确认为边框拉伸而非标题栏移动！
            bool horizAnchoredResize = dW >= 2 &&
                (Math.Abs(curRawRect.Left - rawStartRect.Left) <= 1 ||
                 Math.Abs(curRawRect.Right - rawStartRect.Right) <= 1);
            bool vertAnchoredResize = dH >= 2 &&
                (Math.Abs(curRawRect.Top - rawStartRect.Top) <= 1 ||
                 Math.Abs(curRawRect.Bottom - rawStartRect.Bottom) <= 1);
            return horizAnchoredResize || vertAnchoredResize;
        }

        private static bool IsSingleEdgeAnchoredTransition(Rectangle oldRect, Rectangle newRect)
        {
            int dW = Math.Abs(newRect.Width - oldRect.Width);
            int dH = Math.Abs(newRect.Height - oldRect.Height);
            bool horiz = dW >= 2 &&
                (Math.Abs(newRect.Left - oldRect.Left) <= 1 ||
                 Math.Abs(newRect.Right - oldRect.Right) <= 1);
            bool vert = dH >= 2 &&
                (Math.Abs(newRect.Top - oldRect.Top) <= 1 ||
                 Math.Abs(newRect.Bottom - oldRect.Bottom) <= 1);
            return horiz || vert;
        }

        private bool IsExpectedUnsnapRestoreSize(IWindow window, Rectangle curRawRect, int sizeTol)
        {
            if (!m_isExternalDrag || !m_isDraggingSnappedWindow ||
                m_currentDragTargetSize.X <= 0 || m_currentDragTargetSize.Y <= 0)
            {
                return false;
            }

            var vis = GetVisibleWindowRectangle(window);
            if (vis.Width > 0 && vis.Height > 0 &&
                Math.Abs(vis.Width - m_currentDragTargetSize.X) <= sizeTol &&
                Math.Abs(vis.Height - m_currentDragTargetSize.Y) <= sizeTol)
            {
                return true;
            }

            return curRawRect.Width > 0 && curRawRect.Height > 0 &&
                Math.Abs(curRawRect.Width - m_currentDragTargetSize.X) <= sizeTol + 24 &&
                Math.Abs(curRawRect.Height - m_currentDragTargetSize.Y) <= sizeTol + 24;
        }

        private void SwitchActiveDragToResizing(IWindow? window, string reason)
        {
            m_currentInteraction = UserInteraction.Resizing;
            m_isConfirmedResize = true;
            m_isDraggingTitleBar = false;
            m_isDraggingSnappedWindow = false;
            m_isExternalDrag = false;
            m_potentialExternalDragWindow = null;
            m_potentialExternalDragInitialPos = default;
            m_externalDragFrameCount = 0;
            m_lastActiveSnapZone = ScreenSnapZone.None;
            m_lastActiveSnapTime = TimeSpan.Zero;
            StopFallbackDragProbe();
            StopExternalDragWatchdog();
            StopDragTrackingTimer();
            m_lastPreviewCursor = new Point(-99999, -99999);
            SetPreviewRectangle(null, null);

            if (window != null)
            {
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_preSnapRectangles.Remove(window);
                    m_activeDragOriginalRect.Remove(window);
                }
                using (m_snappedWindowsLock.EnterScope())
                {
                    m_snappedWindows.Remove(window);
                }
                s_snappedHwnds.TryRemove(window.Handle, out _);
                s_hwndPreSnapMemory.TryRemove(window.Handle, out _);
                m_logger.Information("[Drag] Window {Window} switched to RESIZING ({Reason}), split screen disabled",
                    window.DebugString(), reason);
            }
        }

        private static bool IsHalfZone(ScreenSnapZone zone) =>
            zone is ScreenSnapZone.LeftHalf or ScreenSnapZone.RightHalf or ScreenSnapZone.TopHalf or ScreenSnapZone.BottomHalf;

        private static bool IsQuarterZone(ScreenSnapZone zone) =>
            zone is ScreenSnapZone.TopLeftQuarter or ScreenSnapZone.TopRightQuarter or ScreenSnapZone.BottomLeftQuarter or ScreenSnapZone.BottomRightQuarter;

        private ScreenSnapZone m_lastActiveSnapZone = ScreenSnapZone.None;
        private TimeSpan m_lastActiveSnapTime = TimeSpan.Zero;

        private (Rectangle? Rect, string? Title) GetPreviewInfo() => GetPreviewInfo(GetLiveCursorLocation());

        private (Rectangle? Rect, string? Title) GetPreviewInfo(Point pt)
        {
            // 绝对核心前提：必须正在物理按住鼠标按键！若未按住鼠标按键，绝对不属于拖动窗口，绝不显示分屏阴影！
            bool isMouseButtonDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0 || (PInvoke.GetAsyncKeyState(2) & 0x8000) != 0;
            if (!isMouseButtonDown)
            {
                // 松开鼠标按键时仅隐藏半透明阴影框，绝不提前销毁 m_movingWindow 或抹除 m_lastActiveSnapZone！
                // 拖动生命周期的最终完成与上下文释放，严格由 OnWindowPositionChangeEnd (原生拖拽) 或 EndExternalDrag (外部拖拽) 官方负责！
                SetPreviewRectangle(null, null);
                return (null, null);
            }

            // 严格切割：窗口放大/缩小（Resizing）时绝对不触发分屏预览
            if (m_currentInteraction == UserInteraction.Resizing)
            {
                m_lastActiveSnapZone = ScreenSnapZone.None;
                return (null, null);
            }

            // 必须正在进行窗口移动，且当前有被移动窗口；或者正在拖动 panel
            if ((m_currentInteraction == UserInteraction.Moving && m_movingWindow != null) || m_movingPanelNode != null)
            {
                try
                {
                    if (!IsPointOwnedByThisDisplay(pt, out var testPt))
                    {
                        m_lastActiveSnapZone = ScreenSnapZone.None;
                        return (null, null);
                    }

                    {
                        var wa = m_display.WorkArea;
                        if (m_movingPanelNode == null)
                        {
                            var snapZone = GetSnapZone(testPt, wa);
                            if (snapZone != ScreenSnapZone.None)
                            {
                                m_lastActiveSnapZone = snapZone;
                                m_lastActiveSnapTime = m_sw.Elapsed;

                                string snapTitle = snapZone switch
                                {
                                    ScreenSnapZone.LeftHalf => "停靠左半屏",
                                    ScreenSnapZone.RightHalf => "停靠右半屏",
                                    ScreenSnapZone.TopHalf => "停靠上半屏",
                                    ScreenSnapZone.BottomHalf => "停靠下半屏",
                                    ScreenSnapZone.TopLeftQuarter => "停靠左上角",
                                    ScreenSnapZone.TopRightQuarter => "停靠右上角",
                                    ScreenSnapZone.BottomLeftQuarter => "停靠左下角",
                                    ScreenSnapZone.BottomRightQuarter => "停靠右下角",
                                    _ => "边缘停靠"
                                };
                                return (GetSnapRectangle(snapZone, wa), snapTitle);
                            }

                            // 自由区：完全不显示任何阴影框，保证自由拖动纯净无干扰
                            // 保留 400ms 记忆缓冲以抵御物理松手微颤，超时后才清空记忆
                            if (m_sw.Elapsed - m_lastActiveSnapTime > TimeSpan.FromMilliseconds(400))
                            {
                                m_lastActiveSnapZone = ScreenSnapZone.None;
                            }
                            return (null, null);
                        }

                        if (m_movingPanelNode != null)
                        {
                            int defaultW = (int)(wa.Width * 0.60);
                            int defaultH = (int)(wa.Height * 0.65);
                            int targetX = Math.Clamp(pt.X - defaultW / 2, wa.Left, wa.Right - defaultW);
                            int targetY = Math.Clamp(pt.Y - 20, wa.Top, wa.Bottom - defaultH);
                            return (Rectangle.OffsetAndSize(targetX, targetY, defaultW, defaultH), null);
                        }

                        return (null, null);
                    }
                }
                catch
                {
                    return (null, null);
                }
            }
            return (null, null);
        }

        private void UpdateDragPreview(Point pt)
        {
            var preview = GetPreviewInfo(pt);
            bool changed = m_lastPreviewRect != preview.Rect || !string.Equals(m_lastPreviewTitle, preview.Title, StringComparison.Ordinal);
            if (!changed)
            {
                return;
            }
            m_logger.Debug("[Drag] Preview changed: Rect={Rect}, Title={Title}", preview.Rect, preview.Title);
            SetPreviewRectangle(preview.Rect, preview.Title);
            if (preview.Rect.HasValue)
            {
                m_gui.EnsureTopmost();
            }
        }

        private Point m_lastPreviewCursor = new Point(-99999, -99999);

        private TimeSpan m_nativeDragButtonUpTime = TimeSpan.Zero;

        private void OnDragTrackingTimerTick(object? sender, EventArgs e)
        {
            if (!m_active || m_movingWindow == null)
            {
                StopDragTrackingTimer();
                m_nativeDragButtonUpTime = TimeSpan.Zero;
                if (m_movingPanelNode == null)
                {
                    m_currentInteraction = UserInteraction.None;
                    m_isExternalDrag = false;
                }
                return;
            }

            if (m_currentInteraction == UserInteraction.Resizing)
            {
                StopDragTrackingTimer();
                SetPreviewRectangle(null, null);
                return;
            }

            // 实时物理尺寸形变 / 跨屏 DPI / 光标缩放态联合防御：
            // 绝不在宽高 0 变化时仅凭瞬态光标切换为 Resizing（防止鼠标掠过其他窗口边框或屏幕边缘误中断拖动）；
            // 若发生跨显示器 DPI 切换则自动重定基准尺寸；否则一旦发生真实尺寸形变（或缩放光标 + >=1px 形变），立即锁定为 Resizing。
            var curRawTimerPos = GetRawWindowRectangle(m_movingWindow);
            if (!TryRebaseForWindowDpiChange(m_movingWindow, curRawTimerPos) && !m_activeDragStartedMaximized)
            {
                Rectangle initialRaw;
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_activeDragInitialRawRect.TryGetValue(m_movingWindow, out initialRaw);
                }
                if (initialRaw.Width > 0 && initialRaw.Height > 0 && curRawTimerPos.Width > 0 && curRawTimerPos.Height > 0)
                {
                    int tol = GetResizeJitterTolerance();
                    bool sizeDelta1Px = Math.Abs(curRawTimerPos.Width - initialRaw.Width) >= 1 ||
                                        Math.Abs(curRawTimerPos.Height - initialRaw.Height) >= 1;
                    bool isResize = !IsExpectedUnsnapRestoreSize(m_movingWindow, curRawTimerPos, tol) &&
                                    (HasResizeDeformation(initialRaw, curRawTimerPos, tol) ||
                                     (IsResizeCursorActive() && sizeDelta1Px));
                    if (isResize)
                    {
                        SwitchActiveDragToResizing(
                            m_movingWindow,
                            $"Timer SizeDeformation dW={Math.Abs(curRawTimerPos.Width - initialRaw.Width)}, dH={Math.Abs(curRawTimerPos.Height - initialRaw.Height)}");
                        return;
                    }
                }
            }

            var pt = GetLiveCursorLocation();

            // 外部拖动时（Quicker / 辅助移动工具），实时检测鼠标左键是否已经松开
            if (m_isExternalDrag && (PInvoke.GetAsyncKeyState(1) & 0x8000) == 0)
            {
                EndExternalDrag();
                return;
            }

            // 原生 Windows 拖动：窗口拖动模态循环由操作系统管理，并通过 OnWindowPositionChangeEnd (EVENT_SYSTEM_MOVESIZEEND) 官方通知结束！
            // 绝不可在鼠标松开瞬间由定时器抢跑提前 DoWindowMove，否则操作系统退出模态循环时会将窗口强行重置回鼠标释放点，导致分屏被覆盖失效！
            // 仅当鼠标按键松开超过 500ms 且操作系统异常丢失 EVENT_SYSTEM_MOVESIZEEND 时，才作为后备看门狗完整回收状态。
            if (!m_isExternalDrag && (PInvoke.GetAsyncKeyState(1) & 0x8000) == 0 && (PInvoke.GetAsyncKeyState(2) & 0x8000) == 0)
            {
                if (m_nativeDragButtonUpTime == TimeSpan.Zero)
                {
                    m_nativeDragButtonUpTime = m_sw.Elapsed;
                }
                else if (m_sw.Elapsed - m_nativeDragButtonUpTime > TimeSpan.FromMilliseconds(500))
                {
                    m_nativeDragButtonUpTime = TimeSpan.Zero;
                    StopDragTrackingTimer();
                    if (m_movingWindow != null)
                    {
                        var win = m_movingWindow;
                        m_movingWindow = null;
                        var interaction = m_currentInteraction;
                        m_currentInteraction = UserInteraction.None;
                        m_isConfirmedResize = false;
                        m_activeDragStartedMaximized = false;
                        m_activeDragWindowDpi = 0;
                        m_isDraggingTitleBar = false;
                        m_isDraggingSnappedWindow = false;
                        m_lastPreviewCursor = new Point(-99999, -99999);
                        SetPreviewRectangle(null, null);
                        try
                        {
                            if (interaction != UserInteraction.Resizing)
                            {
                                DoWindowMove(win);
                            }
                        }
                        catch (Exception ex)
                        {
                            m_logger.Warning(ex, "Error in native drag watchdog fallback for {Window}", win.DebugString());
                        }
                        finally
                        {
                            using (m_preSnapRectanglesLock.EnterScope())
                            {
                                m_activeDragOriginalRect.Remove(win);
                                m_activeDragInitialRawRect.Remove(win);
                            }
                            ScheduleIgnoreRepositionRemoval(win, 45);
                        }
                    }
                    return;
                }
            }
            else
            {
                m_nativeDragButtonUpTime = TimeSpan.Zero;
            }

            if (pt == m_lastPreviewCursor)
            {
                return;
            }
            m_lastPreviewCursor = pt;

            UpdateDragPreview(pt);
        }

        private void ResetFallbackProbeState()
        {
            m_fallbackProbeHwnd = IntPtr.Zero;
            m_fallbackProbeInitialRect = default;
            m_fallbackProbeLastRect = default;
            m_fallbackProbeMoveFrames = 0;
        }

        private void StartFallbackDragProbe()
        {
            void DoStart()
            {
                if (m_active && m_movingWindow == null && !m_isExternalDrag && m_currentInteraction == UserInteraction.None)
                {
                    ResetFallbackProbeState();
                    m_fallbackDragProbeTimer.Start();
                }
            }

            if (m_dispatcher.CheckAccess())
            {
                DoStart();
            }
            else
            {
                _ = m_dispatcher.InvokeAsync(DoStart);
            }
        }

        private void StopFallbackDragProbe()
        {
            void DoStop()
            {
                m_fallbackDragProbeTimer.Stop();
                ResetFallbackProbeState();
            }

            if (m_dispatcher.CheckAccess())
            {
                DoStop();
            }
            else
            {
                _ = m_dispatcher.InvokeAsync(DoStop);
            }
        }

        private static bool IsCandidateFallbackWindow(IntPtr hwnd, Point cursor, out Rectangle rawRect)
        {
            rawRect = default;
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd))
            {
                return false;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId)
            {
                return false;
            }

            var style = (WINDOWS_STYLE)PInvoke.GetWindowLong(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE);
            if (style.HasFlag(WINDOWS_STYLE.WS_CHILD))
            {
                return false;
            }

            var exStyle = (WINDOWS_EX_STYLE)PInvoke.GetWindowLong(new(hwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE);
            if (exStyle.HasFlag(WINDOWS_EX_STYLE.WS_EX_TOOLWINDOW))
            {
                return false;
            }

            var sb = new System.Text.StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                {
                    return false;
                }
            }

            if (!PInvoke.GetWindowRect(new(hwnd), out RECT rc) || rc.right <= rc.left || rc.bottom <= rc.top)
            {
                return false;
            }

            rawRect = new Rectangle(rc.left, rc.top, rc.right, rc.bottom);
            return cursor.X >= rawRect.Left - 120 && cursor.X <= rawRect.Right + 120 &&
                   cursor.Y >= rawRect.Top - 120 && cursor.Y <= rawRect.Bottom + 120;
        }

        private void OnFallbackDragProbeTick(object? sender, EventArgs e)
        {
            bool isLeftDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0;
            if (!m_active || !isLeftDown || m_movingWindow != null || m_isExternalDrag || m_currentInteraction != UserInteraction.None)
            {
                StopFallbackDragProbe();
                return;
            }

            if (IsResizeCursorActive() || (TryGetRecentLeftDown(out var recentDownPt, out bool recentDownWasResize) && recentDownWasResize))
            {
                ResetFallbackProbeState();
                return;
            }
            bool hasDown = TryGetRecentLeftDown(out recentDownPt, out recentDownWasResize);

            var cursor = GetLiveCursorLocation();
            if (!IsPointOwnedByThisDisplay(cursor, out _))
            {
                ResetFallbackProbeState();
                return;
            }

            IntPtr candidateHwnd = GetForegroundWindow();
            if (!IsCandidateFallbackWindow(candidateHwnd, cursor, out var curRect))
            {
                IntPtr ptHwnd = WindowFromPoint(new POINT_NATIVE { X = cursor.X, Y = cursor.Y });
                if (ptHwnd != IntPtr.Zero)
                {
                    IntPtr rootHwnd = GetAncestor(ptHwnd, GA_ROOT_FLAG);
                    if (rootHwnd != IntPtr.Zero)
                    {
                        ptHwnd = rootHwnd;
                    }
                }
                if (!IsCandidateFallbackWindow(ptHwnd, cursor, out curRect))
                {
                    ResetFallbackProbeState();
                    return;
                }
                candidateHwnd = ptHwnd;
            }

            if (candidateHwnd != m_fallbackProbeHwnd)
            {
                m_fallbackProbeHwnd = candidateHwnd;
                m_fallbackProbeInitialRect = curRect;
                m_fallbackProbeLastRect = curRect;
                m_fallbackProbeMoveFrames = 0;
                return;
            }

            if (curRect == m_fallbackProbeLastRect)
            {
                return;
            }

            bool frameSizeOk = Math.Abs(curRect.Width - m_fallbackProbeLastRect.Width) <= 4 &&
                               Math.Abs(curRect.Height - m_fallbackProbeLastRect.Height) <= 4 &&
                               !IsSingleEdgeAnchoredTransition(m_fallbackProbeLastRect, curRect);
            bool totalSizeOk = m_fallbackProbeInitialRect.Width <= 0 ||
                               (Math.Abs(curRect.Width - m_fallbackProbeInitialRect.Width) <= 4 &&
                                Math.Abs(curRect.Height - m_fallbackProbeInitialRect.Height) <= 4 &&
                                !IsSingleEdgeAnchoredTransition(m_fallbackProbeInitialRect, curRect));

            if (!frameSizeOk || !totalSizeOk)
            {
                ResetFallbackProbeState();
                return;
            }

            int dx = curRect.Left - m_fallbackProbeLastRect.Left;
            int dy = curRect.Top - m_fallbackProbeLastRect.Top;
            if (dx == 0 && dy == 0)
            {
                return;
            }

            m_fallbackProbeLastRect = curRect;
            m_fallbackProbeMoveFrames++;
            if (m_fallbackProbeMoveFrames < 2)
            {
                return;
            }

            IWindow? win = null;
            try
            {
                win = m_workspace.FindWindow(candidateHwnd) ?? m_workspace.UnsafeCreateFromHandle(candidateHwnd);
            }
            catch
            {
                win = null;
            }

            if (win == null || !win.CanMove)
            {
                ResetFallbackProbeState();
                return;
            }

            double scale = m_display.Scaling > 0 ? m_display.Scaling : 1.0;
            var initPos = m_fallbackProbeInitialRect;
            if (IsResizeBorderClick(
                candidateHwnd,
                initPos,
                initPos,
                hasDown ? recentDownPt : cursor,
                hasDown ? recentDownPt : null,
                recentDownWasResize,
                scale,
                out _))
            {
                ResetFallbackProbeState();
                return;
            }

            m_logger.Information("[Drag] Fallback probe captured missed drag on {Window}", win.DebugString());
            StopFallbackDragProbe();
            StartExternalDrag(win, curRect, initPos, cursor);
        }

        private void OnExternalDragMouseButtonStateChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
        {
            if (e.Button == LowLevelMouseHook.MouseButton.Left)
            {
                if (e.IsPressed)
                {
                    m_lastLeftDownPoint = new Point(e.X, e.Y);
                    m_lastLeftDownTime = m_sw.Elapsed;
                    m_lastLeftDownIsResizeCursor = IsResizeCursorActive();
                    if (m_active && m_movingWindow == null && !m_isExternalDrag && m_currentInteraction == UserInteraction.None)
                    {
                        StartFallbackDragProbe();
                    }
                }
                else
                {
                    m_lastLeftDownTime = TimeSpan.Zero;
                    m_lastLeftDownIsResizeCursor = false;
                    StopFallbackDragProbe();
                }
            }

            if (m_isExternalDrag && m_movingWindow != null)
            {
                // 用户松开左键时（或者右键取消时）触发放下与贴靠
                if ((e.Button == LowLevelMouseHook.MouseButton.Left && !e.IsPressed) ||
                    (e.Button == LowLevelMouseHook.MouseButton.Right && e.IsPressed))
                {
                    _ = m_dispatcher.InvokeAsync(EndExternalDrag);
                }
            }
        }

        private void OnExternalDragWatchdogTick(object? sender, EventArgs e)
        {
            if (!m_isExternalDrag || m_movingWindow == null)
            {
                m_externalDragWatchdogTimer.Stop();
                return;
            }

            if (m_sw.Elapsed - m_lastExternalDragTime > TimeSpan.FromMilliseconds(1500))
            {
                m_logger.Information("[Drag] External drag timed out after 1500ms of inactivity");
                EndExternalDrag();
            }
        }

        private void StartExternalDrag(IWindow window, Rectangle rawPos, Rectangle rawInitialPos, Point cursor)
        {
            StopFallbackDragProbe();
            CancelPendingSnapSettle(window.Handle);
            m_potentialExternalDragWindow = null;
            m_potentialExternalDragInitialPos = default;
            m_externalDragFrameCount = 0;

            var startRaw = GetRawWindowRectangle(window);
            if (startRaw.Width <= 0 || startRaw.Height <= 0)
            {
                startRaw = rawPos;
            }
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_activeDragInitialRawRect[window] = startRaw;
            }
            m_isConfirmedResize = false;
            m_activeDragStartedMaximized = window.State == WindowState.Maximized || IsZoomed(window.Handle);
            m_activeDragWindowDpi = GetSafeWindowDpi(window.Handle);

            var pos = GetVisibleWindowRectangle(window);
            if (pos.Width <= 0 || pos.Height <= 0)
            {
                pos = rawPos;
            }
            var initialPos = rawInitialPos;
            if (rawInitialPos.Width > 0 && rawPos.Width > 0)
            {
                int dx = rawInitialPos.Left - rawPos.Left;
                int dy = rawInitialPos.Top - rawPos.Top;
                initialPos = Rectangle.OffsetAndSize(pos.Left + dx, pos.Top + dy, pos.Width, pos.Height);
            }

            m_logger.Information("[Drag] StartExternalDrag on {Window}, Pos={Pos}, InitialPos={InitialPos}, Cursor={Cursor}",
                window.DebugString(), pos, initialPos, cursor);

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Add(window);
            }
            m_movingWindow = window;
            m_currentInteraction = UserInteraction.Moving;
            m_isExternalDrag = true;
            m_lastExternalDragTime = m_sw.Elapsed;

            if (pos.Width > 0)
            {
                m_dragGrabRatioX = Math.Clamp((cursor.X - pos.Left) / (double)pos.Width, 0.1, 0.9);
            }
            else
            {
                m_dragGrabRatioX = 0.5;
            }

            if (pos.Height > 0)
            {
                m_dragGrabRatioY = Math.Clamp((cursor.Y - pos.Top) / (double)pos.Height, 0.05, 0.95);
            }
            else
            {
                m_dragGrabRatioY = 0.1;
            }

            var wa = m_display.WorkArea;
            bool wasSnapped = IsWindowSnappedGeometry(pos, wa) ||
                             (initialPos.Width > 0 && IsWindowSnappedGeometry(initialPos, wa)) ||
                             s_snappedHwnds.ContainsKey(window.Handle);
            if (!wasSnapped)
            {
                using (m_snappedWindowsLock.EnterScope())
                {
                    if (m_snappedWindows.Contains(window) && (pos.Width >= wa.Width * 0.45 || pos.Height >= wa.Height * 0.45))
                        wasSnapped = true;
                }
            }

            if (wasSnapped)
            {
                if (!s_hwndPreSnapMemory.TryGetValue(window.Handle, out var preSnapRect) || !IsValidFloatingRect(preSnapRect, wa))
                {
                    preSnapRect = GetBestFloatingRectangle(window, wa, pos);
                    s_hwndPreSnapMemory[window.Handle] = preSnapRect;
                }
                m_currentDragTargetSize = new Point(preSnapRect.Width, preSnapRect.Height);

                // 在检测到分屏窗口被外部拖拽工具从边缘拖出时，异步安全校准窗口尺寸，
                // 避免在 ProcessingThread 阻塞第三方移动工具并保留原始尺寸记忆至松手结束。
                int targetW = preSnapRect.Width;
                int targetH = preSnapRect.Height;
                int targetX = cursor.X - (int)(targetW * m_dragGrabRatioX);
                int targetY = cursor.Y - (int)(targetH * m_dragGrabRatioY);

                if (targetX < wa.Left) targetX = wa.Left;
                if (targetX + targetW > wa.Right) targetX = wa.Right - targetW;
                if (targetY < wa.Top) targetY = wa.Top;
                if (targetY + targetH > wa.Bottom) targetY = wa.Bottom - targetH;

                var realFloatRect = Rectangle.OffsetAndSize(targetX, targetY, targetW, targetH);
                ApplyWindowGeometryAsyncSafe(window, realFloatRect);

                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_preSnapRectangles[window] = preSnapRect;
                }
                using (m_lastFloatingRectanglesLock.EnterScope())
                {
                    m_lastFloatingRectangles[window] = preSnapRect;
                }
                RecordFloatingMemory(window, preSnapRect);

                m_isDraggingSnappedWindow = true;
                SetPreviewRectangle(null, null);
            }
            else
            {
                m_isDraggingSnappedWindow = false;
                var origRect = initialPos.Width > 0 ? initialPos : pos;
                if (IsValidFloatingRect(origRect, wa))
                {
                    using (m_preSnapRectanglesLock.EnterScope())
                    {
                        m_activeDragOriginalRect[window] = origRect;
                    }
                    using (m_lastFloatingRectanglesLock.EnterScope())
                    {
                        m_lastFloatingRectangles[window] = origRect;
                    }
                    RecordFloatingMemory(window, origRect);
                }
                m_currentDragTargetSize = new Point(origRect.Width, origRect.Height);
            }

            if ((PInvoke.GetAsyncKeyState(1) & 0x8000) == 0)
            {
                _ = m_dispatcher.InvokeAsync(EndExternalDrag);
                return;
            }

            StartDragTrackingTimer();
            StartExternalDragWatchdog();
            UpdateDragPreview(cursor);
        }

        private void EndExternalDrag()
        {
            if (!m_isExternalDrag || m_movingWindow == null)
                return;

            var win = m_movingWindow;
            m_movingWindow = null;
            m_isExternalDrag = false;
            m_potentialExternalDragWindow = null;
            m_potentialExternalDragInitialPos = default;
            m_externalDragFrameCount = 0;

            StopExternalDragWatchdog();
            StopDragTrackingTimer();
            m_lastPreviewCursor = new Point(-99999, -99999);
            SetPreviewRectangle(null, null);

            m_logger.Information("[Drag] EndExternalDrag on {Window}", win.DebugString());

            try
            {
                DoWindowMove(win);
            }
            catch (Exception ex)
            {
                m_logger.Warning(ex, "Error handling external drag move end for {Window}", win.DebugString());
            }
            finally
            {
                m_currentInteraction = UserInteraction.None;
                m_isConfirmedResize = false;
                m_activeDragStartedMaximized = false;
                m_activeDragWindowDpi = 0;
                m_isDraggingTitleBar = false;
                m_isDraggingSnappedWindow = false;
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_activeDragOriginalRect.Remove(win);
                    m_activeDragInitialRawRect.Remove(win);
                }
                ScheduleIgnoreRepositionRemoval(win, 45);
            }
        }

        private void MoveToParentPanel(TilingNode node)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.PullUp(node);
                }
                InvalidateLayout();
            }
            catch (TilingFailedException e)
            {
                m_logger.Error(e, "Attempted pull up of {Node} failed", node);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(e.FailReason));
            }
        }

        private void WrapInSplitPanel(TilingNode node, bool vertical)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.WrapInSplitPanel(node, vertical);
                    m_backend.SetFocus(node);

                    node.Parent!.Padding = GetPanelPaddingRect();
                    node.Parent!.Spacing = GetPanelSpacing();

                    if (m_allocateNewPanelSpace)
                    {
                        node.Parent!.Attach(new PlaceholderNode());
                    }

                    InvalidateLayout();
                }
            }
            catch (TilingFailedException ex)
            {
                m_logger.Error(ex, "Attempted split of {Node} failed", node);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(ex.FailReason));
            }
        }

        private void WrapInStackPanel(TilingNode node)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.WrapInStackPanel(node);
                    node.Parent!.Padding = GetPanelPaddingRect();
                    node.Parent!.Spacing = GetPanelSpacing();
                    m_backend.SetFocus(node);
                    InvalidateLayout();
                }
            }
            catch (TilingFailedException ex)
            {
                m_logger.Error(ex, "Attempted stack of {Node} failed", node);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(ex.FailReason));
            }
        }

        private IntPtr GetOverlayAnchor()
        {
            var desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;
            using (m_backendLock.EnterScope())
            {
                try
                {
                    var focusedNode = m_backend.GetFocus(desktop);
                    if (focusedNode is WindowNode window)
                        return window.WindowReference.Handle;
                }
                catch (ArgumentException)
                {
                    return new IntPtr(0);
                }
            }

            var comparer = m_workspace.CreateSnapshotZOrderComparer();
            using (m_backendLock.EnterScope())
            {
                var tree = m_backend.GetTree(desktop);
                if (tree == null)
                    return new IntPtr(0);
                var topWindow = tree.Root!.Windows
                    .OrderByDescending(x => x.WindowReference, comparer)
                    .FirstOrDefault();

                if (topWindow != null)
                    return topWindow.WindowReference.Handle;

                return new IntPtr(0);
            }
        }

        private void ToggleFloat(IWindow window)
        {
            bool floated;
            using (m_floatingSetLock.EnterScope())
            {
                if (m_floatingSet.Contains(window))
                {
                    floated = false;
                    m_floatingSet.Remove(window);
                }
                else
                {
                    floated = true;
                    m_floatingSet.Add(window);
                }
            }
            DetectChanges(window);
            if (floated)
            {
                OnWindowFloated(window);
            }
            else
            {
                try
                {
                    using (m_backendLock.EnterScope())
                    {
                        m_backend.SetFocus(window);
                    }
                }
                catch
                {
                }
            }
        }

        private void OnDisplayScalingChanged(object? sender, DisplayScalingChangedEventArgs e)
        {
            PropagatePanelHeightChange();
        }

        private void OnPlacementFailed(object? sender, TilingFailedEventArgs e)
        {
            if (e.FailReason == TilingError.NoValidPlacementExists && e.FailSource != null)
            {
                using (m_floatingSetLock.EnterScope())
                {
                    m_floatingSet.Add(e.FailSource);
                }
                OnWindowFloated(e.FailSource);
            }
        }

        private void OnWindowFloated(IWindow window)
        {
            Rectangle? originalPosition;
            try
            {
                using (m_backendLock.EnterScope())
                {
                    originalPosition = m_backend.GetOriginalPosition(window);
                }
            }
            catch
            {
                originalPosition = null;
            }
            try
            {
                originalPosition ??= GetOptimalRestoredSize(window);

                var originalDisplay = m_workspace.DisplayManager.Displays.FirstOrDefault(x => x.Bounds.Contains(originalPosition.Value.Center));
                originalDisplay ??= m_workspace.DisplayManager.PrimaryDisplay;

                var displayBounds = originalDisplay.Bounds;

                var centeredPosition = Rectangle.OffsetAndSize(
                    displayBounds.Left + displayBounds.Width / 2 - originalPosition.Value.Width / 2,
                    displayBounds.Top + displayBounds.Height / 2 - originalPosition.Value.Height / 2,
                    originalPosition.Value.Width,
                    originalPosition.Value.Height);

                window.SetPosition(centeredPosition);
                FocusHelper.ForceActivate(window.Handle);
            }
            catch (Exception e) when (e is InvalidWindowReferenceException || e is InvalidOperationException && window.State != WindowState.Restored)
            {
                // ignore
            }
        }

        private Rectangle GetOptimalRestoredSize(IWindow window)
        {
            var screenSize = m_display.WorkArea.Size;
            var minSize = window.MinSize ?? new Point(0, 0);
            var maxSize = window.MaxSize ?? new Point(screenSize.X, screenSize.Y);
            var pos = window.Position;

            return Rectangle.OffsetAndSize(
                pos.Left,
                pos.Top,
                Math.Max(minSize.X, Math.Min(maxSize.X, Math.Min(screenSize.X, (screenSize.X + minSize.X) / 2))),
                Math.Max(minSize.Y, Math.Min(maxSize.Y, Math.Min(screenSize.Y, (screenSize.Y + minSize.Y) / 2))));
        }


        private void OnCursorLocationChanged(object? sender, CursorLocationChangedEventArgs e)
        {
            // 鼠标未按住时仅隐藏预览阴影框，绝不提前销毁 m_movingWindow 或抹除 m_lastActiveSnapZone
            // 防止松手瞬间鼠标微动导致 OnWindowPositionChangeEnd 丢失吸附意图
            bool isMouseButtonDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0 || (PInvoke.GetAsyncKeyState(2) & 0x8000) != 0;
            if (!isMouseButtonDown)
            {
                SetPreviewRectangle(null, null);
                return;
            }

            if (m_currentInteraction == UserInteraction.Moving && m_movingWindow != null)
            {
                UpdateDragPreview(e.NewLocation);
            }

            if (PendingIntent == null)
                return;

            m_dispatcher.BeginInvoke(() =>
            {
                if (PendingIntent is GroupWithIntent gwi)
                {
                    if (Mouse.LeftButton != MouseButtonState.Pressed)
                    {
                        PendingIntent.Cancel();
                        PendingIntent = null;
                    }

                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.NodeAtPoint(m_workspace.VirtualDesktopManager.CurrentDesktop, e.NewLocation) is WindowNode targetNode)
                        {
                            var newSet = new HashSet<IWindow> { gwi.Source.WindowReference, targetNode.WindowReference };
                            if (!m_gui.PreviewWindows.SetEquals(newSet))
                            {
                                m_gui.PreviewWindows = newSet;
                            }
                        }
                    }
                }
            });
        }

        private void OnPendingIntentChanged(object? sender, EventArgs e)
        {
            if (PendingIntent == null)
            {
                _ = m_dispatcher.BeginInvoke(() =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                });
            }
            else
            {
                if (App.Current.Services.GetService<LowLevelMouseHook>() is LowLevelMouseHook mshk)
                {
                    var startPt = m_workspace.CursorLocation;
                    bool dispatched = false;
                    void onMouseButtonChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
                    {
                        mshk.ButtonStateChanged -= onMouseButtonChanged;
                        if (e.Button == LowLevelMouseHook.MouseButton.Left && e.IsPressed == false)
                        {
                            var pt = new Point(e.X, e.Y);
                            if (Math.Abs(pt.X - startPt.X) > 5 || Math.Abs(pt.Y - startPt.Y) > 5)
                            {
                                if (!dispatched)
                                {
                                    dispatched = true;
                                    m_dispatcher.BeginInvoke(() =>
                                    {
                                        HitTestCompletePendingIntent(pt);
                                    });
                                }
                            }
                        }
                        else
                        {
                            if (!dispatched)
                            {
                                dispatched = true;
                                m_dispatcher.BeginInvoke(() =>
                                {
                                    PendingIntent?.Cancel();
                                    PendingIntent = null;
                                });
                            }
                        }
                    }
                    mshk.ButtonStateChanged += onMouseButtonChanged;
                }
            }
        }

        private void HitTestCompletePendingIntent(Point cursorPosition)
        {
            if (m_pendingIntent is GroupWithIntent intent && m_display.Bounds.Contains(cursorPosition))
            {
                PendingIntent = null;

                WindowNode sourceNode;
                PanelNode panel;
                using (m_backendLock.EnterScope())
                {
                    var node = m_backend.NodeAtPoint(m_workspace.VirtualDesktopManager.CurrentDesktop, cursorPosition);
                    if (node is not WindowNode targetNode)
                    {
                        intent.Cancel();
                        return;
                    }
                    if (targetNode.WindowReference.Equals(intent.Source.WindowReference))
                    {
                        intent.Cancel();
                        return;
                    }

                    switch (intent.Type)
                    {
                        case GroupWithIntent.GroupType.HorizontalPanel:
                            if (!CanSplit(targetNode))
                            {
                                intent.Cancel();
                                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(TilingError.NestingInStackPanel, targetNode.WindowReference));
                                return;
                            }
                            break;
                        case GroupWithIntent.GroupType.VerticalPanel:
                            if (!CanSplit(targetNode))
                            {
                                intent.Cancel();
                                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(TilingError.NestingInStackPanel, targetNode.WindowReference));
                                return;
                            }
                            break;
                        case GroupWithIntent.GroupType.StackPanel:
                            if (!CanStack(targetNode))
                            {
                                intent.Cancel();
                                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(TilingError.NestingInStackPanel, targetNode.WindowReference));
                                return;
                            }
                            break;
                    }

                    // Must complete before doing anything with the intent data.
                    intent.Complete();
                    sourceNode = intent.Source;

                    switch (intent.Type)
                    {
                        case GroupWithIntent.GroupType.HorizontalPanel:
                            m_backend.WrapInSplitPanel(targetNode, vertical: false);
                            break;
                        case GroupWithIntent.GroupType.VerticalPanel:
                            m_backend.WrapInSplitPanel(targetNode, vertical: true);
                            break;
                        case GroupWithIntent.GroupType.StackPanel:
                            m_backend.WrapInStackPanel(targetNode);
                            break;
                    }
                    panel = targetNode.Parent!;
                    panel.Spacing = GetPanelSpacing();
                    panel.Padding = GetPanelPaddingRect();
                }


                BindEventHandlers(sourceNode.WindowReference);
                using (m_windowSetLock.EnterScope())
                {
                    m_windowSet.Add(sourceNode.WindowReference);
                }
                if (CanManage(sourceNode.WindowReference))
                {
                    //m_logger.Information("Window {Handle}={ProcessName} can be managed, registering with backend", e.Source.Handle, e.Source.GetCachedProcessName());
                    try
                    {
                        try
                        {
                            using (m_backendLock.EnterScope())
                            {
                                var node = m_backend.RegisterWindow(sourceNode.WindowReference, panel);
                                m_backend.SetFocus(node);
                            }
                        }
                        catch (NoValidPlacementExistsException)
                        {
                            PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                                TilingError.NoValidPlacementExists, sourceNode.WindowReference));
                        }
                    }
                    catch
                    {
                        return;
                    }

                    InvalidateLayout();
                }
            }
            else
            {
                m_pendingIntent?.Cancel();
            }
        }

        private void OnBeginHorizontalWithRequestedAsync(object? sender, WindowNode e)
        {
            m_gui.PreviewWindows = new HashSet<IWindow> { e.WindowReference };
            PendingIntent = new GroupWithIntent(GroupWithIntent.GroupType.HorizontalPanel, e,
                complete: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                    OnWindowRemoved(this, new WindowChangedEventArgs(e.WindowReference));
                },
                cancel: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                });
        }

        private void OnBeginVerticalWithRequested(object? sender, WindowNode e)
        {
            m_gui.PreviewWindows = new HashSet<IWindow> { e.WindowReference };
            PendingIntent = new GroupWithIntent(GroupWithIntent.GroupType.VerticalPanel, e,
                complete: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                    OnWindowRemoved(this, new WindowChangedEventArgs(e.WindowReference));
                },
                cancel: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                });
        }

        private void OnBeginStackWithRequested(object? sender, WindowNode e)
        {
            m_gui.PreviewWindows = new HashSet<IWindow> { e.WindowReference };
            PendingIntent = new GroupWithIntent(GroupWithIntent.GroupType.StackPanel, e,
                complete: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                    OnWindowRemoved(this, new WindowChangedEventArgs(e.WindowReference));
                },
                cancel: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                });
        }

        private void OnWindowVerticalSplitRequested(object? sender, TilingNode e)
        {
            WrapInSplitPanel(e, true);
        }

        private void OnWindowStackRequested(object? sender, TilingNode e)
        {
            WrapInStackPanel(e);
        }

        private void OnWindowPullUpRequested(object? sender, TilingNode e)
        {
            MoveToParentPanel(e);
        }

        private void OnWindowHorizontalSplitRequested(object? sender, TilingNode e)
        {
            WrapInSplitPanel(e, false);
        }

        private void OnWindowFloatRequested(object? sender, WindowNode e)
        {
            ToggleFloat(e.WindowReference);
        }

        private void OnWindowIgnoreProcessRequested(object? sender, WindowNode e)
        {
            App.Current.AppState.Settings.SaveAsync(x =>
            {
                return x with { ProcessIgnoreList = [.. x.ProcessIgnoreList, e.WindowReference.GetCachedProcessName()] };
            });
        }
        private void OnWindowIgnoreClassRequested(object? sender, WindowNode e)
        {
            App.Current.AppState.Settings.SaveAsync(x =>
            {
                return x with { ClassIgnoreList = [.. x.ClassIgnoreList, ((WinMan.Windows.Win32Window)e.WindowReference).ClassName] };
            });
        }

        private void OnTilingPanelMoving(object? sender, PanelNode panel)
        {
            m_currentInteraction = UserInteraction.Moving;
            m_movingPanelNode = panel;
            InvalidateLayout();
        }

        private void OnTilingPanelMoveRequested(object? sender, PanelNode panel)
        {
            m_logger.Information("Panel {Panel} move ended", panel);
            m_currentInteraction = UserInteraction.None;
            m_movingPanelNode = null;

            try
            {
                var isSwapping = IsSwapModifierPressed();
                var pt = m_workspace.CursorLocation;
                using (m_backendLock.EnterScope())
                {
                    // Check that panel hasn't disappeared during the move.
                    if (panel.Desktop == null)
                    {
                        return;
                    }
                    m_backend.MoveNode(panel, pt, allowNesting: !isSwapping);
                }

                InvalidateLayout();
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
            catch (TilingFailedException e)
            {
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(e.FailReason));
            }
        }

        private void OnTilingNodePullUpRequested(object? sender, TilingNode node)
        {
            MoveToParentPanel(node);
        }

        private void OnDesktopAdded(object? sender, DesktopChangedEventArgs e)
        {
            m_logger.Information("Desktop {Desktop} added to workspace", e.Source);
            var orientation = m_display.Bounds.Width >= m_display.Bounds.Height ? PanelOrientation.Horizontal : PanelOrientation.Vertical;
            using (m_backendLock.EnterScope())
            {
                m_backend.RegisterDesktop(e.Source, m_display.WorkArea, orientation);
            }
        }

        private void OnDesktopRemoved(object? sender, DesktopChangedEventArgs e)
        {
            m_logger.Information("Desktop {Desktop} removed from workspace", e.Source);
            using (m_backendLock.EnterScope())
            {
                m_backend.UnregisterDesktop(e.Source);
            }
        }

        private void OnCurrentDesktopChanged(object? sender, CurrentDesktopChangedEventArgs e)
        {
            Refresh();
            InvalidateLayout();
        }

        private void OnWorkspaceFocusedWindowChanged(object? sender, FocusedWindowChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (m_showFocus)
            {
                InvalidateFocusRectangle(immediate: false);
            }
        }

        private void OnWindowGotFocus(object? sender, WindowFocusChangedEventArgs e)
        {
            m_dispatcher.BeginInvoke(() =>
            {
                m_logger.Information("Got focus on {Window}", e.Source.DebugString());
                try
                {
                    bool hideMaximised = false;
                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(e.Source))
                        {
                            m_logger.Debug("Window {Window} is managed by backend, need to hide all obstructing windows", e.Source.DebugString());
                            // Focused restored windows that are in the tree cause all maximised windows
                            // to be send to the back
                            hideMaximised = true;
                            m_backend.SetFocus(e.Source);
                            if (m_showFocus)
                            {
                                InvalidateFocusRectangle(immediate: false);
                            }
                        }
                        else
                        {
                            m_logger.Debug("Window {Window} is not managed by backend", e.Source.DebugString());
                            if (m_showFocus)
                            {
                                InvalidateFocusRectangle(immediate: false);
                            }
                            return;
                        }
                    }

                    if (hideMaximised)
                    {
                        m_logger.Debug("Moving all obstructing maximised windows to back");
                        var comparer = m_workspace.CreateSnapshotZOrderComparer();
                        foreach (var maximisedWindow in m_workspace.GetCurrentDesktopSnapshot()
                            .Where(x => x.State == WindowState.Maximized && m_display.Bounds.Contains(x.Position.Center))
                            .OrderBy(x => x, comparer))
                        {
                            m_logger.Information("Moving maximised window {Window} to back", maximisedWindow.DebugString());
                            try
                            {
                                if (maximisedWindow.CanReorder)
                                {
                                    maximisedWindow.SendToBack();
                                }
                            }
                            catch (InvalidWindowReferenceException)
                            {
                                continue;
                            }
                            catch (Win32Exception ex)
                            {
                                m_logger.Error(ex, "Moving window {Window} to back failed ({@Metadata})", maximisedWindow.DebugString(), maximisedWindow.GetMetadata());
                                continue;
                            }
                        }
                    }
                    InvalidateLayout();
                }
                catch (InvalidWindowReferenceException)
                {
                    return;
                }
            }, System.Windows.Threading.DispatcherPriority.DataBind);
        }

        private void OnWindowLostFocus(object? sender, WindowFocusChangedEventArgs e)
        {
            // 绝不允许无关窗口的失焦事件或仍在按住鼠标左键拖动期间，意外中断当前正在进行的拖拽状态机
            bool isMouseDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0 || (PInvoke.GetAsyncKeyState(2) & 0x8000) != 0;
            if (m_movingWindow == null || (Equals(e.Source, m_movingWindow) && !isMouseDown))
            {
                m_currentInteraction = UserInteraction.None;
            }
            if (m_showFocus)
            {
                InvalidateFocusRectangle(immediate: false);
            }
        }

        private void OnWindowAdded(object? sender, WindowChangedEventArgs e)
        {
            try
            {
                using (m_windowSetLock.EnterScope())
                {
                    if (!m_windowSet.Add(e.Source))
                    {
                        return;
                    }
                }
                m_logger.Debug("Window {Window} added to workspace", e.Source.DebugString());
                BindEventHandlers(e.Source);
                using (m_newWindowSetLock.EnterScope())
                {
                    m_newWindowSet.Add(e.Source);
                }

                if (m_exclusionMatchers.Any(x => x.Matches(e.Source)))
                {
                    using (m_floatingSetLock.EnterScope())
                    {
                        m_floatingSet.Add(e.Source);
                    }
                }

                if (!AutoRegisterWindows)
                {
                    return;
                }

                if (m_autoFloatNewWindows)
                {
                    using (m_floatingSetLock.EnterScope())
                    {
                        m_floatingSet.Add(e.Source);
                    }
                }

                if (CanManage(e.Source) && e.Source.State == WindowState.Restored)
                {
                    m_logger.Information("Window {Window} can be managed, registering with backend ({Display})", e.Source.DebugString(), m_display);
                    m_dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            try
                            {
                                using (m_backendLock.EnterScope())
                                {
                                    if (m_backend.HasWindow(e.Source))
                                    {
                                        return;
                                    }

                                    var node = m_backend.RegisterWindow(e.Source, maxTreeWidth: m_autoSplitCount);
                                    node.Parent!.Padding = GetPanelPaddingRect();
                                    node.Parent!.Spacing = GetPanelSpacing();
                                }
                            }
                            catch (NoValidPlacementExistsException)
                            {
                                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                                    TilingError.NoValidPlacementExists, e.Source));
                            }
                        }
                        catch
                        {
                            return;
                        }

                        InvalidateLayout();
                    }, System.Windows.Threading.DispatcherPriority.DataBind);
                }
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
        }

        private void OnWindowRemoved(object? sender, WindowChangedEventArgs e)
        {
            m_logger.Information("Window {Window} removed from workspace", e.Source.DebugString());

            UnbindEventHandlers(e.Source);
            CancelPendingSnapSettle(e.Source.Handle);
            s_hwndFloatingMemory.TryRemove(e.Source.Handle, out _);
            s_snappedHwnds.TryRemove(e.Source.Handle, out _);
            s_hwndPreSnapMemory.TryRemove(e.Source.Handle, out _);
            if (Equals(m_movingWindow, e.Source))
            {
                m_movingWindow = null;
                m_currentInteraction = UserInteraction.None;
                m_isExternalDrag = false;
                m_potentialExternalDragWindow = null;
                m_potentialExternalDragInitialPos = default;
                m_externalDragFrameCount = 0;
                m_isConfirmedResize = false;
                m_activeDragStartedMaximized = false;
                m_activeDragWindowDpi = 0;
                m_isDraggingSnappedWindow = false;
                m_isDraggingTitleBar = false;
                m_lastActiveSnapZone = ScreenSnapZone.None;
                m_lastActiveSnapTime = TimeSpan.Zero;
                m_lastPreviewCursor = new Point(-99999, -99999);
                StopFallbackDragProbe();
                StopExternalDragWatchdog();
                StopDragTrackingTimer();
                SetPreviewRectangle(null, null);
            }
            using (m_savedLocationsLock.EnterScope())
            {
                m_savedLocations.Remove(e.Source);
            }
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_preSnapRectangles.Remove(e.Source);
                m_activeDragOriginalRect.Remove(e.Source);
                m_activeDragInitialRawRect.Remove(e.Source);
            }
            using (m_lastFloatingRectanglesLock.EnterScope())
            {
                m_lastFloatingRectangles.Remove(e.Source);
            }
            using (m_snappedWindowsLock.EnterScope())
            {
                m_snappedWindows.Remove(e.Source);
            }
            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Remove(e.Source);
            }
            using (m_backendLock.EnterScope())
            {
                if (m_backend.HasWindow(e.Source))
                {
                    m_logger.Debug("Unregistering window {Window} from backend", e.Source.DebugString());
                    m_backend.UnregisterWindow(e.Source);
                    InvalidateLayout();
                }
            }
            using (m_floatingSetLock.EnterScope())
            {
                m_floatingSet.Remove(e.Source);
            }
            using (m_newWindowSetLock.EnterScope())
            {
                m_newWindowSet.Remove(e.Source);
            }
            using (m_windowSetLock.EnterScope())
            {
                m_windowSet.Remove(e.Source);
            }
        }

        private void DoWindowMove(IWindow window)
        {
            var pt = GetLiveCursorLocation();
            if (!IsPointOwnedByThisDisplay(pt, out var testPt))
            {
                m_lastActiveSnapZone = ScreenSnapZone.None;
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_preSnapRectangles.Remove(window);
                    m_activeDragOriginalRect.Remove(window);
                    m_activeDragInitialRawRect.Remove(window);
                }
                using (m_snappedWindowsLock.EnterScope())
                {
                    m_snappedWindows.Remove(window);
                }
                return;
            }

            var wa = m_display.WorkArea;
            var snapZone = GetSnapZone(testPt, wa);

            // ⭐ 核心优化：防微震颤与松手竞态记忆
            // 若松手瞬间鼠标由于物理按键回弹微颤移出了边缘，但用户在过去 400ms 内正处于预览贴靠区（尤其是 1/4 角落区），
            // 严格以用户所见即所得的预览贴靠区为准进行吸附，彻底解决 1/4 分屏松手偶尔失效问题！
            if (snapZone == ScreenSnapZone.None && m_lastActiveSnapZone != ScreenSnapZone.None &&
                (m_sw.Elapsed - m_lastActiveSnapTime) <= TimeSpan.FromMilliseconds(400))
            {
                snapZone = m_lastActiveSnapZone;
                m_logger.Information("[Snap] Using cached active preview snap zone {Zone} (within 400ms release buffer)", snapZone);
            }
            // 若松手瞬间滑向了半屏（如 TopHalf），但刚才处于角落 1/4 预览（如 TopLeftQuarter），且时间极其贴近（400ms），优先遵循角落 1/4 分屏
            else if (IsHalfZone(snapZone) && IsQuarterZone(m_lastActiveSnapZone) &&
                     (m_sw.Elapsed - m_lastActiveSnapTime) <= TimeSpan.FromMilliseconds(400))
            {
                snapZone = m_lastActiveSnapZone;
                m_logger.Information("[Snap] Overriding edge zone with corner quarter zone {Zone} from recent preview", snapZone);
            }

            m_lastActiveSnapZone = ScreenSnapZone.None;

            if (snapZone != ScreenSnapZone.None)
            {
                var targetRect = GetSnapRectangle(snapZone, wa);
                if (targetRect.HasValue)
                {
                    m_logger.Information("Snapping window {Window} to {Zone} rectangle {Rect}",
                        window.DebugString(), snapZone, targetRect.Value);

                    // 记录停靠前的原始窗口状态（仅首次记录，避免在边缘间反复切换时丢失原始浮动大小）
                    using (m_preSnapRectanglesLock.EnterScope())
                    {
                        if (m_activeDragOriginalRect.TryGetValue(window, out var origDragRect) &&
                            IsValidFloatingRect(origDragRect, wa) &&
                            !s_snappedHwnds.ContainsKey(window.Handle))
                        {
                            m_preSnapRectangles[window] = origDragRect;
                            s_hwndPreSnapMemory[window.Handle] = origDragRect;
                            using (m_lastFloatingRectanglesLock.EnterScope())
                            {
                                m_lastFloatingRectangles[window] = origDragRect;
                            }
                            RecordFloatingMemory(window, origDragRect);
                            m_activeDragOriginalRect.Remove(window);
                            m_activeDragInitialRawRect.Remove(window);
                        }
                        else if (!m_preSnapRectangles.ContainsKey(window) ||
                                 IsWindowSnappedGeometry(m_preSnapRectangles[window], wa))
                        {
                            if (!s_hwndPreSnapMemory.TryGetValue(window.Handle, out var bestFloat) ||
                                !IsValidFloatingRect(bestFloat, wa))
                            {
                                bestFloat = GetBestFloatingRectangle(window, wa, GetVisibleWindowRectangle(window));
                            }
                            m_preSnapRectangles[window] = bestFloat;
                            s_hwndPreSnapMemory[window.Handle] = bestFloat;
                            using (m_lastFloatingRectanglesLock.EnterScope())
                            {
                                m_lastFloatingRectangles[window] = bestFloat;
                            }
                            RecordFloatingMemory(window, bestFloat);
                        }
                    }

                    s_snappedHwnds[window.Handle] = 1;
                    using (m_snappedWindowsLock.EnterScope())
                    {
                        m_snappedWindows.Add(window);
                    }

                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(window))
                        {
                            m_backend.UnregisterWindow(window);
                        }
                    }
                    using (m_floatingSetLock.EnterScope())
                    {
                        m_floatingSet.Add(window);
                    }

                    ApplyWindowSnap(window, targetRect.Value, isUnsnapRestore: false);
                    InvalidateLayout();
                    return;
                }
            }

            // 未落在停靠区：如果是从边缘拖离，自动恢复停靠前的原始窗口状态
            bool hadPreSnap = false;
            Rectangle preSnapRect = default;
            using (m_preSnapRectanglesLock.EnterScope())
            {
                if (m_preSnapRectangles.TryGetValue(window, out preSnapRect))
                {
                    hadPreSnap = true;
                    m_preSnapRectangles.Remove(window);
                }
                m_activeDragOriginalRect.Remove(window);
                m_activeDragInitialRawRect.Remove(window);
            }

            bool wasGloballySnapped = s_snappedHwnds.TryRemove(window.Handle, out _);
            if (s_hwndPreSnapMemory.TryRemove(window.Handle, out var globalPreSnap) && !hadPreSnap)
            {
                preSnapRect = globalPreSnap;
                hadPreSnap = true;
            }

            using (m_snappedWindowsLock.EnterScope())
            {
                m_snappedWindows.Remove(window);
            }

            if (!hadPreSnap && (m_isDraggingSnappedWindow || wasGloballySnapped))
            {
                preSnapRect = GetBestFloatingRectangle(window, wa, GetVisibleWindowRectangle(window));
                hadPreSnap = true;
            }

            if (hadPreSnap && preSnapRect.Width > 0 && preSnapRect.Height > 0)
            {
                m_logger.Information("Window {Window} dragged away from edge/corner, restoring pre-snap rectangle {Rect}",
                    window.DebugString(), preSnapRect);

                // 先等待 Win32 拖拽循环完全落定，确保读取到的 curPos 是松手后的最终真实可视位置
                WaitForMoveSizeExit(window.Handle, 35);

                int targetW = preSnapRect.Width;
                int targetH = preSnapRect.Height;

                if (!IsValidFloatingDimensions(targetW, targetH, wa) ||
                    IsWindowSnappedGeometry(preSnapRect, wa))
                {
                    var best = GetBestFloatingRectangle(window, wa, GetVisibleWindowRectangle(window));
                    if (IsValidFloatingRect(best, wa))
                    {
                        targetW = best.Width;
                        targetH = best.Height;
                    }
                    else
                    {
                        bool isPortrait = wa.Width < wa.Height;
                        targetW = isPortrait ? (int)(wa.Height * 0.45) : (int)(wa.Width * 0.60);
                        targetH = isPortrait ? (int)(wa.Height * 0.75) : (int)(wa.Height * 0.65);
                    }
                }
                targetW = Math.Clamp(targetW, 100, wa.Width);
                targetH = Math.Clamp(targetH, 80, wa.Height);

                // ⭐ 核心优化：以用户实际拖放到达的物理位置（curPos）为绝对基准！
                // 彻底废除比例（Ratio）计算，解决因宽高不同产生的像素漂移跳动问题。
                var curPos = GetVisibleWindowRectangle(window);
                if (curPos.Width <= 0 || curPos.Height <= 0)
                {
                    curPos = window.Position;
                }

                // 1. 垂直方向（Y轴）：标题栏高度固定，顶边严格保持在用户当前放置的 Y 坐标，Y轴绝对 0 偏移！
                int targetY = curPos.Top;

                // 2. 水平方向（X轴）：优先保持左上角不动（所见即所得）；
                // 若鼠标在窗口内的实际偏移仍在还原后窗口的标题栏范围内，左边框完全不动；
                // 仅当光标过于靠右时平滑将光标保留在标题栏内。
                int grabOffsetX = pt.X - curPos.Left;
                int targetX = curPos.Left;
                if (grabOffsetX > targetW - 60)
                {
                    targetX = pt.X - (targetW - 60);
                }
                else if (grabOffsetX < 20)
                {
                    targetX = pt.X - Math.Max(20, grabOffsetX);
                }

                // 屏幕工作区保护
                if (targetX + targetW > wa.Right) targetX = wa.Right - targetW;
                if (targetX < wa.Left) targetX = wa.Left;
                if (targetY + targetH > wa.Bottom) targetY = wa.Bottom - targetH;
                if (targetY < wa.Top) targetY = wa.Top;

                var restoredRect = Rectangle.OffsetAndSize(targetX, targetY, targetW, targetH);
                ApplyWindowSnap(window, restoredRect, isUnsnapRestore: true);

                // 持续保留该真实浮动大小记忆，供后续再次贴靠或拖出使用
                using (m_lastFloatingRectanglesLock.EnterScope())
                {
                    m_lastFloatingRectangles[window] = restoredRect;
                }
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_preSnapRectangles.Remove(window);
                }
                using (m_snappedWindowsLock.EnterScope())
                {
                    m_snappedWindows.Remove(window);
                }
                RecordFloatingMemory(window, restoredRect);
            }
            m_isDraggingSnappedWindow = false;
            m_isDraggingTitleBar = false;

            // 未落在停靠区：拖出分屏后自由浮动
            using (m_backendLock.EnterScope())
            {
                if (m_backend.HasWindow(window))
                {
                    m_logger.Information("Window {Window} moved into free space, detaching from tiling partition (floating)", window.DebugString());
                    using (m_floatingSetLock.EnterScope())
                    {
                        m_floatingSet.Add(window);
                    }
                    m_backend.UnregisterWindow(window);
                    InvalidateLayout();
                }
            }
        }

        private void OnWindowPositionChangeEnd(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (m_movingWindow != null && !Equals(m_movingWindow, e.Source))
            {
                // 不让无关后台窗口延迟到达的 MOVESIZEEND 误中断当前活跃拖拽窗口
                ScheduleIgnoreRepositionRemoval(e.Source, 45);
                return;
            }

            StopDragTrackingTimer();
            m_nativeDragButtonUpTime = TimeSpan.Zero;
            m_lastPreviewCursor = new Point(-99999, -99999);
            SetPreviewRectangle(null, null);

            var win = m_movingWindow ?? e.Source;
            m_movingWindow = null;
            bool wasDraggingSnapped = m_isDraggingSnappedWindow;
            bool wasDraggingTitleBar = m_isDraggingTitleBar;
            bool wasConfirmedResize = m_isConfirmedResize;
            bool startedMaximized = m_activeDragStartedMaximized;
            var interaction = m_currentInteraction;
            m_currentInteraction = UserInteraction.None;
            m_isConfirmedResize = false;
            m_activeDragStartedMaximized = false;
            m_isDraggingTitleBar = false;
            m_isDraggingSnappedWindow = false;

            Rectangle initialRaw = default;
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_activeDragInitialRawRect.TryGetValue(e.Source, out initialRaw);
            }

            if (win != null && interaction == UserInteraction.Resizing && !wasConfirmedResize)
            {
                var finalRaw = GetRawWindowRectangle(win);
                int tol = GetResizeJitterTolerance();
                bool sizeUnchanged = initialRaw.Width > 0 && initialRaw.Height > 0 &&
                                     Math.Abs(finalRaw.Width - initialRaw.Width) <= tol &&
                                     Math.Abs(finalRaw.Height - initialRaw.Height) <= tol &&
                                     !HasResizeDeformation(initialRaw, finalRaw, tol);
                if (sizeUnchanged)
                {
                    // 仅在“未通过光标/HitTest/尺寸形变确认过缩放”的场景下，若整次拖动尺寸未变才交还移动流程
                    interaction = UserInteraction.Moving;
                }
            }
            else if (win != null && interaction == UserInteraction.Moving)
            {
                var finalRaw = GetRawWindowRectangle(win);
                int tol = GetResizeJitterTolerance();
                if (!TryRebaseForWindowDpiChange(win, finalRaw))
                {
                    bool sizeDelta1Px = initialRaw.Width > 0 && initialRaw.Height > 0 &&
                                        (Math.Abs(finalRaw.Width - initialRaw.Width) >= 1 ||
                                         Math.Abs(finalRaw.Height - initialRaw.Height) >= 1);
                    if ((!startedMaximized && initialRaw.Width > 0 && initialRaw.Height > 0 && HasResizeDeformation(initialRaw, finalRaw, tol)) ||
                        (IsResizeCursorActive() && sizeDelta1Px))
                    {
                        // 极短帧快拉兜底：即便松手太快未及收到中间帧，终态发生形变依然 100% 拦截误分屏
                        interaction = UserInteraction.Resizing;
                        m_lastActiveSnapZone = ScreenSnapZone.None;
                        m_lastActiveSnapTime = TimeSpan.Zero;
                    }
                }
            }

            m_activeDragWindowDpi = 0;

            if (win != null && interaction != UserInteraction.Resizing)
            {
                try
                {
                    DoWindowMove(win);
                }
                catch (InvalidWindowReferenceException)
                {
                }
                catch (TilingFailedException ex)
                {
                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(ex.FailReason, win));
                }
                catch (Exception ex)
                {
                    m_logger.Warning(ex, "Error handling window move end for {Window}", win.DebugString());
                }
            }
            else if (win != null && interaction == UserInteraction.Resizing)
            {
                var finalPos = GetVisibleWindowRectangle(win);
                if (finalPos.Width <= 0 || finalPos.Height <= 0)
                {
                    finalPos = e.NewPosition;
                }
                var wa = m_display.WorkArea;
                if (IsPointOwnedByThisDisplay(finalPos.Center, out _) &&
                    IsValidFloatingRect(finalPos, wa) &&
                    !IsWindowSnapped(win, finalPos, wa) &&
                    !s_snappedHwnds.ContainsKey(win.Handle))
                {
                    using (m_lastFloatingRectanglesLock.EnterScope())
                    {
                        m_lastFloatingRectangles[win] = finalPos;
                    }
                    RecordFloatingMemory(win, finalPos);
                    m_logger.Information("Window {Window} manually resized to {Rect}, recorded in floating memory (no snap)", win.DebugString(), finalPos);
                }
            }

            m_logger.Information("Window {Window} move/resize ended", e.Source.DebugString());
            InvalidateLayout();
            ScheduleIgnoreRepositionRemoval(e.Source, 45);
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_activeDragOriginalRect.Remove(e.Source);
                m_activeDragInitialRawRect.Remove(e.Source);
            }

            if (m_showFocus)
            {
                InvalidateFocusRectangle(immediate: true);
            }
        }

        private TimeSpan m_lastPlacementFailed = TimeSpan.Zero;
        private TimeSpan m_lastWindowPositionChanged = TimeSpan.Zero;

        private void OnWindowPositionChanged(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (m_sw.Elapsed - m_lastPlacementFailed <= TimeSpan.FromMilliseconds(100))
            {
                return;
            }

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                if (!m_ignoreRepositionSet.Contains(e.Source))
                {
                    // 防御性自愈：若已无活跃拖拽对象，绝不允许残留交互状态锁死后续外部辅助拖动检测
                    if (m_movingWindow == null && !m_isExternalDrag && m_movingPanelNode == null && m_currentInteraction != UserInteraction.None)
                    {
                        m_currentInteraction = UserInteraction.None;
                    }

                    // Check if this could be an external drag (e.g. Quicker / SetWindowPos mover)
                    // 仅当用户正在物理按住鼠标左键，且光标与命中区域均不属于边框缩放时，才进行外部拖动检测与触发
                    bool isLeftButtonDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0;
                    bool hasRecentDown = TryGetRecentLeftDown(out var recentDownPt, out bool recentDownWasResize);
                    bool activeResizeCursor = recentDownWasResize || IsResizeCursorActive();
                    if (m_currentInteraction == UserInteraction.None && m_movingWindow == null && isLeftButtonDown && !activeResizeCursor)
                    {
                        var cursor = GetLiveCursorLocation();
                        if (IsPointOwnedByThisDisplay(cursor, out _))
                        {
                            bool isSizePreservedExternal = Math.Abs(e.OldPosition.Width - e.NewPosition.Width) <= 4 &&
                                                           Math.Abs(e.OldPosition.Height - e.NewPosition.Height) <= 4 &&
                                                           !IsSingleEdgeAnchoredTransition(e.OldPosition, e.NewPosition);
                            int winDx = e.NewPosition.Left - e.OldPosition.Left;
                            int winDy = e.NewPosition.Top - e.OldPosition.Top;
                            bool isPositionChanged = (winDx != 0 || winDy != 0);

                            // Cursor is inside or very close to the moving window
                            bool isCursorInsideWindow = cursor.X >= e.NewPosition.Left - 120 &&
                                                        cursor.X <= e.NewPosition.Right + 120 &&
                                                        cursor.Y >= e.NewPosition.Top - 120 &&
                                                        cursor.Y <= e.NewPosition.Bottom + 120;

                            if (isSizePreservedExternal && isPositionChanged && isCursorInsideWindow)
                            {
                                if (Equals(m_potentialExternalDragWindow, e.Source) && (m_sw.Elapsed - m_lastExternalDragTime) <= TimeSpan.FromMilliseconds(250))
                                {
                                    bool matchesInitialSize = m_potentialExternalDragInitialPos.Width <= 0 ||
                                        (Math.Abs(e.NewPosition.Width - m_potentialExternalDragInitialPos.Width) <= 4 &&
                                         Math.Abs(e.NewPosition.Height - m_potentialExternalDragInitialPos.Height) <= 4 &&
                                         !IsSingleEdgeAnchoredTransition(m_potentialExternalDragInitialPos, e.NewPosition));
                                    if (!matchesInitialSize)
                                    {
                                        m_potentialExternalDragWindow = null;
                                        m_potentialExternalDragInitialPos = default;
                                        m_externalDragFrameCount = 0;
                                    }
                                    else
                                    {
                                        m_externalDragFrameCount++;
                                        m_lastExternalDragTime = m_sw.Elapsed;

                                        if (m_externalDragFrameCount >= 2)
                                        {
                                            double scale = m_display.Scaling > 0 ? m_display.Scaling : 1.0;
                                            var curRaw = GetRawWindowRectangle(e.Source);
                                            var initialCheckRect = m_potentialExternalDragInitialPos.Width > 0
                                                ? m_potentialExternalDragInitialPos
                                                : curRaw;
                                            if (IsResizeBorderClick(
                                                e.Source.Handle,
                                                initialCheckRect,
                                                initialCheckRect,
                                                hasRecentDown ? recentDownPt : cursor,
                                                hasRecentDown ? recentDownPt : null,
                                                recentDownWasResize,
                                                scale,
                                                out _))
                                            {
                                                m_potentialExternalDragWindow = null;
                                                m_potentialExternalDragInitialPos = default;
                                                m_externalDragFrameCount = 0;
                                            }
                                            else
                                            {
                                                // Confirmed external drag!
                                                StartExternalDrag(e.Source, e.NewPosition, m_potentialExternalDragInitialPos, cursor);
                                                return;
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    m_potentialExternalDragWindow = e.Source;
                                    m_potentialExternalDragInitialPos = e.OldPosition;
                                    m_externalDragFrameCount = 1;
                                    m_lastExternalDragTime = m_sw.Elapsed;
                                }
                            }
                            else if (Equals(m_potentialExternalDragWindow, e.Source))
                            {
                                m_potentialExternalDragWindow = null;
                                m_potentialExternalDragInitialPos = default;
                                m_externalDragFrameCount = 0;
                            }
                        }
                    }
                    else if (!isLeftButtonDown || activeResizeCursor)
                    {
                        m_potentialExternalDragWindow = null;
                        m_potentialExternalDragInitialPos = default;
                        m_externalDragFrameCount = 0;
                    }

                    // Some other event might have resulted in the movement of the window.
                    // Do not call DetectChanges under the lock, to avoid deadlock.
                    m_dispatcher.InvokeAsync(() =>
                    {
                        DetectChanges(e.Source);
                        if (m_showFocus && Equals(m_workspace.FocusedWindow, e.Source))
                        {
                            UpdateFocusRectangleDirect();
                        }
                    });
                    return;
                }
            }

            if (m_movingWindow == null || !Equals(m_movingWindow, e.Source))
            {
                // 拖放刚结束处于冷却窗内收到的异步 SetWindowPos 回声事件或后台窗口变化：直接忽略，绝不污染或限流当前拖拽！
                return;
            }

            double refreshRate = m_display.RefreshRate > 0 ? m_display.RefreshRate : 60.0;
            if (m_currentInteraction != UserInteraction.None && m_sw.Elapsed - m_lastWindowPositionChanged <= TimeSpan.FromSeconds(1.0 / (refreshRate * 2.0)))
            {
                return;
            }
            m_lastWindowPositionChanged = m_sw.Elapsed;

            if (m_isExternalDrag && Equals(m_movingWindow, e.Source))
            {
                m_lastExternalDragTime = m_sw.Elapsed;
            }

            // 实时记录正常浮动窗口的可视尺寸与位置（统一使用 DWM 可视矩形，避免混入隐形阴影边框）
            var curWa = m_display.WorkArea;
            var curPos = GetVisibleWindowRectangle(e.Source);
            if (curPos.Width <= 0 || curPos.Height <= 0)
            {
                curPos = e.NewPosition;
            }
            if (IsPointOwnedByThisDisplay(curPos.Center, out _) &&
                IsValidFloatingRect(curPos, curWa) &&
                !IsWindowSnapped(e.Source, curPos, curWa) &&
                !s_snappedHwnds.ContainsKey(e.Source.Handle) &&
                !m_isDraggingSnappedWindow)
            {
                using (m_lastFloatingRectanglesLock.EnterScope())
                {
                    m_lastFloatingRectangles[e.Source] = curPos;
                }
                RecordFloatingMemory(e.Source, curPos);
            }

            if (m_currentInteraction == UserInteraction.Resizing)
            {
                m_lastActiveSnapZone = ScreenSnapZone.None;
                m_lastActiveSnapTime = TimeSpan.Zero;
                SetPreviewRectangle(null, null);
                return;
            }

            var curRawPos = GetRawWindowRectangle(e.Source);
            if (curRawPos.Width <= 0 || curRawPos.Height <= 0)
            {
                curRawPos = e.NewPosition;
            }

            if (!TryRebaseForWindowDpiChange(e.Source, curRawPos))
            {
                Rectangle rawStartRect = default;
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_activeDragInitialRawRect.TryGetValue(e.Source, out rawStartRect);
                }

                int sizeTol = GetResizeJitterTolerance();
                bool sizeDelta1Px = rawStartRect.Width > 0 && rawStartRect.Height > 0 &&
                                    (Math.Abs(curRawPos.Width - rawStartRect.Width) >= 1 ||
                                     Math.Abs(curRawPos.Height - rawStartRect.Height) >= 1);

                // 尺寸形变判定（覆盖自由窗口 + 已贴靠窗口的边框缩放 + 外部拖拽意外形变）：
                // 只要窗口未在拖拽起点处于最大化态，未因跨屏 DPI 重算，且本次形变不属于外部拖出贴靠时的预期还原尺寸，
                // 只要超出容差、出现 >=2px 单边锚定拉伸或缩放光标伴随 >=1px 变化，立即锁定为 Resizing 并彻底禁止边缘分屏！
                bool isSizeChanged = !m_activeDragStartedMaximized &&
                                     !IsExpectedUnsnapRestoreSize(e.Source, curRawPos, sizeTol) &&
                                     (HasResizeDeformation(rawStartRect, curRawPos, sizeTol) ||
                                      (IsResizeCursorActive() && sizeDelta1Px));
                if (isSizeChanged)
                {
                    SwitchActiveDragToResizing(
                        e.Source,
                        $"SizeDeformation dW={Math.Abs(curRawPos.Width - rawStartRect.Width)}, dH={Math.Abs(curRawPos.Height - rawStartRect.Height)}");
                    return;
                }
            }

            if (m_currentInteraction != UserInteraction.Resizing)
            {
                m_currentInteraction = UserInteraction.Moving;
            }

            try
            {
                DetectChanges(e.Source);

                if (e.NewPosition == e.OldPosition)
                {
                    return;
                }

                if (m_currentInteraction == UserInteraction.Moving)
                {
                    UpdateDragPreview(GetLiveCursorLocation());
                }
                else
                {
                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(e.Source))
                        {
                            var node = m_backend.FindWindow(e.Source);
                            var oldPosition = node!.ComputedContentRectangle;
                            var frame = e.Source.FrameMargins;
                            var adjustedRect = new Rectangle(
                                left: oldPosition.Left - frame.Left,
                                top: oldPosition.Top - frame.Top,
                                right: oldPosition.Right + frame.Right,
                                bottom: oldPosition.Bottom + frame.Bottom);

                            m_logger.Debug("Window {Window} size is different, attempting to resize window from {OldPosition} to {NewPosition}", e.Source.DebugString(), adjustedRect, e.NewPosition);
                            m_backend.ResizeWindow(e.Source, e.NewPosition, adjustedRect);
                            UpdateTree(node.Desktop!);
                        }
                    }
                }
                InvalidateLayout();
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
            catch (TilingFailedException ex)
            {
                if (m_sw.Elapsed - m_lastPlacementFailed <= TimeSpan.FromSeconds(1))
                {
                    return;
                }
                m_lastPlacementFailed = m_sw.Elapsed;
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(ex.FailReason, e.Source));
            }
            finally
            {
                Unfreeze();
            }
        }

        private void OnWindowTopmostChanged(object? sender, WindowTopmostChangedEventArgs e)
        {
            if (!m_active)
                return;

            try
            {
                m_logger.Verbose("Changed topmost of window {Window}", e.Source.DebugString());
                DetectChanges(e.Source);
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
        }

        private void OnWindowStateChanged(object? sender, WindowStateChangedEventArgs e)
        {
            if (!m_active)
                return;

            void UnregisterAndSaveLocation()
            {
                using (m_backendLock.EnterScope())
                {
                    var window = m_backend.FindWindow(e.Source);
                    if (window != null)
                    {
                        using (m_savedLocationsLock.EnterScope())
                        {
                            // Be resilient to multiple OnWindowStateChanged events happening one after the other
                            m_savedLocations[e.Source] = new NodeLocation(window);
                        }
                        InvalidateLayout();
                        m_backend.UnregisterWindow(e.Source);
                    }
                }
                DetectChanges(e.Source);
            }

            void RegisterAndRestoreLocation()
            {
                NodeLocation? savedLocation;
                using (m_savedLocationsLock.EnterScope())
                {
                    if (m_savedLocations.TryGetValue(e.Source, out savedLocation))
                    {
                        m_savedLocations.Remove(e.Source);
                    }
                }

                void RegisterInTopLevelPanel()
                {
                    try
                    {
                        var window = m_backend.RegisterWindow(e.Source, maxTreeWidth: m_autoSplitCount);
                        window.Parent!.Padding = GetPanelPaddingRect();
                        window.Parent!.Spacing = GetPanelSpacing();
                    }
                    catch (WindowAlreadyRegisteredException)
                    {
                        // Window might be already registered!
                        var registered = m_backend.FindWindow(e.Source);
                        if (registered == null)
                        {
                            throw;
                        }
                        // This is clearly a race condition with DetectChanges dirty checking.
                    }
                }

                void RegisterInSavedPanel()
                {
                    WindowNode window;
                    try
                    {
                        window = m_backend.RegisterWindow(e.Source, savedLocation.Parent);
                        window.Parent!.Padding = GetPanelPaddingRect();
                        window.Parent!.Spacing = GetPanelSpacing();
                    }
                    catch (WindowAlreadyRegisteredException)
                    {
                        // Window might be already registered!
                        var registered = m_backend.FindWindow(e.Source);
                        if (registered == null)
                        {
                            throw;
                        }
                        // This is clearly a race condition with DetectChanges dirty checking.
                        window = registered;
                    }

                    window.Parent!.Detach(window);
                    int childCount = savedLocation.Parent.Children.Count;
                    int index = Math.Min(savedLocation.Index, childCount);
                    savedLocation.Parent.Attach(index, window);

                    // Restore size
                    if (window.Parent is GridLikeNode gridNode)
                    {
                        if (m_backend.GetTree(m_workspace.VirtualDesktopManager.CurrentDesktop) is DesktopTree tree)
                        {
                            // Assign ComputedRectangle to that Resize will work.
                            try
                            {
                                tree.Measure();
                                tree.Arrange();
                            }
                            catch (UnsatisfiableFlexConstraintsException)
                            {
                            }
                            if (gridNode.CanResizeInOrientation(PanelOrientation.Horizontal))
                            {
                                gridNode.ResizeTo(window, savedLocation.ComputedRectangle.Width, GrowDirection.Both);
                            }
                            else
                            {
                                gridNode.ResizeTo(window, savedLocation.ComputedRectangle.Height, GrowDirection.Both);
                            }
                        }
                    }
                }

                try
                {
                    using (m_backendLock.EnterScope())
                    {
                        if (savedLocation?.Parent?.Desktop != null)
                        {
                            try
                            {
                                RegisterInSavedPanel();
                            }
                            catch (NoValidPlacementExistsException)
                            {
                                RegisterInTopLevelPanel();
                            }
                        }
                        else
                        {
                            RegisterInTopLevelPanel();
                        }
                    }
                }
                catch (NoValidPlacementExistsException)
                {
                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                        TilingError.NoValidPlacementExists, e.Source));
                }
                DetectChanges(e.Source);
            }

            try
            {
                m_logger.Information("Changed state of window {Window} to {NewState}", e.Source.DebugString(), e.NewState);

                try
                {
                    // Window is now minimized or maximized but was restored
                    if ((e.NewState == WindowState.Maximized || e.NewState == WindowState.Minimized)
                        && e.OldState == WindowState.Restored)
                    {
                        UnregisterAndSaveLocation();
                    }
                    // Window is now restored
                    else if (e.NewState == WindowState.Restored
                        && (e.OldState == WindowState.Maximized || e.OldState == WindowState.Minimized))
                    {
                        if (!CanManage(e.Source))
                        {
                            return;
                        }
                        RegisterAndRestoreLocation();
                    }
                    else
                    {
                        DetectChanges(e.Source);
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    return;
                }
                catch (WindowAlreadyRegisteredException)
                {
                    return;
                }

                if (Equals(m_workspace.FocusedWindow, sender))
                {
                    m_logger.Debug("Window {Window} is also focused, calling OnWindowGotFocus", e.Source.DebugString());
                    // This is to update focus when a maximised window is restored.
                    OnWindowGotFocus(e.Source, new WindowFocusChangedEventArgs(e.Source, true));
                }
                else if (m_showFocus)
                {
                    UpdateFocusRectangle();
                }
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
        }

        private void OnWindowPositionChangeStart(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!m_active)
                return;

            StopFallbackDragProbe();
            CancelPendingSnapSettle(e.Source.Handle);
            if (m_movingWindow != null && !Equals(m_movingWindow, e.Source))
            {
                using (m_ignoreRepositionSetLock.EnterScope())
                {
                    m_ignoreRepositionSet.Remove(m_movingWindow);
                }
            }

            m_isExternalDrag = false;
            m_potentialExternalDragWindow = null;
            m_potentialExternalDragInitialPos = default;
            m_externalDragFrameCount = 0;
            StopExternalDragWatchdog();

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Add(e.Source);
            }
            m_movingWindow = e.Source;
            m_currentInteraction = UserInteraction.Moving;
            m_isConfirmedResize = false;
            m_activeDragStartedMaximized = e.Source.State == WindowState.Maximized || IsZoomed(e.Source.Handle);
            m_activeDragWindowDpi = GetSafeWindowDpi(e.Source.Handle);

            var cursor = GetLiveCursorLocation();
            bool hasRecentDown = TryGetRecentLeftDown(out var recentDownPt, out bool recentDownWasResize);
            Rectangle rawStartPos = GetRawWindowRectangle(e.Source);
            if (rawStartPos.Width <= 0 || rawStartPos.Height <= 0)
            {
                rawStartPos = e.NewPosition.Width > 0 ? e.NewPosition : e.Source.Position;
            }
            Rectangle startPos = GetVisibleWindowRectangle(e.Source);
            if (startPos.Width <= 0 || startPos.Height <= 0)
            {
                startPos = rawStartPos;
            }
            bool belongsToThisDisplay = IsPointOwnedByThisDisplay(cursor, out _) ||
                                        IsPointOwnedByThisDisplay(startPos.Center, out _);

            // 始终记录初始真实 raw 物理位置供尺寸形变比对（避免 Win32Window.m_position 缓存滞后导致差值误判！）
            // 同时仅在所属显示器上记录 startPos 供贴靠窗口还原记忆
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_activeDragInitialRawRect[e.Source] = rawStartPos;
                if (belongsToThisDisplay)
                {
                    m_activeDragOriginalRect[e.Source] = startPos;
                }
            }

            double scale = m_display.Scaling > 0 ? m_display.Scaling : 1.0;
            bool isBorderResize = IsResizeBorderClick(
                e.Source.Handle,
                startPos,
                rawStartPos,
                cursor,
                hasRecentDown ? recentDownPt : null,
                recentDownWasResize,
                scale,
                out bool confirmedResize);

            // 严格边框缩放拦截：无论用户从顶边、底边、左边、右边或四角哪个边框调整窗口大小，
            // 均在起点立即锁定为 RESIZING 模式，彻底杜绝调整尺寸时误触发贴边分屏！
            if (isBorderResize)
            {
                m_currentInteraction = UserInteraction.Resizing;
                m_isConfirmedResize = confirmedResize;
                m_isDraggingTitleBar = false;
                m_isDraggingSnappedWindow = false;
                m_lastActiveSnapZone = ScreenSnapZone.None;
                m_lastActiveSnapTime = TimeSpan.Zero;
                SetPreviewRectangle(null, null);
                StopDragTrackingTimer();
                m_logger.Information("[Drag] Window {Window} interaction detected as BORDER RESIZE (confirmed={Confirmed}), disabling split-screen snapping",
                    e.Source.DebugString(), confirmedResize);
                return;
            }

            m_logger.Information("[Drag] PositionChangeStart on {Window}, Pos={Pos}, Cursor={Cursor}", e.Source.DebugString(), startPos, cursor);
            if (startPos.Width > 0)
            {
                m_dragGrabRatioX = Math.Clamp((cursor.X - startPos.Left) / (double)startPos.Width, 0.1, 0.9);
            }
            else
            {
                m_dragGrabRatioX = 0.5;
            }

            if (startPos.Height > 0)
            {
                m_dragGrabRatioY = Math.Clamp((cursor.Y - startPos.Top) / (double)startPos.Height, 0.05, 0.95);
            }
            else
            {
                m_dragGrabRatioY = 0.1;
            }

            var wa = m_display.WorkArea;
            bool wasSnapped = false;
            if (belongsToThisDisplay)
            {
                wasSnapped = IsWindowSnappedGeometry(startPos, wa) || s_snappedHwnds.ContainsKey(e.Source.Handle);
                if (!wasSnapped)
                {
                    using (m_snappedWindowsLock.EnterScope())
                    {
                        if (m_snappedWindows.Contains(e.Source) && (startPos.Width >= wa.Width * 0.45 || startPos.Height >= wa.Height * 0.45))
                            wasSnapped = true;
                    }
                }
                if (!wasSnapped)
                {
                    if (e.Source.State == WindowState.Maximized ||
                        (startPos.Width >= wa.Width - 20 && startPos.Height >= wa.Height - 20))
                    {
                        wasSnapped = true;
                    }
                }
            }
            else
            {
                wasSnapped = s_snappedHwnds.ContainsKey(e.Source.Handle);
            }

            if (wasSnapped)
            {
                m_isDraggingSnappedWindow = true;
                if (belongsToThisDisplay)
                {
                    s_snappedHwnds[e.Source.Handle] = 1;
                    using (m_snappedWindowsLock.EnterScope())
                    {
                        m_snappedWindows.Add(e.Source);
                    }
                    var preSnapRect = GetBestFloatingRectangle(e.Source, wa, startPos);
                    s_hwndPreSnapMemory[e.Source.Handle] = preSnapRect;
                    using (m_preSnapRectanglesLock.EnterScope())
                    {
                        m_preSnapRectangles[e.Source] = preSnapRect;
                    }
                    m_currentDragTargetSize = new WinMan.Point(preSnapRect.Width, preSnapRect.Height);
                }
            }
            else
            {
                m_isDraggingSnappedWindow = false;
                if (belongsToThisDisplay)
                {
                    using (m_lastFloatingRectanglesLock.EnterScope())
                    {
                        if (IsValidFloatingRect(startPos, wa))
                        {
                            m_lastFloatingRectangles[e.Source] = startPos;
                        }
                    }
                    if (IsValidFloatingRect(startPos, wa))
                    {
                        RecordFloatingMemory(e.Source, startPos);
                    }
                }
                m_currentDragTargetSize = new WinMan.Point(startPos.Width, startPos.Height);
            }

            int sideMargin = Math.Max(5, (int)Math.Round(5 * scale));
            int topMargin = Math.Max(5, (int)Math.Round(5 * scale));
            int topHeaderHeight = Math.Max(60, (int)Math.Round(75 * scale));

            // 严格位于窗口顶部内侧（完整覆盖标题栏与菜单栏，排除外层边框缩放区域）
            bool inTitleBarArea = cursor.Y > rawStartPos.Top + topMargin &&
                                  cursor.Y <= rawStartPos.Top + topHeaderHeight &&
                                  cursor.X > rawStartPos.Left + sideMargin &&
                                  cursor.X < rawStartPos.Right - sideMargin;
            m_isDraggingTitleBar = inTitleBarArea;
            m_currentInteraction = UserInteraction.Moving;

            StartDragTrackingTimer();
            if (inTitleBarArea)
            {
                UpdateDragPreview(cursor);
            }
        }

        private void OnTilingNodeFocusRequested(object? sender, TilingNode e)
        {
            using (m_backendLock.EnterScope())
            {
                var windowNode = e.Windows.FirstOrDefault();
                try
                {
                    if (windowNode != null)
                    {
                        if (FocusHelper.ForceActivate(windowNode.WindowReference.Handle))
                        {
                            m_backend.SetFocus(windowNode.WindowReference);
                        }
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    return;
                }
            }
        }

        private void OnTilingNodeCloseRequested(object? sender, TilingNode e)
        {
            foreach (var window in e.Windows.ToList())
            {
                try
                {
                    if (window.WindowReference.CanClose)
                    {
                        window.WindowReference.Close();
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    // Ignore
                }
                catch (Win32Exception)
                {
                    // Ignore
                    // TODO: Show toast
                }
            }
        }

        private void BindEventHandlers(IWindow window)
        {
            window.StateChanged += OnWindowStateChanged;
            window.PositionChangeStart += OnWindowPositionChangeStart;
            window.PositionChangeEnd += OnWindowPositionChangeEnd;
            window.PositionChanged += OnWindowPositionChanged;
            window.GotFocus += OnWindowGotFocus;
            window.LostFocus += OnWindowLostFocus;
            window.TopmostChanged += OnWindowTopmostChanged;
        }

        private void UnbindEventHandlers(IWindow window)
        {
            window.StateChanged -= OnWindowStateChanged;
            window.PositionChangeStart -= OnWindowPositionChangeStart;
            window.PositionChangeEnd -= OnWindowPositionChangeEnd;
            window.PositionChanged -= OnWindowPositionChanged;
            window.GotFocus -= OnWindowGotFocus;
            window.LostFocus -= OnWindowLostFocus;
            window.TopmostChanged -= OnWindowTopmostChanged;
        }

        private bool IsSwapModifierPressed() => false;

        private bool DetectChanges(IWindow window)
        {
            m_logger.Verbose("Dirty checking for changes with window {Window}", window.DebugString());
            try
            {
                if (window.State == WindowState.Restored && CanManage(window))
                {
                    if (!AutoRegisterWindows)
                    {
                        return false;
                    }

                    try
                    {
                        using (m_backendLock.EnterScope())
                        {
                            try
                            {
                                if (!m_backend.HasWindow(window))
                                {
                                    m_logger.Debug("Window {Window} can be managed, but is not registered with backend, registering now", window.DebugString());
                                    var newNode = m_backend.RegisterWindow(window, maxTreeWidth: m_autoSplitCount);
                                    newNode.Parent!.Padding = GetPanelPaddingRect();
                                    newNode.Parent!.Spacing = GetPanelSpacing();
                                    InvalidateLayout();
                                    return true;
                                }
                            }
                            catch (InvalidWindowReferenceException)
                            {
                                if (m_backend.HasWindow(window))
                                    m_backend.UnregisterWindow(window);
                            }
                        }
                    }
                    catch (NoValidPlacementExistsException)
                    {
                        PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                            TilingError.NoValidPlacementExists, window));
                    }
                }
                else
                {
                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(window))
                        {
                            m_logger.Verbose("Window {Window} can no longer be managed, but is registered with backend, unregistering now", window.DebugString());
                            m_backend.UnregisterWindow(window);

                            InvalidateLayout();
                            return true;
                        }
                    }
                }
            }
            catch (WindowAlreadyRegisteredException)
            {
                return false;
            }
            // TODO: Is the following catch block necessary?
            catch (InvalidOperationException)
            {
                return false;
            }
            return false;
        }

        private bool CanManage(IWindow x, bool ignoreFloating = false)
        {
            bool IsOnCurrentDisplay()
            {
                var pos = x.Position.Center;
                if (m_display.Bounds.Contains(pos))
                    return true;

                // Check if on any other displays
                return !m_workspace.DisplayManager.Displays
                    .Where(d => !d.Equals(m_display) && d.Bounds.Contains(pos))
                    .Any();
            }
            bool IsFloating()
            {
                using (m_floatingSetLock.EnterScope())
                {
                    return m_floatingSet.Contains(x);
                }
            }

            // Cheap boolean read
            if (x.IsTopmost)
            {
                return false;
            }

            // Set lookup
            if (!ignoreFloating && IsFloating())
            {
                return false;
            }

            // GetWindowPos + Lookup
            if (!IsOnCurrentDisplay())
            {
                return false;
            }

            // GetWindowStyle + OpenProcess
            if (!x.CanResize)
            {
                return false;
            }

            // OpenProcess (expensive)
            if (!x.CanMove)
            {
                return false;
            }

            // Virtual Desktop stuff is very expensive
            if (m_workspace.VirtualDesktopManager.IsWindowPinned(x))
            {
                return false;
            }

            return true;
        }

        private void InvalidateLayout()
        {
            if (!m_active)
            {
                return;
            }

            m_dirty = true;
            if (m_frozen.IsPositive())
            {
                return;
            }
            m_dispatcher.InvokeAsync(new Action(() =>
            {
                if (!m_dirty || m_frozen.IsPositive())
                    return;
                m_dirty = false;
                _ = UpdateLayoutAsync();
            }), System.Windows.Threading.DispatcherPriority.DataBind);
        }

        private void Freeze()
        {
            m_frozen.Increment();
        }

        private void Unfreeze()
        {
            if (m_frozen.DecrementIfPositive())
            {
                if (m_dirty)
                {
                    InvalidateLayout();
                }
            }
        }

        private static Rectangle ShrinkTo(Rectangle container, int width, int height)
        {
            int wdiff = container.Width - width;
            int hdiff = container.Height - height;
            return new Rectangle(
                container.Left + wdiff / 2,
                container.Top + hdiff / 2,
                container.Right - wdiff / 2,
                container.Bottom - hdiff / 2
            );
        }

        private int GetPanelSpacing()
        {
            double scaling = m_display.Scaling;
            return (int)(m_windowPadding * scaling);
        }

        private Rectangle GetPanelPaddingRect()
        {
            return new Rectangle(0, 0, 0, 0);
        }

        private static System.Windows.Thickness ToThickness(Rectangle rc)
        {
            return new System.Windows.Thickness(rc.Left, rc.Top, rc.Right, rc.Bottom);
        }

        private void UpdateGuiNodeOptions()
        {
            m_dispatcher.Invoke(() =>
            {
                m_gui.PanelSpacing = GetPanelSpacing();
                m_gui.PanelPadding = ToThickness(GetPanelPaddingRect());
                m_gui.InvalidateView();
                InvalidateLayout();
            });
        }

        private void PropagatePaddingChange()
        {
            using (m_backendLock.EnterScope())
            {
                foreach (var panel in m_backend.Trees.SelectMany(x => x.Root!.Nodes).OfType<PanelNode>())
                {
                    panel.Spacing = GetPanelSpacing();
                }
            }
            UpdateGuiNodeOptions();
        }

        private void PropagatePanelHeightChange()
        {
            using (m_backendLock.EnterScope())
            {
                foreach (var panel in m_backend.Trees.SelectMany(x => x.Root!.Nodes).OfType<PanelNode>())
                {
                    panel.Padding = GetPanelPaddingRect();
                    panel.Spacing = GetPanelSpacing();
                }
            }
            UpdateGuiNodeOptions();
        }

        private void PropagateShowFocusChange()
        {
            InvalidateFocusRectangle(immediate: true);
            InvalidateLayout();
        }

        private void PropagateShowPreviewFocusChange()
        {
            InvalidateLayout();
        }

        bool HasFocusAndAdjacentWindow(TilingDirection direction)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.GetFocusAndAdjacentWindow(m_workspace.VirtualDesktopManager.CurrentDesktop, direction);
                    return true;
                }
            }
            catch (TilingFailedException e) when (e.FailReason == TilingError.MissingTarget || e.FailReason == TilingError.MissingAdjacentWindow)
            {
                return false;
            }
        }
    }
}
