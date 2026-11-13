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
