using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

            if (m_currentInteraction != UserInteraction.None && m_sw.Elapsed - m_lastUpdateLayout <= TimeSpan.FromSeconds(1.0 / m_display.RefreshRate))
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

            m_gui.FocusRectangle = GetFocusRectangle(focusedNode);

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

                m_gui.FocusRectangle = focusRectangle;
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
            return m_showFocus && m_currentInteraction == UserInteraction.None && m_movingPanelNode == null && m_movingWindow == null;
        }

        private void UpdateFocusRectangle()
        {
            if (!m_dispatcher.CheckAccess())
            {
                m_dispatcher.InvokeAsync(UpdateFocusRectangle, System.Windows.Threading.DispatcherPriority.DataBind);
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
            using (m_snappedWindowsLock.EnterScope())
            {
                if (m_snappedWindows.Contains(window))
                    return true;
            }
            return IsWindowSnappedGeometry(pos, wa);
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

            // Maximized
            if (Math.Abs(pos.Left - wa.Left) <= tol &&
                Math.Abs(pos.Top - wa.Top) <= tol &&
                Math.Abs(pos.Width - wa.Width) <= tol &&
                Math.Abs(pos.Height - wa.Height) <= tol)
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
            int edgeMargin = 25;
            int cornerSizeX = Math.Max(120, (int)(wa.Width * 0.20));
            int cornerSizeY = Math.Max(100, (int)(wa.Height * 0.20));

            bool isNearLeft = pt.X <= wa.Left + edgeMargin;
            bool isNearRight = pt.X >= wa.Right - edgeMargin;
            bool isNearTop = pt.Y <= wa.Top + edgeMargin;
            bool isNearBottom = pt.Y >= wa.Bottom - edgeMargin;

            // 1. Top-Left Corner (1/4)
            if ((isNearLeft && pt.Y <= wa.Top + cornerSizeY) ||
                (isNearTop && pt.X <= wa.Left + cornerSizeX))
            {
                return ScreenSnapZone.TopLeftQuarter;
            }

            // 2. Top-Right Corner (1/4)
            if ((isNearRight && pt.Y <= wa.Top + cornerSizeY) ||
                (isNearTop && pt.X >= wa.Right - cornerSizeX))
            {
                return ScreenSnapZone.TopRightQuarter;
            }

            // 3. Bottom-Left Corner (1/4)
            if ((isNearLeft && pt.Y >= wa.Bottom - cornerSizeY) ||
                (isNearBottom && pt.X <= wa.Left + cornerSizeX))
            {
                return ScreenSnapZone.BottomLeftQuarter;
            }

            // 4. Bottom-Right Corner (1/4)
            if ((isNearRight && pt.Y >= wa.Bottom - cornerSizeY) ||
                (isNearBottom && pt.X >= wa.Right - cornerSizeX))
            {
                return ScreenSnapZone.BottomRightQuarter;
            }

            // 5. Four Edges (1/2)
            if (isNearLeft)
            {
                return ScreenSnapZone.LeftHalf;
            }
            if (isNearRight)
            {
                return ScreenSnapZone.RightHalf;
            }
            if (isNearTop)
            {
                return ScreenSnapZone.TopHalf;
            }
            if (isNearBottom)
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

        private (Rectangle? Rect, string? Title) GetPreviewInfo() => GetPreviewInfo(m_workspace.CursorLocation);

        private (Rectangle? Rect, string? Title) GetPreviewInfo(Point pt)
        {
            if (m_currentInteraction == UserInteraction.Moving || m_movingWindow != null || m_movingPanelNode != null)
            {
                try
                {
                    if (m_display.Bounds.Contains(pt))
                    {
                        var wa = m_display.WorkArea;
                        if (m_movingPanelNode == null)
                        {
                            var snapZone = GetSnapZone(pt, wa);
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

                            // 自由区：仅当从边缘/角落贴靠状态拖回自由区时，展示原始尺寸阴影框跟随光标；普通自由拖动时不显示任何阴影框
                            if (m_movingWindow != null && m_isDraggingSnappedWindow)
                            {
                                int targetW = m_currentDragTargetSize.X;
                                int targetH = m_currentDragTargetSize.Y;

                                if (targetW <= 0 || targetH <= 0 || (targetW >= wa.Width && targetH >= wa.Height))
                                {
                                    bool isPortrait = m_currentDragTargetSize.X > 0 && m_currentDragTargetSize.Y > 0 && m_currentDragTargetSize.X < m_currentDragTargetSize.Y;
                                    targetW = isPortrait ? (int)(wa.Height * 0.45) : (int)(wa.Width * 0.60);
                                    targetH = isPortrait ? (int)(wa.Height * 0.75) : (int)(wa.Height * 0.65);
                                }
                                targetW = Math.Clamp(targetW, 100, (int)(wa.Width * 0.98));
                                targetH = Math.Clamp(targetH, 80, (int)(wa.Height * 0.98));

                                int targetX = pt.X - (int)(targetW * m_dragGrabRatioX);
                                int targetY = pt.Y - 20;

                                if (targetX < wa.Left) targetX = wa.Left;
                                if (targetX + targetW > wa.Right) targetX = wa.Right - targetW;
                                if (targetY < wa.Top) targetY = wa.Top;
                                if (targetY + targetH > wa.Bottom) targetY = wa.Bottom - targetH;

                                return (Rectangle.OffsetAndSize(targetX, targetY, targetW, targetH), "还原原始尺寸");
                            }
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
            if ((PInvoke.GetAsyncKeyState(1) & 0x8000) == 0 && (PInvoke.GetAsyncKeyState(2) & 0x8000) == 0)
            {
                StopDragTrackingTimer();
                if (m_movingWindow != null)
                {
                    var win = m_movingWindow;
                    m_movingWindow = null;
                    m_currentInteraction = UserInteraction.None;
                    m_lastPreviewCursor = new Point(-99999, -99999);
                    SetPreviewRectangle(null, null);
                    DoWindowMove(win);
                }
                return;
            }

            if (pt == m_lastPreviewCursor)
            {
                return;
            }
            m_lastPreviewCursor = pt;

            UpdateDragPreview(pt);
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
            if (m_currentInteraction == UserInteraction.Moving || m_movingWindow != null)
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

            m_dispatcher.InvokeAsync(() =>
            {
                if (m_showFocus)
                {
                    UpdateFocusRectangle();
                }
            }, System.Windows.Threading.DispatcherPriority.DataBind);
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
                        }
                        else
                        {
                            m_logger.Debug("Window {Window} is not managed by backend", e.Source.DebugString());
                            if (m_showFocus)
                            {
                                UpdateFocusRectangle();
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
                m_dispatcher.BeginInvoke(UpdateFocusRectangle);
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
            var wa = m_display.WorkArea;
            var snapZone = m_display.Bounds.Contains(pt) ? GetSnapZone(pt, wa) : ScreenSnapZone.None;

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
                        if (m_activeDragOriginalRect.TryGetValue(window, out var origDragRect))
                        {
                            m_preSnapRectangles[window] = origDragRect;
                            m_activeDragOriginalRect.Remove(window);
                        }
                        else if (!m_preSnapRectangles.ContainsKey(window))
                        {
                            var currentPos = GetVisibleWindowRectangle(window);
                            if (currentPos.Width > 0 && currentPos.Height > 0 &&
                                (currentPos.Width < wa.Width * 0.95 || currentPos.Height < wa.Height * 0.95) &&
                                !IsWindowSnapped(window, currentPos, wa))
                            {
                                m_preSnapRectangles[window] = currentPos;
                            }
                            else
                            {
                                bool isPortrait = currentPos.Width > 0 && currentPos.Height > 0 && currentPos.Width < currentPos.Height;
                                int defaultW = isPortrait ? (int)(wa.Height * 0.45) : (int)(wa.Width * 0.60);
                                int defaultH = isPortrait ? (int)(wa.Height * 0.75) : (int)(wa.Height * 0.65);
                                m_preSnapRectangles[window] = Rectangle.OffsetAndSize(
                                    wa.Left + (wa.Width - defaultW) / 2,
                                    wa.Top + (wa.Height - defaultH) / 2,
                                    defaultW, defaultH);
                            }
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
            }

            using (m_snappedWindowsLock.EnterScope())
            {
                m_snappedWindows.Remove(window);
            }

            if (!hadPreSnap && m_isDraggingSnappedWindow)
            {
                var curPos = GetVisibleWindowRectangle(window);
                bool isPortrait = curPos.Width > 0 && curPos.Height > 0 && curPos.Width < curPos.Height;
                int defaultW = isPortrait ? (int)(wa.Height * 0.45) : (int)(wa.Width * 0.60);
                int defaultH = isPortrait ? (int)(wa.Height * 0.75) : (int)(wa.Height * 0.65);
                preSnapRect = Rectangle.OffsetAndSize(
                    wa.Left + (wa.Width - defaultW) / 2,
                    wa.Top + (wa.Height - defaultH) / 2,
                    defaultW, defaultH);
                hadPreSnap = true;
            }

            if (hadPreSnap && preSnapRect.Width > 0 && preSnapRect.Height > 0)
            {
                m_logger.Information("Window {Window} dragged away from edge/corner, restoring pre-snap rectangle {Rect}",
                    window.DebugString(), preSnapRect);

                int targetW = preSnapRect.Width;
                int targetH = preSnapRect.Height;

                if (targetW >= wa.Width && targetH >= wa.Height)
                {
                    bool isPortrait = preSnapRect.Width < preSnapRect.Height;
                    targetW = isPortrait ? (int)(wa.Height * 0.45) : (int)(wa.Width * 0.60);
                    targetH = isPortrait ? (int)(wa.Height * 0.75) : (int)(wa.Height * 0.65);
                }
                targetW = Math.Clamp(targetW, 100, (int)(wa.Width * 0.98));
                targetH = Math.Clamp(targetH, 80, (int)(wa.Height * 0.98));

                int targetX = pt.X - (int)(targetW * m_dragGrabRatioX);
                int targetY = pt.Y - 20;

                if (targetX < wa.Left) targetX = wa.Left;
                if (targetX + targetW > wa.Right) targetX = wa.Right - targetW;
                if (targetY < wa.Top) targetY = wa.Top;
                if (targetY + targetH > wa.Bottom) targetY = wa.Bottom - targetH;

                var restoredRect = Rectangle.OffsetAndSize(targetX, targetY, targetW, targetH);
                ApplyWindowSnap(window, restoredRect);
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

            if (win != null && m_currentInteraction == UserInteraction.Moving)
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
                finally
                {
                    m_currentInteraction = UserInteraction.None;
                    m_isDraggingTitleBar = false;
                    m_isDraggingSnappedWindow = false;
                }
            }
            else
            {
                m_currentInteraction = UserInteraction.None;
                m_isDraggingTitleBar = false;
                m_isDraggingSnappedWindow = false;
            }

            m_logger.Information("Window {Window} move ended", e.Source.DebugString());
            InvalidateLayout();
            if (m_showFocus)
            {
                UpdateFocusRectangle();
            }
            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Remove(e.Source);
            }
            using (m_preSnapRectanglesLock.EnterScope())
            {
                m_activeDragOriginalRect.Remove(e.Source);
            }
            m_currentInteraction = UserInteraction.None;
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

            if (m_currentInteraction != UserInteraction.None && m_sw.Elapsed - m_lastWindowPositionChanged <= TimeSpan.FromSeconds(1.0 / m_display.RefreshRate))
            {
                return;
            }
            m_lastWindowPositionChanged = m_sw.Elapsed;

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                if (!m_ignoreRepositionSet.Contains(e.Source))
                {
                    // Some other event might have resulted in the movement of the window.
                    // Do not call DetectChanges under the lock, to avoid deadlock.
                    m_dispatcher.InvokeAsync(() =>
                    {
                        DetectChanges(e.Source);
                        if (m_showFocus && Equals(m_workspace.FocusedWindow, e.Source))
                        {
                            UpdateFocusRectangle();
                        }
                    });
                    return;
                }
            }

            bool isSizePreserved = Math.Abs(e.OldPosition.Width - e.NewPosition.Width) <= 5 &&
                                   Math.Abs(e.OldPosition.Height - e.NewPosition.Height) <= 5;

            if (m_currentInteraction == UserInteraction.Starting || m_currentInteraction == UserInteraction.Moving)
            {
                // Only consider it a manual border resize if not dragging a snapped window and not dragging by title bar
                if (!m_isDraggingSnappedWindow && !m_isDraggingTitleBar && !isSizePreserved)
                {
                    m_currentInteraction = UserInteraction.Resizing;
                    StopDragTrackingTimer();
                    m_lastPreviewCursor = new Point(-99999, -99999);
                    SetPreviewRectangle(null, null);
                    using (m_preSnapRectanglesLock.EnterScope())
                    {
                        m_preSnapRectangles.Remove(e.Source);
                        m_activeDragOriginalRect.Remove(e.Source);
                    }
                    using (m_snappedWindowsLock.EnterScope())
                    {
                        m_snappedWindows.Remove(e.Source);
                    }
                }
                else
                {
                    m_currentInteraction = UserInteraction.Moving;
                }
            }

            try
            {
                DetectChanges(e.Source);

                if (e.NewPosition == e.OldPosition)
                {
                    return;
                }

                if (m_currentInteraction == UserInteraction.Moving || m_movingWindow != null)
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

            var cursor = m_workspace.CursorLocation;
            Rectangle startPos = GetVisibleWindowRectangle(e.Source);
            if (startPos.Width <= 0 || startPos.Height <= 0)
            {
                startPos = e.OldPosition.Width > 0 ? e.OldPosition : e.Source.Position;
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

            var wa = m_display.WorkArea;
            bool wasSnapped = IsWindowSnapped(e.Source, startPos, wa);
            Rectangle preSnapRect = default;
            bool hasPreSnap = false;

            using (m_preSnapRectanglesLock.EnterScope())
            {
                hasPreSnap = m_preSnapRectangles.TryGetValue(e.Source, out preSnapRect);
            }

            if (wasSnapped)
            {
                m_isDraggingSnappedWindow = true;
                if (!hasPreSnap || preSnapRect.Width <= 0 || preSnapRect.Height <= 0)
                {
                    bool isPortrait = startPos.Width > 0 && startPos.Height > 0 && startPos.Width < startPos.Height;
                    int defaultW = isPortrait ? (int)(wa.Height * 0.45) : (int)(wa.Width * 0.60);
                    int defaultH = isPortrait ? (int)(wa.Height * 0.75) : (int)(wa.Height * 0.65);
                    preSnapRect = Rectangle.OffsetAndSize(
                        wa.Left + (wa.Width - defaultW) / 2,
                        wa.Top + (wa.Height - defaultH) / 2,
                        defaultW, defaultH);
                    using (m_preSnapRectanglesLock.EnterScope())
                    {
                        m_preSnapRectangles[e.Source] = preSnapRect;
                    }
                }
                m_currentDragTargetSize = new WinMan.Point(preSnapRect.Width, preSnapRect.Height);
            }
            else
            {
                m_isDraggingSnappedWindow = false;
                using (m_preSnapRectanglesLock.EnterScope())
                {
                    m_preSnapRectangles.Remove(e.Source);
                    m_activeDragOriginalRect[e.Source] = startPos;
                }
                m_currentDragTargetSize = new WinMan.Point(startPos.Width, startPos.Height);
            }

            int topHeaderHeight = Math.Max(40, (int)(startPos.Height * 0.25));
            bool inTitleBarArea = cursor.Y >= startPos.Top - 15 && cursor.Y <= startPos.Top + topHeaderHeight &&
                                  cursor.X >= startPos.Left - 10 && cursor.X <= startPos.Right + 10;
            m_isDraggingTitleBar = inTitleBarArea;
            m_currentInteraction = UserInteraction.Starting;

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
            UpdateFocusRectangle();
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
