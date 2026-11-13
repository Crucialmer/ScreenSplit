using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

using FancyWM.ViewModels;

using Windows.ApplicationModel;
using Windows.System;

namespace FancyWM.Pages.Settings
{
    /// <summary>
    /// Interaction logic for AboutPage.xaml
    /// </summary>
    public partial class AboutPage : UserControl
    {
        public AboutPage()
        {
            InitializeComponent();
            DataContext = new
            {
                AppVersionText = App.Current.VersionString,
            };
        }

        public AboutPage(SettingsViewModel _) : this()
        {
        }

        private void OnHyperlinkRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            _ = Launcher.LaunchUriAsync(e.Uri);
        }

        private void OnHyperlinkButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _ = Launcher.LaunchUriAsync(uri);
            }
        }
        private async void OnCheckUpdatesButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.IsEnabled = false;
                try
                {
                    if (App.Current.Services.GetService(typeof(FancyWM.Utilities.ReleaseChecker)) is FancyWM.Utilities.ReleaseChecker checker)
                    {
                        var latestVersion = await checker.GetLatestStableVersionAsync("Crucialmer", "ScreenSplit");
                        var currentVersion = new Version(App.Current.VersionString);
                        if (latestVersion != null && latestVersion > currentVersion)
                        {
                            var result = MessageBox.Show(
                                $"发现新版本: v{latestVersion}\n当前版本: v{currentVersion}\n\n是否前往 GitHub Release 查看并下载最新版？",
                                "ScreenSplit 检查更新",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Information);
                            if (result == MessageBoxResult.Yes)
                            {
                                _ = Launcher.LaunchUriAsync(new Uri("https://github.com/Crucialmer/ScreenSplit/releases"));
                            }
                        }
                        else
                        {
                            MessageBox.Show(
                                $"当前已是最新版本 (v{currentVersion})，无需更新。",
                                "ScreenSplit 检查更新",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"检查更新失败: {ex.Message}",
                        "ScreenSplit 检查更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            }
        }

        private void OnSponsorButtonClick(object sender, RoutedEventArgs e)
        {
            App.Sponsor();
        }
        private void OnReviewButtonClick(object sender, RoutedEventArgs e)
        {
            App.Review();
        }
    }
}
