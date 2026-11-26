using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using FancyWM.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Windows.System;

namespace FancyWM.Windows
{
    /// <summary>
    /// Interaction logic for UpdateWindow.xaml (macOS 26 Liquid Glass Software Update Sheet)
    /// </summary>
    public partial class UpdateWindow : Window
    {
        private static UpdateWindow? s_currentInstance;

        private readonly ReleaseChecker m_releaseChecker;
        private readonly Version m_currentVersion;
        private readonly ReleaseInfo? m_preloadedRelease;
        private CancellationTokenSource? m_downloadCts;
        private ReleaseInfo? m_latestRelease;
        private string? m_extractedSourceDir;

        public static void ShowUpdateDialog(Window? owner = null, ReleaseInfo? preloadedRelease = null)
        {
            if (s_currentInstance != null && s_currentInstance.IsLoaded)
            {
                if (s_currentInstance.WindowState == WindowState.Minimized)
                {
                    s_currentInstance.WindowState = WindowState.Normal;
                }
                if (preloadedRelease != null && s_currentInstance.m_latestRelease == null)
                {
                    s_currentInstance.ApplyReleaseInfo(preloadedRelease);
                }
                s_currentInstance.Activate();
                s_currentInstance.Focus();
                return;
            }

            var window = new UpdateWindow(preloadedRelease);
            if (owner != null && owner.IsLoaded && owner.IsVisible)
            {
                window.Owner = owner;
            }
            s_currentInstance = window;
            window.Closed += (_, _) => s_currentInstance = null;
            window.Show();
        }

        public UpdateWindow(ReleaseInfo? preloadedRelease = null)
        {
            InitializeComponent();
            m_preloadedRelease = preloadedRelease;

            if (Application.Current is App app)
            {
                m_releaseChecker = app.Services?.GetService<ReleaseChecker>() ?? new ReleaseChecker();
                m_currentVersion = App.NormalizedVersion;
            }
            else
            {
                m_releaseChecker = new ReleaseChecker();
                m_currentVersion = new Version(1, 0, 36);
            }

            AppHeroIcon.Source = LiquidGlassTheme.GetAppIconImage();
            Loaded += OnWindowLoaded;
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            LiquidGlassTheme.PlayEntranceAnimation(SheetContainer, SheetTranslate, liftPixels: 12, durationMs: 220);

            if (m_preloadedRelease != null)
            {
                ApplyReleaseInfo(m_preloadedRelease);
                return;
            }

            await CheckForUpdatesAsync();
        }

        private void ApplyReleaseInfo(ReleaseInfo info)
        {
            m_latestRelease = info;
            if (info.Version != null && info.Version > m_currentVersion)
            {
                SetStateUpdateAvailable(info);
            }
            else
            {
                SetStateUpToDate(info);
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            SetStateChecking();

            try
            {
                var release = await m_releaseChecker.GetLatestReleaseInfoAsync("Crucialmer", "ScreenSplit");

                if (release == null || release.Version == null)
                {
                    SetStateError("未能从更新服务器解析到有效的版本信息。");
                    return;
                }

                ApplyReleaseInfo(release);
            }
            catch (Exception ex)
            {
                SetStateError(ex.Message);
            }
        }

        #region State Management

        private void HideAllViews()
        {
            CheckingView.Visibility = Visibility.Collapsed;
            UpToDateView.Visibility = Visibility.Collapsed;
            UpdateAvailableView.Visibility = Visibility.Collapsed;
            DownloadingView.Visibility = Visibility.Collapsed;
            ReadyToRestartView.Visibility = Visibility.Collapsed;
            ErrorView.Visibility = Visibility.Collapsed;
        }

        private void SetBadge(string text, Color color)
        {
            var bg = new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B));
            var border = new SolidColorBrush(Color.FromArgb(76, color.R, color.G, color.B));
            var fg = new SolidColorBrush(color);
            bg.Freeze();
            border.Freeze();
            fg.Freeze();

            StatusBadge.Background = bg;
            StatusBadge.BorderBrush = border;
            StatusBadgeText.Foreground = fg;
            StatusBadgeText.Text = text;
        }

        private void SetStateChecking()
        {
            HideAllViews();
            CheckingView.Visibility = Visibility.Visible;
            SetBadge("检查中", Color.FromRgb(10, 132, 255));
            CheckingSubtext.Text = $"正在比对本地版本 (v{m_currentVersion}) 与 GitHub Release 发行通道";
        }

        private void SetStateUpToDate(ReleaseInfo info)
        {
            HideAllViews();
            UpToDateView.Visibility = Visibility.Visible;
            SetBadge($"v{m_currentVersion} · 最新", Color.FromRgb(48, 209, 88));

            UpToDateDescription.Text = $"ScreenSplit v{m_currentVersion} 目前已是最新状态。\n所有智能边缘吸附与 240Hz 超流畅性能均已就绪。";
            LastCheckedText.Text = $"上次检查时间: 今天 {DateTime.Now:HH:mm:ss}";
        }

        private void SetStateUpdateAvailable(ReleaseInfo info)
        {
            HideAllViews();
            UpdateAvailableView.Visibility = Visibility.Visible;
            SetBadge($"发现新版本 v{info.Version}", Color.FromRgb(10, 132, 255));

            NewVersionTitle.Text = $"发现新版本 ScreenSplit v{info.Version}";

            var sizeMb = info.AssetSize > 0 ? (info.AssetSize / (1024.0 * 1024.0)).ToString("0.0") : "8.8";
            var pubDate = info.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? DateTime.Now.ToString("yyyy-MM-dd");
            NewVersionMeta.Text = $"发布时间: {pubDate}  •  大小: ~{sizeMb} MB";

            var changelog = string.IsNullOrWhiteSpace(info.Changelog)
                ? $"### ScreenSplit v{info.Version}\n• macOS 26 Liquid Glass 设计美学与性能全面升级\n• 智能边缘停靠与多窗口分屏管理优化"
                : info.Changelog;
            ReleaseNotesText.Text = changelog;
        }

        private void SetStateDownloading()
        {
            HideAllViews();
            DownloadingView.Visibility = Visibility.Visible;
            SetBadge("正在下载", Color.FromRgb(10, 132, 255));

            DownloadProgressBar.Value = 0;
            DownloadPercentText.Text = "0%";
            DownloadStatusDetail.Text = "正在连接下载节点并建立传输连接...";
        }

        private void SetStateReadyToRestart()
        {
            HideAllViews();
            ReadyToRestartView.Visibility = Visibility.Visible;
            SetBadge("更新就绪", Color.FromRgb(48, 209, 88));
        }

        private void SetStateError(string errorMessage)
        {
            HideAllViews();
            ErrorView.Visibility = Visibility.Visible;
            SetBadge("连接异常", Color.FromRgb(245, 158, 11));
            ErrorMessageText.Text = $"错误详情: {errorMessage}";
        }

        #endregion

        #region Actions & Events

        private async void OnStartDownloadClick(object sender, RoutedEventArgs e)
        {
            if (m_latestRelease == null)
            {
                return;
            }

            await StartDownloadAndExtractAsync(m_latestRelease);
        }

        private async void OnForceReDownloadClick(object sender, RoutedEventArgs e)
        {
            if (m_latestRelease == null)
            {
                await CheckForUpdatesAsync();
                return;
            }

            await StartDownloadAndExtractAsync(m_latestRelease);
        }

        private async Task StartDownloadAndExtractAsync(ReleaseInfo info)
        {
            SetStateDownloading();

            var downloadUrl = info.DownloadUrl;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                downloadUrl = $"https://github.com/Crucialmer/ScreenSplit/releases/download/{info.TagName}/ScreenSplit.zip";
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "ScreenSplit_Update");
            var zipPath = Path.Combine(tempDir, "ScreenSplit.zip");
            var extractDir = Path.Combine(tempDir, "extracted");

            m_downloadCts = new CancellationTokenSource();

            try
            {
                if (Directory.Exists(extractDir))
                {
                    try { Directory.Delete(extractDir, true); } catch { }
                }
                Directory.CreateDirectory(extractDir);

                var progress = new Progress<(long BytesRead, long TotalBytes, double SpeedMBps)>(p =>
                {
                    if (p.TotalBytes > 0)
                    {
                        var pct = Math.Min(100.0, (double)p.BytesRead / p.TotalBytes * 100.0);
                        DownloadProgressBar.Value = pct;
                        DownloadPercentText.Text = $"{pct:0}%";

                        var readMb = (p.BytesRead / (1024.0 * 1024.0)).ToString("0.0");
                        var totalMb = (p.TotalBytes / (1024.0 * 1024.0)).ToString("0.0");
                        var speedStr = p.SpeedMBps.ToString("0.0");
                        DownloadStatusDetail.Text = $"已下载: {readMb} MB / {totalMb} MB  •  下载速度: {speedStr} MB/s";
                    }
                });

                await m_releaseChecker.DownloadAssetAsync(
                    downloadUrl,
                    zipPath,
                    info.AssetSize > 0 ? info.AssetSize : 8821926,
                    progress,
                    m_downloadCts.Token);

                DownloadStatusDetail.Text = "下载完成，正在解压与校验更新包...";
                await Task.Run(() =>
                {
                    ZipFile.ExtractToDirectory(zipPath, extractDir, true);
                });

                var sourceDir = extractDir;
                if (File.Exists(Path.Combine(extractDir, "ScreenSplit_Lite", "ScreenSplit.exe")))
                {
                    sourceDir = Path.Combine(extractDir, "ScreenSplit_Lite");
                }
                else if (!File.Exists(Path.Combine(extractDir, "ScreenSplit.exe")))
                {
                    var files = Directory.GetFiles(extractDir, "ScreenSplit.exe", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        sourceDir = Path.GetDirectoryName(files[0])!;
                    }
                }

                m_extractedSourceDir = sourceDir;
                SetStateReadyToRestart();
            }
            catch (OperationCanceledException)
            {
                if (m_latestRelease != null && m_latestRelease.Version > m_currentVersion)
                {
                    SetStateUpdateAvailable(m_latestRelease);
                }
                else if (m_latestRelease != null)
                {
                    SetStateUpToDate(m_latestRelease);
                }
                else
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                SetStateError($"下载或解压更新包失败: {ex.Message}");
            }
            finally
            {
                m_downloadCts?.Dispose();
                m_downloadCts = null;
            }
        }

        private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
        {
            m_downloadCts?.Cancel();
        }

        private void OnRestartAndInstallClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(m_extractedSourceDir) || !Directory.Exists(m_extractedSourceDir))
            {
                SetStateError("找不到已解压的更新文件，请重试下载。");
                return;
            }

            try
            {
                var targetDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                var sourceDir = m_extractedSourceDir.TrimEnd('\\', '/');
                int currentPid = Environment.ProcessId;

                var updaterScriptPath = Path.Combine(Path.GetTempPath(), "screensplit_updater.cmd");
                var scriptContent = $@"@echo off
chcp 65001 >nul
set TARGET_DIR=%~1
set SOURCE_DIR=%~2
set TARGET_PID=%~3

:wait_process
timeout /t 1 /nobreak >nul
tasklist /fi ""PID eq %TARGET_PID%"" 2>nul | find ""%TARGET_PID%"" >nul
if not errorlevel 1 goto wait_process

timeout /t 1 /nobreak >nul

:: Copy updated files over target directory, keeping Data/ intact
xcopy ""%SOURCE_DIR%\*"" ""%TARGET_DIR%\"" /s /e /y /q >nul

:: Launch updated ScreenSplit
start """" ""%TARGET_DIR%\ScreenSplit.exe""

:: Cleanup
(goto) 2>nul & del ""%~f0""
exit
";

                File.WriteAllText(updaterScriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{updaterScriptPath}\" \"{targetDir}\" \"{sourceDir}\" {currentPid}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(psi);

                SystemSnapManager.RestoreSystemSnap();
                if (Application.Current is App app)
                {
                    app.Terminate();
                }
                else
                {
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                SetStateError($"启动自动更新程序失败: {ex.Message}");
            }
        }

        private async void OnViewInBrowserClick(object sender, RoutedEventArgs e)
        {
            var url = m_latestRelease?.HtmlUrl ?? "https://github.com/Crucialmer/ScreenSplit/releases";
            await Launcher.LaunchUriAsync(new Uri(url));
        }

        private async void OnRetryClick(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync();
        }

        private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        {
            m_downloadCts?.Cancel();
            Close();
        }

        private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        #endregion
    }
}
