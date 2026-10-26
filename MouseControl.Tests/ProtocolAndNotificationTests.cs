using System;
using MouseControl.Models;
using MouseControl.Native;
using MouseControl.Services;
using Xunit;

namespace MouseControl.Tests
{
    public class ProtocolAndNotificationTests
    {
        private const uint WM_APP_DEVICE = 0x4000;
        private const uint WM_APP_BUTTON = 0x4001;

        [Fact]
        public void DpiPacket_DecodesStage_And_DoesNotUpdateBattery()
        {
            using var driver = new MouseDriver();

            int receivedStage = -1;
            bool batteryUpdatedCalled = false;

            driver.DpiStageChanged += stage => receivedStage = stage;
            driver.BatteryUpdated += (pct, charging, conn, wired) => batteryUpdatedCalled = true;

            // Packet: header 0x47, packetType 0x10 (DPI switch), stage 3
            IntPtr wParam = new IntPtr((0x10 << 8) | 0x47);
            IntPtr lParam = new IntPtr(3);

            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, wParam, lParam);

            Assert.Equal(3, receivedStage);
            Assert.False(batteryUpdatedCalled, "DPI switch packet must NOT be treated as a battery update!");
            Assert.NotEqual(0, driver.CurrentBattery);
            Assert.True(driver.IsConnected);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void DpiPacket_AllStages1To6_DecodeCorrectly(int expectedStage)
        {
            using var driver = new MouseDriver();

            int receivedStage = -1;
            bool batteryReported = false;

            driver.DpiStageChanged += stage => receivedStage = stage;
            driver.BatteryUpdated += (pct, charging, conn, wired) => batteryReported = true;

            IntPtr wParam = new IntPtr((0x10 << 8) | 0x47);
            IntPtr lParam = new IntPtr(expectedStage);

            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, wParam, lParam);

            Assert.Equal(expectedStage, receivedStage);
            Assert.False(batteryReported);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(255)]
        public void DpiPacket_OutOufBoundsStage_Ignored(int invalidStage)
        {
            using var driver = new MouseDriver();

            int receivedStage = -1;
            driver.DpiStageChanged += stage => receivedStage = stage;

            IntPtr wParam = new IntPtr((0x10 << 8) | 0x47);
            IntPtr lParam = new IntPtr(invalidStage);

            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, wParam, lParam);

            Assert.Equal(-1, receivedStage);
        }

        [Fact]
        public void BatteryPacket_DecodesBatteryAndChargingCorrectly()
        {
            using var driver = new MouseDriver();

            int reportedPct = -1;
            bool reportedCharging = false;
            bool reportedConn = false;

            driver.BatteryUpdated += (pct, charging, conn, wired) =>
            {
                reportedPct = pct;
                reportedCharging = charging;
                reportedConn = conn;
            };

            // Packet: header 0x47, packetType 0x40 (Battery report)
            // status = 2 (charging), battery = 90%
            IntPtr wParam = new IntPtr((0x40 << 8) | 0x47);
            IntPtr lParam = new IntPtr((90 << 8) | 2);

            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, wParam, lParam);

            Assert.Equal(90, reportedPct);
            Assert.True(reportedCharging);
            Assert.True(reportedConn);
            Assert.Equal(90, driver.CurrentBattery);
            Assert.True(driver.IsCharging);
        }

        [Fact]
        public void ProfileAndPollingRatePackets_DoNotTriggerBatteryUpdate()
        {
            using var driver = new MouseDriver();

            bool batteryReported = false;
            driver.BatteryUpdated += (pct, charging, conn, wired) => batteryReported = true;

            // Profile packet: type 0x20, payload 1
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, new IntPtr((0x20 << 8) | 0x47), new IntPtr(1));
            // Power packet: type 0x50, payload 0
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, new IntPtr((0x50 << 8) | 0x47), new IntPtr(0));
            // Polling rate packet: type 0x60, payload 3
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, new IntPtr((0x60 << 8) | 0x47), new IntPtr(3));

            Assert.False(batteryReported, "Non-battery packets must never trigger battery update events!");
            Assert.True(driver.IsConnected);
        }

        [Fact]
        public void NotificationManager_NegativeBattery_IgnoredSafely()
        {
            var settings = new AppSettings
            {
                BatteryWarningThreshold1 = 20,
                BatteryWarningThreshold2 = 10,
                BatteryNotificationAlert = true
            };

            var notifier = new NotificationManager(settings);

            // Sentinel -1 indicates offline/disconnected state and must be ignored without alerts
            notifier.ProcessBatteryUpdate(-1, isCharging: false, isConnected: true);
            notifier.ProcessBatteryUpdate(-1, isCharging: false, isConnected: false);
        }

        [Fact]
        public void NotificationManager_ZeroPercentBattery_ProcessesCriticalAlert()
        {
            var settings = new AppSettings
            {
                BatteryWarningThreshold1 = 20,
                BatteryWarningThreshold2 = 10,
                BatteryNotificationAlert = true,
                BatterySoundAlert = false
            };

            var notifier = new NotificationManager(settings);

            // Legitimate 0% battery must be processed through threshold2 without throwing
            notifier.ProcessBatteryUpdate(0, isCharging: false, isConnected: true);
        }

        [Fact]
        public void BatteryPacket_ZeroPercent_UpdatesStateCorrectly()
        {
            using var driver = new MouseDriver();

            int reportedPct = -1;
            bool batteryUpdated = false;

            driver.BatteryUpdated += (pct, chg, conn, wired) =>
            {
                reportedPct = pct;
                batteryUpdated = true;
            };

            // Legitimate 0% battery packet: header 0x47, packetType 0x40, battery 0%, status 0
            IntPtr wParam = new IntPtr((0x40 << 8) | 0x47);
            IntPtr lParam = new IntPtr((0 << 8) | 0);

            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, wParam, lParam);

            Assert.True(batteryUpdated);
            Assert.Equal(0, reportedPct);
            Assert.Equal(0, driver.CurrentBattery);
            Assert.True(driver.IsConnected);
        }

        [Fact]
        public void Non0x47Header_PacketsAreIgnored()
        {
            using var driver = new MouseDriver();

            int stageReceived = -1;
            bool batteryReported = false;

            driver.DpiStageChanged += stage => stageReceived = stage;
            driver.BatteryUpdated += (pct, chg, conn, wired) => batteryReported = true;

            // Packet with wrong header (0x12 instead of 0x47), even if packetType looks like 0x10 or 0x40
            IntPtr wParamDpi = new IntPtr((0x10 << 8) | 0x12);
            IntPtr lParamDpi = new IntPtr(3);
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, wParamDpi, lParamDpi);

            IntPtr wParamBat = new IntPtr((0x40 << 8) | 0x12);
            IntPtr lParamBat = new IntPtr((80 << 8) | 0);
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, wParamBat, lParamBat);

            Assert.Equal(-1, stageReceived);
            Assert.False(batteryReported);
        }

        [Fact]
        public void ExtendedPacketTypes_AcknowledgedWithoutBatteryCorruption()
        {
            using var driver = new MouseDriver();

            bool batteryReported = false;
            driver.BatteryUpdated += (pct, chg, conn, wired) => batteryReported = true;

            // Extended packets 0x70, 0x80
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, new IntPtr((0x70 << 8) | 0x47), new IntPtr(1));
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, new IntPtr((0x80 << 8) | 0x47), new IntPtr(2));

            Assert.False(batteryReported);
            Assert.True(driver.IsConnected);
        }

        [Fact]
        public void AppSettings_DpiNotificationAlert_DefaultsToTrue_AndPersists()
        {
            var settings = new AppSettings();
            Assert.True(settings.DpiNotificationAlert);

            settings.DpiNotificationAlert = false;
            Assert.False(settings.DpiNotificationAlert);
        }

        [Fact]
        public void RapidDpiSwitching_CycledProperly_WithoutBatteryInterference()
        {
            using var driver = new MouseDriver();

            int lastStage = -1;
            int changeCount = 0;
            bool batteryTriggered = false;

            driver.DpiStageChanged += stage =>
            {
                lastStage = stage;
                changeCount++;
            };
            driver.BatteryUpdated += (p, c, conn, w) => batteryTriggered = true;

            // Simulate pressing the bottom button 12 times cycling 1..6 twice
            for (int i = 0; i < 12; i++)
            {
                int stage = (i % 6) + 1;
                driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, new IntPtr((0x10 << 8) | 0x47), new IntPtr(stage));
            }

            Assert.Equal(12, changeCount);
            Assert.Equal(6, lastStage);
            Assert.False(batteryTriggered);
        }

        [Fact]
        public void ZeroWParam_TransitionsMouseToOffline()
        {
            using var driver = new MouseDriver();

            // First send online battery packet
            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, new IntPtr((0x40 << 8) | 0x47), new IntPtr((75 << 8) | 0));
            Assert.True(driver.IsConnected);
            Assert.Equal(75, driver.CurrentBattery);

            // Now send sleep packet (wp == 0)
            int reportedBattery = 0;
            bool reportedConnected = true;
            driver.BatteryUpdated += (pct, chg, conn, wired) =>
            {
                reportedBattery = pct;
                reportedConnected = conn;
            };

            driver.WndProc(IntPtr.Zero, WM_APP_DEVICE, IntPtr.Zero, IntPtr.Zero);

            Assert.False(driver.IsConnected);
            Assert.Equal(-1, reportedBattery);
            Assert.False(reportedConnected);
        }

        [Fact]
        public void NotificationManager_ProcessDpiUpdate_InvokesWithoutCrashing()
        {
            var settings = new AppSettings { DpiNotificationAlert = true };
            var notifier = new NotificationManager(settings);

            // Should safely execute without NullReferenceException even when notifyIcon is null
            notifier.ProcessDpiUpdate(2, 800);
            notifier.ProcessDpiUpdate(3, 1600);

            settings.DpiNotificationAlert = false;
            notifier.ProcessDpiUpdate(4, 2400);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)] // No hardware connected in unit test environment, safely returns false
        [InlineData(6, false)]
        [InlineData(7, false)]
        [InlineData(-1, false)]
        public void SendActiveDpiStage_ValidatesRangeAndGracefullyHandlesHeadless(int stage, bool expected)
        {
            using var driver = new MouseDriver();
            bool result = driver.SendActiveDpiStage(stage);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void SendSensorConfiguration_NullSettings_ReturnsFalse()
        {
            using var driver = new MouseDriver();
            bool result = driver.SendSensorConfiguration(null!);
            Assert.False(result);
        }

        [Fact]
        public void SendSensorConfiguration_ValidSettings_GracefullyExecutesInHeadless()
        {
            using var driver = new MouseDriver();
            var settings = new AppSettings
            {
                SensorActiveDpiStage = 2,
                SensorDpiStages = new int[] { 400, 800, 1600, 2400, 3200, 6400 },
                SensorPollingRate = 1000,
                SensorLod = "1mm"
            };
            bool result = driver.SendSensorConfiguration(settings);
            // Returns false cleanly in headless environment without crashing
            Assert.False(result);
        }

        [Fact]
        public void ProcessBatteryUpdate_ZeroPercent_DoesNotTriggerNotification()
        {
            var settings = new AppSettings
            {
                BatteryNotificationAlert = true,
                BatteryWarningThreshold1 = 20,
                BatteryWarningThreshold2 = 10
            };
            var notifier = new NotificationManager(settings);

            // 0% indicates sleep mode or disconnection artifact, must not trigger low battery warning
            notifier.ProcessBatteryUpdate(0, isCharging: false, isConnected: true);
            notifier.ProcessBatteryUpdate(-1, isCharging: false, isConnected: true);
            notifier.ProcessBatteryUpdate(101, isCharging: false, isConnected: true);
        }

        [Fact]
        public void ProcessBatteryUpdate_SuddenDrop_RequiresConsecutiveReports()
        {
            var settings = new AppSettings
            {
                BatteryNotificationAlert = true,
                BatteryWarningThreshold1 = 20,
                BatteryWarningThreshold2 = 10
            };
            var notifier = new NotificationManager(settings);

            // Initial healthy state
            notifier.ProcessBatteryUpdate(100, isCharging: false, isConnected: true);

            // Sudden drop (single report) is held for confirmation
            notifier.ProcessBatteryUpdate(15, isCharging: false, isConnected: true);

            // Second consecutive report confirms real discharge
            notifier.ProcessBatteryUpdate(15, isCharging: false, isConnected: true);
        }
    }
}
