using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows;

using FancyWM.Utilities;
using FancyWM.DllImports;

using Microsoft.Extensions.DependencyInjection;

using Serilog;
using Serilog.Events;
using System.Threading;
using System.Globalization;
using FancyWM.Resources;

namespace FancyWM
{
    public static class Startup
    {
        class Arguments
        {
            public LogEventLevel LogLevel { get; init; }
            public bool HasConsole { get; init; }
        }

        private static event Action? CrashCleanup;

        private static event Action? ProgramExit;

        private static Mutex? s_singleInstanceMutex;

        private const string LogFile = "screensplit.log";

        private static bool IsPackaged
        {
            get
            {
                try
                {
                    global::Windows.ApplicationModel.Package.Current.GetHashCode();
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PInvoke.FreeConsole();
            }

            if (args.Contains("--help") || args.Contains("-h"))
            {
                Console.WriteLine($@"
ScreenSplit - Dynamic Tiling Window Manager
===========================================

USAGE: ScreenSplit.exe [OPTIONS] [ACTION]

OPTIONS:
    -h, --help                Show this help
    -v, -vv, -vvv             Verbose logging (repeat for more)
    --version                 Show version info
    --action NAME             Execute specific action directly

EXAMPLES:
    ScreenSplit --action CreateHorizontalPanel

ACTIONS:
{GenerateActionsHelp()}

Type 'ScreenSplit --help' from anywhere after installation.
");
                return 0;
            }

            if (args.Contains("--action"))
            {
                SendMessage(args[Array.IndexOf(args, "--action") + 1]);
                return 0;
            }

            if (args.Contains("--update") || args.Contains("--check-updates"))
            {
                try
                {
                    SendMessage("CheckUpdates");
                    return 0;
                }
                catch
                {
                    // No instance running, will boot up and open dialog
                }
            }

            if (args.Contains("--version"))
            {
                Console.WriteLine(GetFullVersionString());
                return 0;
            }

            return AppMain(args);
        }

        private static int AppMain(string[] args)
        {
            // Set the working path (Portable Mode: data stored inside application directory)
            string appDir = AppContext.BaseDirectory;
            string fullPath = Path.Combine(appDir, "Data");
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }
            Directory.SetCurrentDirectory(fullPath);

            if (File.Exists("administrator-mode") && !IsAdministrator())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        Verb = "runas",
                        FileName = Environment.ProcessPath!,
                        UseShellExecute = true,
                    });
                    return 0;
                }
                catch
                {
                    // Something went wrong.
                }
            }

            // Check if another instance is already running using system-wide Mutex
            const string mutexName = @"Local\ScreenSplit_SingleInstance_Mutex_Crucialmer";
            s_singleInstanceMutex = new Mutex(true, mutexName, out bool isInitialInstance);
            if (!isInitialInstance)
            {
                // Another instance is already running! Gently wake up the existing instance and exit cleanly
                try
                {
                    var hwnd = PInvoke.FindWindow(null, "ScreenSplitMainWindow").Value;
                    if (hwnd != IntPtr.Zero)
                    {
                        WindowCopyDataHelper.Send(hwnd, Encoding.Default.GetBytes("OpenSettings"));
                    }
                }
                catch
                {
                }
                return 0;
            }

            // Parse command line
            var logLevel = args.Contains("-vv")
                ? LogEventLevel.Verbose
                : args.Contains("-v")
                ? LogEventLevel.Debug
                : LogEventLevel.Information;
#if DEBUG
            if (logLevel > LogEventLevel.Debug)
            {
                logLevel = LogEventLevel.Debug;
            }
#endif

            // Create logger configuration
            var logConfig = new LoggerConfiguration();

            IServiceCollection serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection, new Arguments
            {
                LogLevel = logLevel,
                HasConsole = PInvoke.GetConsoleWindow() != IntPtr.Zero,
            });

            ProgramExit += OnProgramExit;
            ProgramExit += SystemSnapManager.RestoreSystemSnap;
            CrashCleanup += SystemSnapManager.RestoreSystemSnap;
            SystemSnapManager.Initialize();

            var provider = serviceCollection.BuildServiceProvider();

            // Create logger
            var logger = provider.GetRequiredService<ILogger>();
            logger.Information($"ScreenSplit v{GetVersionString()} on {Environment.OSVersion}");

            ConfigureThreadPriority(logger);

            bool exitNormally = false;
            try
            {
                // Create the app
                var app = new App(provider);
                // We want this running after the one attached in App()
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    logger.Fatal("{Exception}", e.ExceptionObject);
                    CrashCleanup?.Invoke();
                    if (e.IsTerminating)
                    {
                        ProgramExit?.Invoke();
                    }
                };
                // Run the app
                app.InitializeComponent();
                int exitCode = app.Run();
                exitNormally = true;
                logger.Information("Exiting normally with exit code={ExitCode}", exitCode);
                ProgramExit?.Invoke();
                return exitCode;
            }
            finally
            {
                try
                {
                    s_singleInstanceMutex?.Dispose();
                }
                catch
                {
                }

                // Write crash log if needed
                if (!exitNormally)
                {
                    CrashCleanup?.Invoke();
                    ProgramExit?.Invoke();
                }
            }
        }

        private static void ConfigureThreadPriority(ILogger logger)
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to update process priority");
            }

            try
            {
                Process.GetCurrentProcess().PriorityBoostEnabled = true;
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to enable process priority boost");
            }

            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to increase main thread priority");
            }
        }

        private static void OnProgramExit()
        {
            if (Application.Current is App app && app.RestartOnClose)
            {
                var startupCommand = Environment.ProcessPath ?? (IsPackaged ? $"screensplit" : Assembly.GetEntryAssembly()?.Location.Replace(".dll", ".exe"));
                if (startupCommand == null)
                {
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout 1 && taskkill /f /im {Process.GetCurrentProcess().ProcessName}.exe && timeout 1 && \"{startupCommand}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
        }

        private static void ConfigureServices(IServiceCollection services, Arguments args)
        {
            ConfigureLogging(services, args);
            services.AddSingleton<IMicaProvider>(_ => new FauxMicaProvider(TimeSpan.FromSeconds(1)));
            services.AddSingleton(_ => new LowLevelMouseHook());
            services.AddSingleton(_ => new LowLevelKeyboardHook());
            services.AddSingleton(_ => new ReleaseChecker());
        }

        private static void ConfigureLogging(IServiceCollection services, Arguments args)
        {
            // Create log file
            bool isUsingTempLogFile = false;
            string logFileName = Path.GetFullPath(LogFile);
            try
            {
                File.WriteAllText(logFileName, "");
            }
            catch
            {
                // Fall back to using a temporary file
                isUsingTempLogFile = true;
                logFileName = Path.GetTempFileName() + ".log";
            }

            // Create logger configuration
            var logConfig = new LoggerConfiguration();
#if DEBUG
            if (Debugger.IsAttached)
            {
                logConfig.WriteTo.Debug();
            }
#endif

            logConfig.WriteTo.File(
                logFileName,
                restrictedToMinimumLevel: args.LogLevel,
                fileSizeLimitBytes: 4 * 1024 * 1024,
                buffered: false);

            var logBuffer = new CircularBuffer<string>(1000);
            var lineWriter = new LineWriter(logBuffer, '\n');

            logConfig.WriteTo.TextWriter(lineWriter, restrictedToMinimumLevel: LogEventLevel.Information);
            if (args.HasConsole)
            {
                logConfig.WriteTo.TextWriter(Console.Out);
            }

            var logger = logConfig.CreateLogger();

            services.AddSingleton<ILogger>(logger);
            services.AddSingleton<ILogFileViewer>(new LogFileViewer(logFileName, logBuffer));

            CrashCleanup += () =>
            {
                logger.Dispose();
                if (!isUsingTempLogFile)
                {
                    // Create the file with a placeholder error message indicating more severe failure
                    var crashFileName = $"screensplit-crash-{DateTimeOffset.Now:yyyyMMddTHHmmss}.log";
                    File.Copy(logFileName, crashFileName, true);
                }
            };

            ProgramExit += () =>
            {
                logger.Dispose();
            };
        }

        private static string GetFullVersionString() => $"ScreenSplit v{GetVersionString()} on {Environment.OSVersion}";

        private static string GetVersionString()
        {
            var path = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(path))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(path);
                return versionInfo.FileVersion ?? "1.0.0.0";
            }
            return "1.0.0.0";
        }

        private static bool IsAdministrator()
        {
            return (new WindowsPrincipal(WindowsIdentity.GetCurrent()))
                      .IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void SendMessage(string message)
        {
            var hwnd = PInvoke.FindWindow(null, "ScreenSplitMainWindow").Value;
            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("ScreenSplit is not running!");
            }
            WindowCopyDataHelper.Send(hwnd, Encoding.Default.GetBytes(message));
        }

        private static string GenerateActionsHelp()
        {
            return string.Join('\n', Enum.GetNames<Models.BindableAction>().Select(action =>
            {
                return $"    {action,-25} {Resources.Strings.ResourceManager.GetString($"Keybinding.{action}.Description")}";
            }));
        }
    }
}
