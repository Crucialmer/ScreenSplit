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
                        var frame = window.WindowReference.FrameMargins;
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

        private static readonly ConcurrentDictionary<IntPtr, Rectangle> s_hwndFloatingMemory = new();
        private static readonly ConcurrentDictionary<string, (int Width, int Height)> s_processFloatingMemory = new();

        private static void RecordFloatingMemory(IWindow window, Rectangle pos)
        {
            try
            {
                IntPtr hwnd = window.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetProp(hwnd, "SS_FloatW", (IntPtr)pos.Width);
                    SetProp(hwnd, "SS_FloatH", (IntPtr)pos.Height);
                    SetProp(hwnd, "SS_FloatX", (IntPtr)pos.Left);
                    SetProp(hwnd, "SS_FloatY", (IntPtr)pos.Top);
                    s_hwndFloatingMemory[hwnd] = pos;
                }

                string procName = "";
                try { procName = window.GetProcess()?.ProcessName?.ToLowerInvariant() ?? ""; } catch { }
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
                try { procName = window.GetProcess()?.ProcessName?.ToLowerInvariant() ?? ""; } catch { }
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
            // 贴靠最边缘触发阈值：紧贴物理屏幕最外层边框（~2 像素，最大化自由浮动空间，绝不向屏幕内延伸）
            int edgeMargin = Math.Max(2, (int)Math.Round(1.5 * scale));
            // 最顶点触发阈值：仅在屏幕四个顶角极小区域（~3 像素，顶点精准触发 1/4 分屏）
            int cornerSize = Math.Max(3, (int)Math.Round(2.5 * scale));

            // 1. Four Corners (1/4 Layout) - Must be within corner threshold in both X and Y
            if (pt.X <= wa.Left + cornerSize && pt.Y <= wa.Top + cornerSize)
            {
                return ScreenSnapZone.TopLeftQuarter;
            }
            if (pt.X >= wa.Right - cornerSize && pt.Y <= wa.Top + cornerSize)
            {
                return ScreenSnapZone.TopRightQuarter;
            }
            if (pt.X <= wa.Left + cornerSize && pt.Y >= wa.Bottom - cornerSize)
            {
                return ScreenSnapZone.BottomLeftQuarter;
            }
            if (pt.X >= wa.Right - cornerSize && pt.Y >= wa.Bottom - cornerSize)
            {
                return ScreenSnapZone.BottomRightQuarter;
            }

            // 2. Four Edges (1/2 Layout) - Triggered when near screen edge but outside corner hotzone
            if (pt.X <= wa.Left + edgeMargin)
            {
                return ScreenSnapZone.LeftHalf;
            }
            if (pt.X >= wa.Right - edgeMargin)
            {
                return ScreenSnapZone.RightHalf;
            }
            if (pt.Y <= wa.Top + edgeMargin)
            {
                return ScreenSnapZone.TopHalf;
            }
            if (pt.Y >= wa.Bottom - edgeMargin)
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

        private void ApplyWindowSnap(IWindow window, Rectangle targetRect)
        {
            try
            {
                var frame = window.FrameMargins;
                var adjustedRect = new Rectangle(
                    left: targetRect.Left - frame.Left,
                    top: targetRect.Top - frame.Top,
                    right: targetRect.Right + frame.Right,
                    bottom: targetRect.Bottom + frame.Bottom);

                if (window.State != WindowState.Restored)
                {
                    window.SetState(WindowState.Restored);
                }
                window.SetPosition(adjustedRect);
            }
            catch (Exception ex)
            {
                m_logger.Warning(ex, "Failed to apply window snap for {Window}", window.DebugString());
            }
        }

        private static bool IsResizeBorderClick(Rectangle pos, WinMan.Point cursor, double scale)
        {
            if (pos.Width <= 0 || pos.Height <= 0) return false;
            int margin = Math.Max(8, (int)Math.Round(8 * scale));
            int topMargin = Math.Max(6, (int)Math.Round(6 * scale));

            // Bottom border or bottom corners
            if (Math.Abs(cursor.Y - pos.Bottom) <= margin)
                return true;

            // Top border or top corners
            if (Math.Abs(cursor.Y - pos.Top) <= topMargin)
                return true;

            // Left border or left corners
            if (Math.Abs(cursor.X - pos.Left) <= margin)
                return true;

            // Right border or right corners
            if (Math.Abs(cursor.X - pos.Right) <= margin)
                return true;

            return false;
        }

        private (Rectangle? Rect, string? Title) GetPreviewInfo() => GetPreviewInfo(m_workspace.CursorLocation);

        private (Rectangle? Rect, string? Title) GetPreviewInfo(Point pt)
        {
            // 绝对核心前提：必须正在物理按住鼠标按键！若未按住鼠标按键，绝对不属于拖动窗口，绝不显示分屏阴影！
            bool isMouseButtonDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0 || (PInvoke.GetAsyncKeyState(2) & 0x8000) != 0;
            if (!isMouseButtonDown)
            {
                if (m_movingWindow != null || m_currentInteraction != UserInteraction.None)
                {
                    m_movingWindow = null;
                    m_currentInteraction = UserInteraction.None;
                    m_isDraggingTitleBar = false;
                    m_isDraggingSnappedWindow = false;
                    m_isExternalDrag = false;
                    StopDragTrackingTimer();
                    SetPreviewRectangle(null, null);
                }
                return (null, null);
            }

            // 严格切割：窗口放大/缩小（Resizing）时绝对不触发分屏预览
            if (m_currentInteraction == UserInteraction.Resizing)
            {
                return (null, null);
            }

            // 必须正在进行窗口移动，且当前有被移动窗口；或者正在拖动 panel
            if ((m_currentInteraction == UserInteraction.Moving && m_movingWindow != null) || m_movingPanelNode != null)
            {
                try
                {
                    var bounds = m_display.Bounds;
                    var testPt = pt;
                    // 仅当光标在屏幕边界外微小偏移时，校准回屏幕边界内
                    if (pt.X < bounds.Left) testPt = new Point(bounds.Left, testPt.Y);
                    if (pt.X > bounds.Right) testPt = new Point(bounds.Right, testPt.Y);
                    if (pt.Y < bounds.Top) testPt = new Point(testPt.X, bounds.Top);
                    if (pt.Y > bounds.Bottom) testPt = new Point(testPt.X, bounds.Bottom);

                    if (bounds.Contains(testPt))
                    {
                        var wa = m_display.WorkArea;
                        if (m_movingPanelNode == null)
                        {
                            var snapZone = GetSnapZone(testPt, wa);
                            if (snapZone != ScreenSnapZone.None)
                            {
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
            m_logger.Information("[Drag] Preview Rect={Rect}, Title={Title}", preview.Rect, preview.Title);
            SetPreviewRectangle(preview.Rect, preview.Title);
            m_gui.EnsureTopmost();
        }

        private Point m_lastPreviewCursor = new Point(-99999, -99999);

        private void OnDragTrackingTimerTick(object? sender, EventArgs e)
        {
            if (!m_active || m_movingWindow == null)
            {
                StopDragTrackingTimer();
                return;
            }

            var pt = m_workspace.CursorLocation;

            // 实时检测鼠标主按键是否松开（通过直接检测物理按键状态，支持左右手鼠标配置）
            if (!m_isExternalDrag && (PInvoke.GetAsyncKeyState(1) & 0x8000) == 0 && (PInvoke.GetAsyncKeyState(2) & 0x8000) == 0)
            {
                StopDragTrackingTimer();
                if (m_movingWindow != null)
                {
                    var win = m_movingWindow;
                    m_movingWindow = null;
                    var interaction = m_currentInteraction;
                    m_currentInteraction = UserInteraction.None;
                    m_lastPreviewCursor = new Point(-99999, -99999);
                    SetPreviewRectangle(null, null);
                    if (interaction == UserInteraction.Resizing)
                    {
                        var finalPos = GetVisibleWindowRectangle(win);
                        if (finalPos.Width <= 0 || finalPos.Height <= 0) finalPos = win.Position;
                        var wa = m_display.WorkArea;
                        if (IsValidFloatingRect(finalPos, wa))
                        {
                            using (m_lastFloatingRectanglesLock.EnterScope())
                            {
                                m_lastFloatingRectangles[win] = finalPos;
                            }
                            RecordFloatingMemory(win, finalPos);
                        }
                    }
                    else
                    {
                        DoWindowMove(win);
                    }
                }
                return;
            }

            // 外部拖动时，实时检测鼠标左键是否已经松开
            if (m_isExternalDrag && (PInvoke.GetAsyncKeyState(1) & 0x8000) == 0)
            {
                EndExternalDrag();
                return;
            }

            if (pt == m_lastPreviewCursor)
            {
                return;
            }
            m_lastPreviewCursor = pt;

            UpdateDragPreview(pt);
        }

        private void OnExternalDragMouseButtonStateChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
        {
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

        private void StartExternalDrag(IWindow window, Rectangle pos, Rectangle initialPos, Point cursor)
        {
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
                             (initialPos.Width > 0 && IsWindowSnappedGeometry(initialPos, wa));
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
                var preSnapRect = GetBestFloatingRectangle(window, wa, pos);
                m_currentDragTargetSize = new Point(preSnapRect.Width, preSnapRect.Height);

                // ⭐ 核心优化：在检测到窗口从边缘拖出时，立即实时恢复其实际窗口大小！
                // 就像 Windows 原生 Aero Snap 一样，当拖动脱离边缘时，窗口立即在物理层面缩小为原始浮动尺寸，并跟随鼠标！
                // 这彻底消除了 Quicker 的 SWP_NOSIZE 导致窗口依然巨大并在松手时产生竞态条件的 Bug！
                int targetW = preSnapRect.Width;
                int targetH = preSnapRect.Height;
                int targetX = cursor.X - (int)(targetW * m_dragGrabRatioX);
                int targetY = cursor.Y - (int)(targetH * m_dragGrabRatioY);

                if (targetX < wa.Left) targetX = wa.Left;
                if (targetX + targetW > wa.Right) targetX = wa.Right - targetW;
                if (targetY < wa.Top) targetY = wa.Top;
                if (targetY + targetH > wa.Bottom) targetY = wa.Bottom - targetH;

                var realFloatRect = Rectangle.OffsetAndSize(targetX, targetY, targetW, targetH);
                ApplyWindowSnap(window, realFloatRect);
                PInvoke.SetWindowPos(
                    new(window.Handle),
                    new(),
                    realFloatRect.Left, realFloatRect.Top, realFloatRect.Width, realFloatRect.Height,
                    SetWindowPos_uFlags.SWP_NOZORDER | SetWindowPos_uFlags.SWP_NOACTIVATE);

                // 保持拖出标记与原始大小记录，供后续自由释放或重新贴靠使用
                using (m_snappedWindowsLock.EnterScope())
                {
                    m_snappedWindows.Remove(window);
                }
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

            StartDragTrackingTimer();
            m_externalDragWatchdogTimer.Start();
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

            m_externalDragWatchdogTimer.Stop();
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
                m_isDraggingTitleBar = false;
                m_isDraggingSnappedWindow = false;

                m_dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(100);
                    using (m_ignoreRepositionSetLock.EnterScope())
                    {
                        m_ignoreRepositionSet.Remove(win);
                    }
                });
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
            // 鼠标未按住时绝对不触发分屏预览，并清理残留状态
            bool isMouseButtonDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0 || (PInvoke.GetAsyncKeyState(2) & 0x8000) != 0;
            if (!isMouseButtonDown)
            {
                if (m_movingWindow != null || m_currentInteraction != UserInteraction.None)
                {
                    m_movingWindow = null;
                    m_currentInteraction = UserInteraction.None;
                    m_isDraggingTitleBar = false;
                    m_isDraggingSnappedWindow = false;
                    m_isExternalDrag = false;
                    StopDragTrackingTimer();
                    SetPreviewRectangle(null, null);
                }
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
            m_currentInteraction = UserInteraction.None;
            if (m_showFocus)
            {
                InvalidateFocusRectangle(immediate: false);
            }
        }

        private void OnWindowAdded(object? sender, WindowChangedEventArgs e)
        {
            m_logger.Debug("Window {Window} added to workspace", e.Source.DebugString());
            try
            {
                BindEventHandlers(e.Source);
                using (m_windowSetLock.EnterScope())
                {
                    m_windowSet.Add(e.Source);
                }
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
            if (Equals(m_movingWindow, e.Source))
            {
                m_movingWindow = null;
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
            var pt = m_workspace.CursorLocation;
            var bounds = m_display.Bounds;
            var testPt = pt;
            if (pt.X < bounds.Left) testPt = new Point(bounds.Left, testPt.Y);
            if (pt.X > bounds.Right) testPt = new Point(bounds.Right, testPt.Y);
            if (pt.Y < bounds.Top) testPt = new Point(testPt.X, bounds.Top);
            if (pt.Y > bounds.Bottom) testPt = new Point(testPt.X, bounds.Bottom);
            var wa = m_display.WorkArea;
            var snapZone = bounds.Contains(testPt) ? GetSnapZone(testPt, wa) : ScreenSnapZone.None;

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
                            IsValidFloatingRect(origDragRect, wa))
                        {
                            m_preSnapRectangles[window] = origDragRect;
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
                            var bestFloat = GetBestFloatingRectangle(window, wa, GetVisibleWindowRectangle(window));
                            m_preSnapRectangles[window] = bestFloat;
                            using (m_lastFloatingRectanglesLock.EnterScope())
                            {
                                m_lastFloatingRectangles[window] = bestFloat;
                            }
                            RecordFloatingMemory(window, bestFloat);
                        }
                    }

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

                    ApplyWindowSnap(window, targetRect.Value);
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

            using (m_snappedWindowsLock.EnterScope())
            {
                m_snappedWindows.Remove(window);
            }

            if (!hadPreSnap && m_isDraggingSnappedWindow)
            {
                preSnapRect = GetBestFloatingRectangle(window, wa, GetVisibleWindowRectangle(window));
                hadPreSnap = true;
            }

            if (hadPreSnap && preSnapRect.Width > 0 && preSnapRect.Height > 0)
            {
                m_logger.Information("Window {Window} dragged away from edge/corner, restoring pre-snap rectangle {Rect}",
                    window.DebugString(), preSnapRect);

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

                int targetX = pt.X - (int)(targetW * m_dragGrabRatioX);
                int targetY = pt.Y - (int)(targetH * m_dragGrabRatioY);

                if (targetX < wa.Left) targetX = wa.Left;
                if (targetX + targetW > wa.Right) targetX = wa.Right - targetW;
                if (targetY < wa.Top) targetY = wa.Top;
                if (targetY + targetH > wa.Bottom) targetY = wa.Bottom - targetH;

                var restoredRect = Rectangle.OffsetAndSize(targetX, targetY, targetW, targetH);
                ApplyWindowSnap(window, restoredRect);
                PInvoke.SetWindowPos(
                    new(window.Handle),
                    new(),
                    restoredRect.Left, restoredRect.Top, restoredRect.Width, restoredRect.Height,
                    SetWindowPos_uFlags.SWP_NOZORDER | SetWindowPos_uFlags.SWP_NOACTIVATE);

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

                // ⭐ 核心防覆写 Watchdog：延迟 40ms 确保 Quicker 等外部工具在松手退出循环时的残余位置更新不会覆盖恢复后的尺寸！
                var capturedRestoredRect = restoredRect;
                m_dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(40);
                    try
                    {
                        var curRect = GetVisibleWindowRectangle(window);
                        if (Math.Abs(curRect.Width - capturedRestoredRect.Width) > 30 ||
                            Math.Abs(curRect.Height - capturedRestoredRect.Height) > 30)
                        {
                            ApplyWindowSnap(window, capturedRestoredRect);
                            PInvoke.SetWindowPos(
                                new(window.Handle),
                                new(),
                                capturedRestoredRect.Left, capturedRestoredRect.Top, capturedRestoredRect.Width, capturedRestoredRect.Height,
                                SetWindowPos_uFlags.SWP_NOZORDER | SetWindowPos_uFlags.SWP_NOACTIVATE);
                        }
                    }
                    catch { }
                });
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

            StopDragTrackingTimer();
            m_lastPreviewCursor = new Point(-99999, -99999);
            SetPreviewRectangle(null, null);

            var win = m_movingWindow;
            m_movingWindow = null;
            bool wasDraggingSnapped = m_isDraggingSnappedWindow;
            bool wasDraggingTitleBar = m_isDraggingTitleBar;
            var interaction = m_currentInteraction;
            m_currentInteraction = UserInteraction.None;
            m_isDraggingTitleBar = false;
            m_isDraggingSnappedWindow = false;

            if (win != null && interaction != UserInteraction.Resizing && (interaction == UserInteraction.Moving || interaction == UserInteraction.Starting))
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
                if (IsValidFloatingRect(finalPos, wa))
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
            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Remove(e.Source);
            }
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

            double refreshRate = m_display.RefreshRate > 0 ? m_display.RefreshRate : 60.0;
            if (m_currentInteraction != UserInteraction.None && m_sw.Elapsed - m_lastWindowPositionChanged <= TimeSpan.FromSeconds(1.0 / (refreshRate * 2.0)))
            {
                return;
            }
            m_lastWindowPositionChanged = m_sw.Elapsed;

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                if (!m_ignoreRepositionSet.Contains(e.Source))
                {
                    // Check if this could be an external drag (e.g. Quicker / SetWindowPos mover)
                    // 仅当用户正在物理按住鼠标左键的状态下，才进行外部拖动检测与触发
                    bool isLeftButtonDown = (PInvoke.GetAsyncKeyState(1) & 0x8000) != 0;
                    if (m_currentInteraction == UserInteraction.None && m_movingWindow == null && isLeftButtonDown)
                    {
                        var cursor = m_workspace.CursorLocation;
                        bool isSizePreservedExternal = Math.Abs(e.OldPosition.Width - e.NewPosition.Width) <= 10 &&
                                                       Math.Abs(e.OldPosition.Height - e.NewPosition.Height) <= 10;
                        int winDx = e.NewPosition.Left - e.OldPosition.Left;
                        int winDy = e.NewPosition.Top - e.OldPosition.Top;
                        bool isPositionChanged = (winDx != 0 || winDy != 0);

                        // Cursor is inside or very close to the moving window
                        bool isCursorInsideWindow = cursor.X >= e.NewPosition.Left - 80 &&
                                                    cursor.X <= e.NewPosition.Right + 80 &&
                                                    cursor.Y >= e.NewPosition.Top - 80 &&
                                                    cursor.Y <= e.NewPosition.Bottom + 80;

                        if (isSizePreservedExternal && isPositionChanged && isCursorInsideWindow)
                        {
                            if (Equals(m_potentialExternalDragWindow, e.Source) && (m_sw.Elapsed - m_lastExternalDragTime) <= TimeSpan.FromMilliseconds(250))
                            {
                                m_externalDragFrameCount++;
                                m_lastExternalDragTime = m_sw.Elapsed;

                                if (m_externalDragFrameCount >= 2)
                                {
                                    // Confirmed external drag!
                                    StartExternalDrag(e.Source, e.NewPosition, m_potentialExternalDragInitialPos, cursor);
                                    return;
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
                    }
                    else if (!isLeftButtonDown)
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

            if (m_isExternalDrag && Equals(m_movingWindow, e.Source))
            {
                m_lastExternalDragTime = m_sw.Elapsed;
            }

            // 实时记录正常浮动窗口的尺寸与位置，防止窗口在各种方式贴靠后丢失记忆
            var curWa = m_display.WorkArea;
            var curPos = e.NewPosition;
            if (IsValidFloatingRect(curPos, curWa) &&
                !IsWindowSnapped(e.Source, curPos, curWa) &&
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
                SetPreviewRectangle(null, null);
                return;
            }

            Rectangle rawStartRect = default;
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_activeDragInitialRawRect.TryGetValue(e.Source, out rawStartRect);
            }

            int dW = rawStartRect.Width > 0 ? Math.Abs(e.NewPosition.Width - rawStartRect.Width) : 0;
            int dH = rawStartRect.Height > 0 ? Math.Abs(e.NewPosition.Height - rawStartRect.Height) : 0;

            // 尺寸形变判定：只要不是贴靠窗口拖拽还原、也不是外部拖动，一旦窗口尺寸发生形变（大于 8 像素），确认为用户正在手动缩放边框（放大/缩小）
            bool isSizeChanged = !m_isDraggingSnappedWindow && !m_isExternalDrag && (dW > 8 || dH > 8);
            if (isSizeChanged)
            {
                m_currentInteraction = UserInteraction.Resizing;
                m_isDraggingTitleBar = false;
                StopDragTrackingTimer();
                m_lastPreviewCursor = new Point(-99999, -99999);
                SetPreviewRectangle(null, null);
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_preSnapRectangles.Remove(e.Source);
                    m_activeDragOriginalRect.Remove(e.Source);
                    m_activeDragInitialRawRect.Remove(e.Source);
                }
                using (m_snappedWindowsLock.EnterScope())
                {
                    m_snappedWindows.Remove(e.Source);
                }
                m_logger.Information("Window {Window} size changed (dW={DW}, dH={DH}), switching to RESIZING mode, disabling split screen",
                    e.Source.DebugString(), dW, dH);
                return;
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
                    UpdateDragPreview(m_workspace.CursorLocation);
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

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Add(e.Source);
            }
            m_movingWindow = e.Source;
            m_currentInteraction = UserInteraction.Moving;

            var cursor = m_workspace.CursorLocation;
            Rectangle rawStartPos = e.NewPosition.Width > 0 ? e.NewPosition : e.Source.Position;
            Rectangle startPos = GetVisibleWindowRectangle(e.Source);
            if (startPos.Width <= 0 || startPos.Height <= 0)
            {
                startPos = rawStartPos;
            }

            // 始终记录初始 raw 物理位置供尺寸形变比对（与 e.NewPosition 处于完全相同的坐标系，绝无 DWM 边框差值！）
            // 同时记录 startPos 供贴靠窗口还原记忆
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_activeDragInitialRawRect[e.Source] = rawStartPos;
                m_activeDragOriginalRect[e.Source] = startPos;
            }

            double scale = m_display.Scaling > 0 ? m_display.Scaling : 1.0;
            bool isBorderResize = IsResizeBorderClick(rawStartPos, cursor, scale);

            // 仅当用户明确点击窗口底部边框或侧边底部缩放手柄时，才在起点拦截；
            // 拖动顶部标题栏/菜单栏时绝对不拦截，保证顺畅分屏！
            if (isBorderResize)
            {
                m_currentInteraction = UserInteraction.Resizing;
                m_isDraggingTitleBar = false;
                m_isDraggingSnappedWindow = false;
                SetPreviewRectangle(null, null);
                StopDragTrackingTimer();
                m_logger.Information("[Drag] Window {Window} interaction detected as BORDER RESIZE, disabling split-screen snapping",
                    e.Source.DebugString());
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
            bool wasSnapped = IsWindowSnappedGeometry(startPos, wa);
            if (!wasSnapped)
            {
                using (m_snappedWindowsLock.EnterScope())
                {
                    if (m_snappedWindows.Contains(e.Source) && (startPos.Width >= wa.Width * 0.45 || startPos.Height >= wa.Height * 0.45))
                        wasSnapped = true;
                }
            }

            if (wasSnapped)
            {
                m_isDraggingSnappedWindow = true;
                using (m_snappedWindowsLock.EnterScope())
                {
                    m_snappedWindows.Add(e.Source);
                }
                var preSnapRect = GetBestFloatingRectangle(e.Source, wa, startPos);
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_preSnapRectangles[e.Source] = preSnapRect;
                }
                m_currentDragTargetSize = new WinMan.Point(preSnapRect.Width, preSnapRect.Height);
            }
            else
            {
                m_isDraggingSnappedWindow = false;
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
                m_currentDragTargetSize = new WinMan.Point(startPos.Width, startPos.Height);
            }

            int borderMargin = Math.Max(8, (int)Math.Round(8 * scale));
            int topHeaderHeight = Math.Max(40, (int)Math.Round(45 * scale));

            // 严格位于窗口顶部内侧（距离上、左、右各边框均保留安全间距，完全排除四周边框缩放区域）
            bool inTitleBarArea = cursor.Y > rawStartPos.Top + borderMargin &&
                                  cursor.Y <= rawStartPos.Top + topHeaderHeight &&
                                  cursor.X > rawStartPos.Left + borderMargin &&
                                  cursor.X < rawStartPos.Right - borderMargin;
            m_isDraggingTitleBar = inTitleBarArea;
            m_currentInteraction = UserInteraction.Moving;

            StartDragTrackingTimer();
            UpdateDragPreview(cursor);
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
                container.Height - wdiff / 2
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
