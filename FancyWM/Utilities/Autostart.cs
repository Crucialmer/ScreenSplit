using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FancyWM.Utilities
{
    internal static class Autostart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "ScreenSplit";

        public static Task<bool> IsEnabledAsync()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var val = key?.GetValue(AppName) as string;
                return Task.FromResult(!string.IsNullOrEmpty(val));
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public static Task<bool> EnableAsync()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    return Task.FromResult(false);
                }
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
                key?.SetValue(AppName, $"\"{exePath}\" --autostart");
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public static Task<bool> DisableAsync()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
                key?.DeleteValue(AppName, false);
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }
}
