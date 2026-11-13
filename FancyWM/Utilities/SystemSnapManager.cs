using System;
using Microsoft.Win32;
using FancyWM.DllImports;
using static FancyWM.DllImports.PInvoke;

namespace FancyWM.Utilities
{
    public static class SystemSnapManager
    {
        private static bool s_isHooked = false;

        public static void Initialize()
        {
            if (s_isHooked) return;
            s_isHooked = true;

            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreSystemSnap();
            }
            catch
            {
            }
        }

        public static void DisableWhileRunning()
        {
            try
            {
                unsafe
                {
                    // Only broadcast in-memory session change during runtime (do NOT write 0 to user profile on disk)
                    // This ensures that even in case of sudden termination or force-kill, on-disk setting remains enabled!
                    SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWINARRANGING, 0u, null, SystemParametersInfo_fWinIni.SPIF_SENDCHANGE);
                }
            }
            catch (Exception ex)
            {
                App.Current?.Logger?.Warning(ex, "Failed to temporarily disable Windows window arranging");
            }
        }

        public static void RestoreSystemSnap()
        {
            try
            {
                // 1. Invoke SystemParametersInfo with SPIF_UPDATEINIFILE | SPIF_SENDCHANGE (0x03)
                unsafe
                {
                    const SystemParametersInfo_fWinIni flags = (SystemParametersInfo_fWinIni)0x03; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
                    SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWINARRANGING, 1u, null, flags);
                }

                // 2. Ensure registry keys in HKCU:\Control Panel\Desktop are set to enabled
                using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (key != null)
                    {
                        key.SetValue("WindowArrangementActive", "1", RegistryValueKind.String);
                        key.SetValue("SnapSizing", "1", RegistryValueKind.String);
                        key.SetValue("DockMoving", "1", RegistryValueKind.String);
                    }
                }

                // 3. Ensure SnapAssist in Explorer\Advanced is also enabled
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true))
                {
                    if (key != null)
                    {
                        key.SetValue("SnapAssist", 1, RegistryValueKind.DWord);
                        key.SetValue("JointResize", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
