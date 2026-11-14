using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;

using FancyWM.Models;
using FancyWM.Utilities;
using FancyWM.Windows;

using Microsoft.Extensions.DependencyInjection;
using Serilog;

using Windows.ApplicationModel;
using Windows.System;

namespace FancyWM
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static new App Current => (App)Application.Current;

        private readonly Lazy<AppState> m_appState = new(() => new AppState());

        private bool m_isProcessingUnhandledException = false;

        internal AppState AppState => m_appState.Value;

        internal IServiceProvider Services { get; }

        internal ILogger Logger { get; }

        internal string? PackageFamilyName
        {
            get
            {
                unsafe
                {
                    uint cBytes = 256;
                    char[] buffer = new char[256];
                    fixed (char* ptr = buffer)
                    {
                        var err = DllImports.PInvoke.GetPackageFamilyName(new(Process.GetCurrentProcess().Handle), &cBytes, new(ptr));
                        if (err == 0)
                        {
                            return new string(ptr);
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
            }
        }

        internal string VersionString
        {
            get
            {
                try
                {
                    Package package = Package.Current;
                    PackageId packageId = package.Id;
                    PackageVersion version = packageId.Version;
                    return string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
                }
                catch (Exception)
                {
                    var path = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
                    var versionInfo = !string.IsNullOrEmpty(path) ? FileVersionInfo.GetVersionInfo(path) : null;
                    return versionInfo?.FileVersion ?? "1.0.8.0";
                }
            }
        }

        public bool RestartOnClose { get; internal set; }

        public static bool IsAutostartLaunch { get; set; } = false;

        private App()
        {
            throw new NotImplementedException();
        }

        public App(ServiceProvider services)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Services = services;
            Logger = services.GetRequiredService<ILogger>();
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Logger?.Error(e.Exception, "Unobserved task exception caught and suppressed");
                e.SetObserved();
            };
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Logger?.Error(e.Exception, "Unhandled dispatcher exception caught and suppressed");
            e.Handled = true;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var asm = typeof(App).Assembly.GetName().Name;
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{asm};component/Themes/Fluent/Rounded.xaml", UriKind.RelativeOrAbsolute) });
            }
            else
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{asm};component/Themes/Fluent/Squared.xaml", UriKind.RelativeOrAbsolute) });
            }
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{asm};component/Themes/Fluent/Generic.xaml", UriKind.RelativeOrAbsolute) });

            if (e.Args != null && (e.Args.Contains("--update") || e.Args.Contains("--check-updates")))
            {
                Dispatcher.BeginInvoke(() => FancyWM.Windows.UpdateWindow.ShowUpdateDialog(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        internal string GetRealPath(string path)
        {
            if (PackageFamilyName is String pfn)
            {
                var appDataPath = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))!;
                var packagedAppDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages",
                    pfn,
                    "LocalCache");
                return path.Replace(appDataPath, packagedAppDataPath);
            }
            return path;
        }

        internal static void Sponsor()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Crucialmer/ScreenSplit",
                UseShellExecute = true,
                Verb = "open",
            });
        }

        internal static void Review()
        {
            _ = Launcher.LaunchUriAsync(new Uri(Publishing.StoreReviewProtocolLink));
        }

        internal void Terminate()
        {
            foreach (var win in Windows.OfType<MainWindow>())
            {
                win.IsTerminating = true;
            }
            Close();
            Shutdown();
        }

        private async void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = (Exception)e.ExceptionObject!;
            while (m_isProcessingUnhandledException)
            {
                await Task.Yield();
            }
            m_isProcessingUnhandledException = true;

            try
            {
                HandleException(exception);
            }
            finally
            {
                m_isProcessingUnhandledException = false;
            }
        }

        private static bool DidExplorerJustCrash()
        {
            if (DllImports.PInvoke.GetShellWindow().Value == 0)
            {
                return true;
            }

            bool didExplorerJustRestart = Process.GetProcessesByName("explorer")
                .Where(x => x.MainWindowTitle.Length == 0 && DateTime.Now - x.StartTime < TimeSpan.FromSeconds(1))
                .Any();
            if (didExplorerJustRestart)
            {
                return true;
            }

            return false;
        }

        private void HandleException(Exception exception)
        {
            if (exception is XamlParseException && exception.InnerException is Exception innerException)
            {
                exception = innerException;
            }

            // Special case for COMException: We do not show a dialog for 
            // this type of exception when explorer.exe is closed. This is because
            // explorer.exe is what most-likely caused the crash in the first place.
            if (exception is COMException && exception.StackTrace?.Contains("Win32VirtualDesktopManager") == true && DidExplorerJustCrash())
            {
                exception = new COMException($"Windows Explorer (explorer.exe) just crashed and this caused the virtual desktop service to fail! ScreenSplit received the following error message: {exception.Message}", exception);
            }

            // Show error dialog
            var result = ShowDialogOnBackgroundThread(() => new ErrorMessageBox()
            {
                ExceptionObject = exception
            }).Result;

            // View log file
            if (result == true)
            {
                Services.GetRequiredService<ILogFileViewer>().Open();
            }

            // Close and dispose of all application windows
            Exception? closingException = null;
            try
            {
                Close();
            }
            catch (Exception ce)
            {
                closingException = ce;
            }

            // Try to write logs
            try
            {
                Logger.Fatal("{Exception}", exception);
                if (closingException != null)
                {
                    Logger.Error("{Exception}", closingException);
                }
            }
            catch (Exception)
            {
                // Things are really bad now (logger failed to initialize or to write to disk)
            }

            // Hard crash
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        private void Close()
        {
            void CloseAppWindows()
            {
                List<Exception> exceptions = [];
                foreach (var window in Windows.Cast<Window>())
                {
                    try
                    {
                        // Dispose and close window
                        if (window is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                        try
                        {
                            window.Close();
                        }
                        catch (InvalidOperationException)
                        {
                            // Already closing...
                        }
                    }
                    catch (Exception e)
                    {
                        exceptions.Add(e);
                    }
                }
                if (exceptions.Count > 0)
                {
                    throw new AggregateException("Application shutdown failed!", exceptions);
                }
            }

            if (Dispatcher.Thread == Thread.CurrentThread)
            {
                CloseAppWindows();
            }
            else
            {
                // Use highest priority to close
                Dispatcher.Invoke(CloseAppWindows, System.Windows.Threading.DispatcherPriority.Send);
            }

            if (Services.GetRequiredService<LowLevelMouseHook>() is LowLevelMouseHook mshk)
            {
                mshk.Dispose();
            }
        }

        private static Task<bool?> ShowDialogOnBackgroundThread(Func<Window> windowFactory)
        {
            TaskCompletionSource<bool?> tcs = new();
            Thread thread = new(() =>
            {
                try
                {
                    // Create separate dispatcher
                    _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    var window = windowFactory();
                    tcs.SetResult(window.ShowDialog());
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "DialogWindowBackgroundThread";
            thread.Start();

            return tcs.Task;
        }
    }
}
