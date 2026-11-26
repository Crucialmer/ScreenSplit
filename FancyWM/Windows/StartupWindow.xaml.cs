using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

using FancyWM.Utilities;
using FancyWM.ViewModels;

namespace FancyWM.Windows
{
    /// <summary>
    /// Interaction logic for StartupWindow.xaml (macOS 26 Liquid Glass Welcome Sheet)
    /// </summary>
    public partial class StartupWindow : Window
    {
        private readonly SettingsViewModel m_settingsViewModel;

        public StartupWindow(SettingsViewModel settingsViewModel)
        {
            InitializeComponent();
            m_settingsViewModel = settingsViewModel;
            DataContext = m_settingsViewModel;
            AppHeroIcon.Source = LiquidGlassTheme.GetAppIconImage();

            Loaded += (_, _) =>
            {
                LiquidGlassTheme.PlayEntranceAnimation(SheetContainer, SheetTranslate, liftPixels: 12, durationMs: 220);
                Activate();
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                FocusHelper.ForceActivate(hwnd);
            };
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

        private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

        private void OnMinimizeButtonClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnHelpClick(object sender, RoutedEventArgs e)
        {
            Close();
            MainWindow.OpenHelp();
        }

        private void OnStartExperienceClick(object sender, RoutedEventArgs e) => Close();
    }
}
