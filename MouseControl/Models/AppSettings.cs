using System;
using System.IO;
using System.Text.Json;

namespace MouseControl.Models
{
    /// <summary>
    /// User settings model with thread-safe atomic serialization to LocalApplicationData.
    /// Resilient against corrupted JSON files and concurrent write collisions.
    /// </summary>
    public class AppSettings
    {
        public string Theme { get; set; } = "Dark"; // "Dark" or "Light"
        public bool StartWithWindows { get; set; } = false;
        public bool MinimizeToTrayOnClose { get; set; } = true;

        // Auto Clicker
        public bool ClickerEnabled { get; set; } = true;
        public int ClickerCps { get; set; } = 25;
        public bool ClickerHumanizer { get; set; } = true;
        public string ClickerMode { get; set; } = "Hold"; // "Hold" or "Toggle"
        public int ClickerTriggerVk { get; set; } = 0xC0; // VK_OEM_3 = ~ / Ё
        public string ClickerTriggerKeyName { get; set; } = "Ё ( ~ )";
        public bool ClickerAudioFeedback { get; set; } = false;

        // Sensor & DPI Configuration (PixArt PAW3311 up to 12 000 DPI)
        public int SensorActiveDpiStage { get; set; } = 3; // 1 to 6 (default stage 3: 1600 DPI)
        public int[] SensorDpiStages { get; set; } = new int[] { 400, 800, 1600, 2400, 3200, 6400 };
        public string[] SensorDpiColors { get; set; } = new string[] { "#EF4444", "#10B981", "#3B82F6", "#F59E0B", "#06B6D4", "#8B5CF6" };
        public int SensorPollingRate { get; set; } = 1000; // 125, 250, 500, 1000 Hz
        public string SensorLod { get; set; } = "1mm"; // "1mm" or "2mm"
        public int SensorKeyDebounceMs { get; set; } = 4; // 2 to 20 ms hardware debounce
        public bool SensorAngleSnapping { get; set; } = false;
        public bool SensorRippleControl { get; set; } = false;
        public bool SensorMotionSync { get; set; } = true;

        // Debounce Filter (Anti-Double-Click)
        public bool DebounceEnabled { get; set; } = true;
        public int DebounceMs { get; set; } = 20; // 5 to 40 ms, 20 ms recommended for Huano switches

        // Switch Lifespan & Odometer (Huano 80M rating)
        public long TotalLmbClicks { get; set; } = 0;
        public long TotalRmbClicks { get; set; } = 0;

        // Battery Notifications
        public int BatteryWarningThreshold1 { get; set; } = 20;
        public int BatteryWarningThreshold2 { get; set; } = 10;
        public bool BatterySoundAlert { get; set; } = true;
        public bool BatteryNotificationAlert { get; set; } = true;
        public bool BatteryNotifyFullCharge { get; set; } = true;

        private static readonly object _fileLock = new();
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MouseControl"
        );
        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        string json = File.ReadAllText(SettingsPath);
                        var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                        if (loaded != null)
                        {
                            if (loaded.SensorDpiStages == null || loaded.SensorDpiStages.Length != 6)
                                loaded.SensorDpiStages = new int[] { 400, 800, 1600, 2400, 3200, 6400 };
                            if (loaded.SensorDpiColors == null || loaded.SensorDpiColors.Length != 6)
                                loaded.SensorDpiColors = new string[] { "#EF4444", "#10B981", "#3B82F6", "#F59E0B", "#06B6D4", "#8B5CF6" };
                            if (loaded.SensorActiveDpiStage < 1 || loaded.SensorActiveDpiStage > 6)
                                loaded.SensorActiveDpiStage = 3;
                            if (string.IsNullOrEmpty(loaded.SensorLod))
                                loaded.SensorLod = "1mm";
                            if (loaded.SensorPollingRate != 125 && loaded.SensorPollingRate != 250 && loaded.SensorPollingRate != 500 && loaded.SensorPollingRate != 1000)
                                loaded.SensorPollingRate = 1000;
                            return loaded;
                        }
                    }
                }
                catch
                {
                    // Fallback to fresh defaults if file is corrupt or unreadable
                }
                return new AppSettings();
            }
        }

        public void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    if (!Directory.Exists(SettingsDir))
                    {
                        Directory.CreateDirectory(SettingsDir);
                    }

                    string tempPath = SettingsPath + ".tmp";
                    string json = JsonSerializer.Serialize(this, _jsonOptions);
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, SettingsPath, overwrite: true);
                }
                catch
                {
                    // Defensively handle concurrent file IO or disk full errors
                }
            }
        }
    }
}
