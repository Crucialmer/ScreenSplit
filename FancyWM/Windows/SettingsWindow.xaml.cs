using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.Utilities;
using FancyWM.ViewModels;

using ModernWpf;
using Serilog;

namespace FancyWM.Windows
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml (macOS 26 Tahoe Floating Liquid Glass Settings)
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly ILogger m_logger = App.Current.Logger;
        private readonly SettingsViewModel m_viewModel;
        private bool m_isSyncingSelection;

        public SettingsWindow(SettingsViewModel viewModel)
        {
            m_logger.Debug($"Initialising {nameof(SettingsWindow)}");
            m_viewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
            SidebarAppIcon.Source = LiquidGlassTheme.GetAppIconImage();
            m_logger.Debug($"Initialised {nameof(SettingsWindow)} successfully");

            SourceInitialized += OnSourceInitialized;
            ThemeManager.Current.ActualApplicationThemeChanged += OnThemeChanged;

            Loaded += (_, _) =>
            {
                LiquidGlassTheme.PlayEntranceAnimation(RootContent, RootTranslate, liftPixels: 8, durationMs: 200);
                Activate();
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                FocusHelper.ForceActivate(hwnd);
            };
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            LiquidGlassTheme.ApplyNativeWindowBackdrop(this, enableMicaBackdrop: true);
        }

        private void OnThemeChanged(ThemeManager sender, object args)
        {
            Dispatcher.BeginInvoke(() => LiquidGlassTheme.ApplyNativeWindowBackdrop(this, enableMicaBackdrop: true));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            FocusManager.SetFocusedElement(this, this);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            Resources.Remove("MicaPrimaryColor");
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            Resources["MicaPrimaryColor"] = Colors.Transparent;
        }

        protected override void OnClosed(EventArgs e)
        {
            ThemeManager.Current.ActualApplicationThemeChanged -= OnThemeChanged;
            SourceInitialized -= OnSourceInitialized;
            base.OnClosed(e);
            m_viewModel.Dispose();
            FocusManager.SetFocusedElement(this, null);
            Keyboard.ClearFocus();
            GCHelper.ScheduleCollection();
        }

        private void PagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (m_isSyncingSelection)
                return;

            if (e.AddedItems.OfType<PageItem>().FirstOrDefault()?.Page is Type pageType)
            {
                GoToPage(pageType);
            }
        }

        public void GoToPage(Type pageType)
        {
            // Keep sidebar selection visually synchronized when navigating programmatically (e.g. OpenHelp)
            if (PagesList != null && PagesList.ItemsSource != null)
            {
                var matchingItem = PagesList.ItemsSource.OfType<PageItem>().FirstOrDefault(x => x.Page == pageType);
                if (matchingItem != null && !Equals(PagesList.SelectedItem, matchingItem))
                {
                    m_isSyncingSelection = true;
                    try
                    {
                        PagesList.SelectedItem = matchingItem;
                    }
                    finally
                    {
                        m_isSyncingSelection = false;
                    }
                }
            }

            var page = (UIElement)Activator.CreateInstance(pageType, m_viewModel)!;
            Dispatcher.InvokeAsync(() =>
            {
                PageContent.Child = page;
                ContentScroller?.ScrollToTop();
                LiquidGlassTheme.PlayEntranceAnimation(PageContent, PageTranslate, liftPixels: 6, durationMs: 165);
            }, DispatcherPriority.ContextIdle);
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else
            {
                DragMove();
            }
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

        private void OnMinimizeButtonClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximizeButtonClick(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void OnQuitButtonClick(object sender, RoutedEventArgs e)
        {
            App.Current.Terminate();
        }

        private void OnSponsorButtonClick(object sender, RoutedEventArgs e)
        {
            App.Sponsor();
        }
    }
}
