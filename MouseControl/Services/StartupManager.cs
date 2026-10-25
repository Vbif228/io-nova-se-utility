using System;
using System.IO;
using Microsoft.Win32;

namespace MouseControl.Services
{
    /// <summary>
    /// Manages application startup with Windows via the CurrentUser Run registry key.
    /// Resilient against registry permission restrictions and process path resolution.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "MouseControl";

        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = Environment.ProcessPath 
                        ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MouseControl.exe");
                    key.SetValue(AppName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
                }
                else
                {
                    if (key.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch
            {
                // Defensive against Registry permission / security restrictions
            }
        }
    }
}
