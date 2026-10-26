using System.Reflection;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MouseControl.Models;

namespace MouseControl.Native
{
    /// <summary>
    /// Manages interaction with the mouse hardware driver DLL (hiddriver_1.dll) and HID SetupAPI.
    /// Thread-safe, leak-free handle management for unmanaged window classes, DLL modules, and device info lists.
    /// </summary>
    public class MouseDriver : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Set_VIDPID_Delegate(int vid, int pid);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Open_DevMonitor_Delegate(IntPtr hWnd, uint devMsg, uint btnMsg);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Close_DevMonitor_Delegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Open_FeatureDevice_Delegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Close_FeatureDevice_Delegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetFeature_Delegate(IntPtr buffer, int length);

        private const uint WM_APP_DEVICE = 0x4000;
        private const uint WM_APP_BUTTON = 0x4001;
        private const uint WM_DEVICECHANGE = 0x0219;

        private readonly object _stateLock = new();
        private IntPtr _hDll = IntPtr.Zero;
        private Set_VIDPID_Delegate? _setVidPid;
        private Open_DevMonitor_Delegate? _openMonitor;
        private Close_DevMonitor_Delegate? _closeMonitor;
        private Open_FeatureDevice_Delegate? _openFeature;
        private Close_FeatureDevice_Delegate? _closeFeature;
        private SetFeature_Delegate? _setFeature;
        private readonly object _featureLock = new();

        private Thread? _pumpThread;
        private volatile bool _running = false;
        private IntPtr _hWnd = IntPtr.Zero;
        private WinApi.WndProcDelegate? _wndProc;

        private int _currentBattery = -1;
        private bool _isCharging = false;
        private bool _isConnected = false;
        private bool _isWired = false;
        private long _lastOnlineTick = 0;

        private int _lastWiredState = -1;
        private int _lastReportedBattery = -999;
        private bool _lastReportedCharging = false;
        private bool _lastReportedConnected = false;
        private bool _lastReportedWired = false;

        public event Action<int, bool, bool, bool>? BatteryUpdated; // percentage, isCharging, isConnected, isWired
        public event Action<int>? DpiStageChanged; // active DPI stage (1-6)

        public int CurrentBattery
        {
            get { lock (_stateLock) return _currentBattery; }
            private set { lock (_stateLock) _currentBattery = value; }
        }

        public bool IsCharging
        {
            get { lock (_stateLock) return _isCharging; }
            private set { lock (_stateLock) _isCharging = value; }
        }

        public bool IsConnected
        {
            get { lock (_stateLock) return _isConnected; }
            private set { lock (_stateLock) _isConnected = value; }
        }

        public bool IsWired
        {
            get { lock (_stateLock) return _isWired; }
            private set { lock (_stateLock) _isWired = value; }
        }

        private static string EnsureDriverDllExists()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string primaryPath = Path.Combine(appDir, "hiddriver_1.dll");
            if (File.Exists(primaryPath)) return primaryPath;

            string cwdPath = Path.Combine(Environment.CurrentDirectory, "hiddriver_1.dll");
            if (File.Exists(cwdPath)) return cwdPath;

            string devPath = Path.GetFullPath(Path.Combine(appDir, @"..\..\..\Original_Driver\hiddriver_1.dll"));
            if (File.Exists(devPath)) return devPath;

            string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MouseControl");
            string localAppDataPath = Path.Combine(localAppDataDir, "hiddriver_1.dll");
            if (File.Exists(localAppDataPath)) return localAppDataPath;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("MouseControl.hiddriver_1.dll");
                if (stream != null)
                {
                    try
                    {
                        using var fs = new FileStream(primaryPath, FileMode.Create, FileAccess.Write);
                        stream.CopyTo(fs);
                        return primaryPath;
                    }
                    catch
                    {
                        Directory.CreateDirectory(localAppDataDir);
                        using var fs = new FileStream(localAppDataPath, FileMode.Create, FileAccess.Write);
                        stream.CopyTo(fs);
                        return localAppDataPath;
                    }
                }
            }
            catch { }

            return primaryPath;
        }

        public bool Initialize()
        {
            string dllPath = EnsureDriverDllExists();
            if (!File.Exists(dllPath))
                return false;

            _hDll = WinApi.LoadLibrary(dllPath);
            if (_hDll == IntPtr.Zero)
                return false;

            var pSetVidPid = WinApi.GetProcAddress(_hDll, "Set_VIDPID");
            var pOpenMonitor = WinApi.GetProcAddress(_hDll, "Open_DevMonitor");
            var pCloseMonitor = WinApi.GetProcAddress(_hDll, "Close_DevMonitor");

            if (pSetVidPid == IntPtr.Zero || pOpenMonitor == IntPtr.Zero || pCloseMonitor == IntPtr.Zero)
            {
                WinApi.FreeLibrary(_hDll);
                _hDll = IntPtr.Zero;
                return false;
            }

            _setVidPid = Marshal.GetDelegateForFunctionPointer<Set_VIDPID_Delegate>(pSetVidPid);
            _openMonitor = Marshal.GetDelegateForFunctionPointer<Open_DevMonitor_Delegate>(pOpenMonitor);
            _closeMonitor = Marshal.GetDelegateForFunctionPointer<Close_DevMonitor_Delegate>(pCloseMonitor);

            var pOpenFeature = WinApi.GetProcAddress(_hDll, "Open_FeatureDevice");
            var pCloseFeature = WinApi.GetProcAddress(_hDll, "Close_FeatureDevice");
            var pSetFeature = WinApi.GetProcAddress(_hDll, "SetFeature");

            if (pOpenFeature != IntPtr.Zero)
                _openFeature = Marshal.GetDelegateForFunctionPointer<Open_FeatureDevice_Delegate>(pOpenFeature);
            if (pCloseFeature != IntPtr.Zero)
                _closeFeature = Marshal.GetDelegateForFunctionPointer<Close_FeatureDevice_Delegate>(pCloseFeature);
            if (pSetFeature != IntPtr.Zero)
                _setFeature = Marshal.GetDelegateForFunctionPointer<SetFeature_Delegate>(pSetFeature);

            // Initial hardware scan
            RefreshHardwareState();

            _running = true;
            _pumpThread = new Thread(MessagePumpWorker)
            {
                IsBackground = true,
                Name = "MouseDriver_PumpThread"
            };
            _pumpThread.Start();

            return true;
        }

        public void RefreshHardwareState()
        {
            var (hasWired, hasWireless) = DetectPnpDevices();

            int battery;
            bool charging;
            bool wired = hasWired;
            bool connected;

            lock (_stateLock)
            {
                if (hasWired)
                {
                    _isWired = true;
                    _isCharging = true;
                    connected = true;
                    _lastOnlineTick = Environment.TickCount64;

                    // If on wire and no battery reported yet, default to 100
                    if (_currentBattery < 0)
                        _currentBattery = 100;

                    battery = _currentBattery;
                    charging = _isCharging;
                }
                else if (hasWireless)
                {
                    _isWired = false;
                    _isCharging = false;

                    // USB Dongle is present. Check if mouse has communicated recently (within 7.5 seconds).
                    // If the mouse was switched OFF or went to sleep, no reports arrive.
                    bool isRecent = (Environment.TickCount64 - _lastOnlineTick) < 7500;
                    connected = _isConnected && isRecent;

                    battery = connected ? _currentBattery : -1;
                    charging = false;
                }
                else
                {
                    _isWired = false;
                    _isCharging = false;
                    connected = false;
                    _currentBattery = -1;
                    battery = -1;
                    charging = false;
                }

                _isConnected = connected;
            }

            int currentWireState = hasWired ? 1 : (hasWireless ? 0 : -1);
            if (currentWireState != _lastWiredState)
            {
                _lastWiredState = currentWireState;
                try
                {
                    if (hasWired)
                    {
                        _setVidPid?.Invoke(0x1D57, 0x2015);
                    }
                    else if (hasWireless)
                    {
                        _setVidPid?.Invoke(0x1D57, 0xFA60);
                    }
                }
                catch { }
            }

            bool changed;
            lock (_stateLock)
            {
                changed = (battery != _lastReportedBattery) ||
                          (charging != _lastReportedCharging) ||
                          (connected != _lastReportedConnected) ||
                          (wired != _lastReportedWired);

                if (changed)
                {
                    _lastReportedBattery = battery;
                    _lastReportedCharging = charging;
                    _lastReportedConnected = connected;
                    _lastReportedWired = wired;
                }
            }

            if (changed)
            {
                RaiseBatteryUpdated(battery, charging, connected, wired);
            }
        }

        private void MessagePumpWorker()
        {
            string className = "MouseControl_DevMonitor_" + Guid.NewGuid().ToString("N");
            IntPtr hInstance = WinApi.GetModuleHandle(null);
            bool classRegistered = false;

            try
            {
                _wndProc = WndProc;

                var wcx = new WinApi.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WinApi.WNDCLASSEX>(),
                    lpfnWndProc = _wndProc,
                    hInstance = hInstance,
                    lpszClassName = className
                };

                if (WinApi.RegisterClassEx(ref wcx) != 0)
                {
                    classRegistered = true;
                }

                _hWnd = WinApi.CreateWindowEx(0, className, "MouseMonitor", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (_hWnd != IntPtr.Zero && _openMonitor != null)
                {
                    // Pass 0 for btnMsg: We only monitor device/battery/DPI status via WM_APP_DEVICE.
                    // We explicitly DO NOT monitor mouse buttons to prevent any input latency or hook overhead.
                    _openMonitor(_hWnd, WM_APP_DEVICE, 0);
                    WinApi.SetTimer(_hWnd, (UIntPtr)1, 5000, IntPtr.Zero); // 5-second periodic connection check
                }

                // Zero-polling GetMessage loop: blocks with 0% CPU until WM_APP_DEVICE or WM_TIMER arrives.
                while (_running && WinApi.GetMessage(out WinApi.MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WinApi.WM_TIMER)
                    {
                        TriggerAsyncHardwareRefresh();
                        continue;
                    }

                    WinApi.TranslateMessage(ref msg);
                    WinApi.DispatchMessage(ref msg);
                }
            }
            catch { }
            finally
            {
                if (_hWnd != IntPtr.Zero)
                {
                    WinApi.KillTimer(_hWnd, (UIntPtr)1);
                }

                try
                {
                    _closeMonitor?.Invoke();
                }
                catch { }

                if (_hWnd != IntPtr.Zero)
                {
                    WinApi.DestroyWindow(_hWnd);
                    _hWnd = IntPtr.Zero;
                }

                if (classRegistered)
                {
                    WinApi.UnregisterClass(className, hInstance);
                }
            }
        }

        private int _refreshInProgress = 0;

        private void TriggerAsyncHardwareRefresh()
        {
            if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) == 0)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        RefreshHardwareState();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshInProgress, 0);
                    }
                });
            }
        }

        internal IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_APP_DEVICE)
            {
                long wp = wParam.ToInt64();
                long lp = lParam.ToInt64();

                byte header = (byte)(wp & 0xFF);
                byte packetType = (byte)((wp >> 8) & 0xFF);
                byte dataLow = (byte)(lp & 0xFF);
                byte dataHigh = (byte)((lp >> 8) & 0xFF);

                bool wired;
                lock (_stateLock) { wired = _isWired; }

                if (wp == 0)
                {
                    // Mouse turned OFF or entered sleep mode
                    bool changed;
                    lock (_stateLock)
                    {
                        bool connected = wired;
                        _isConnected = connected;
                        if (!connected)
                        {
                            _currentBattery = -1;
                        }

                        int reportBattery = connected ? _currentBattery : -1;
                        changed = (reportBattery != _lastReportedBattery) ||
                                  (_isCharging != _lastReportedCharging) ||
                                  (connected != _lastReportedConnected) ||
                                  (wired != _lastReportedWired);

                        if (changed)
                        {
                            _lastReportedBattery = reportBattery;
                            _lastReportedCharging = _isCharging;
                            _lastReportedConnected = connected;
                            _lastReportedWired = wired;
                        }
                    }

                    if (changed)
                    {
                        RaiseBatteryUpdated(wired ? _currentBattery : -1, _isCharging, wired, wired);
                    }
                    return WinApi.DefWindowProc(hWnd, msg, wParam, lParam);
                }

                // Any report with valid protocol (header 0x47 'G') indicates device is active and communicating
                if (header == 0x47)
                {
                    _lastOnlineTick = Environment.TickCount64;
                    lock (_stateLock)
                    {
                        _isConnected = true;
                    }

                    // Report ID 3 protocol (header 0x47 'G'):
                    // packetType 0x10: Hardware DPI stage switch (dataLow: active stage 1-6)
                    if (packetType == 0x10)
                    {
                        int stage = dataLow;
                        if (stage >= 1 && stage <= 6)
                        {
                            RaiseDpiStageChanged(stage);
                        }
                        return WinApi.DefWindowProc(hWnd, msg, wParam, lParam);
                    }

                    // packetType 0x20: Profile switch, 0x50: Sleep mode, 0x60: Polling rate, 0x70, 0x80
                    if (packetType == 0x20 || packetType == 0x50 || packetType == 0x60 || packetType == 0x70 || packetType == 0x80)
                    {
                        return WinApi.DefWindowProc(hWnd, msg, wParam, lParam);
                    }

                    // packetType 0x40: Dedicated battery & charging status
                    if (packetType == 0x40)
                    {
                        byte status = dataLow;
                        int battery = dataHigh;

                        bool isOnline = (battery >= 0 && battery <= 100);
                        bool connected = wired || isOnline;
                        bool charging = (status == 2) || wired;

                        bool changed;
                        lock (_stateLock)
                        {
                            if (connected && isOnline)
                            {
                                _currentBattery = battery;
                            }

                            _isCharging = charging;
                            _isConnected = connected;

                            int reportBattery = connected ? _currentBattery : -1;
                            changed = (reportBattery != _lastReportedBattery) ||
                                      (charging != _lastReportedCharging) ||
                                      (connected != _lastReportedConnected) ||
                                      (wired != _lastReportedWired);

                            if (changed)
                            {
                                _lastReportedBattery = reportBattery;
                                _lastReportedCharging = charging;
                                _lastReportedConnected = connected;
                                _lastReportedWired = wired;
                            }
                        }

                        if (changed)
                        {
                            RaiseBatteryUpdated(connected ? _currentBattery : -1, charging, connected, wired);
                        }
                    }
                }
            }
            else if (msg == WM_DEVICECHANGE)
            {
                TriggerAsyncHardwareRefresh();
            }

            return WinApi.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void RaiseDpiStageChanged(int stage)
        {
            var handler = DpiStageChanged;
            if (handler == null) return;

            foreach (Action<int> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(stage);
                }
                catch { }
            }
        }

        private void RaiseBatteryUpdated(int percentage, bool isCharging, bool isConnected, bool isWired)
        {
            var handler = BatteryUpdated;
            if (handler == null) return;

            foreach (Action<int, bool, bool, bool> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(percentage, isCharging, isConnected, isWired);
                }
                catch { }
            }
        }

        #region SetupAPI Device Enumeration

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid interfaceClassGuid;
            public uint flags;
            public IntPtr reserved;
        }

        private static (bool hasWired, bool hasWireless) DetectPnpDevices()
        {
            bool wired = false;
            bool wireless = false;
            IntPtr hDevInfo = IntPtr.Zero;

            try
            {
                HidD_GetHidGuid(out Guid hidGuid);
                hDevInfo = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE

                if (hDevInfo == IntPtr.Zero || hDevInfo == WinApi.INVALID_HANDLE_VALUE)
                    return (false, false);

                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                uint idx = 0;
                while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, idx, ref ifData))
                {
                    SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref ifData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                    if (requiredSize > 0)
                    {
                        IntPtr pDetail = Marshal.AllocHGlobal((int)requiredSize);
                        try
                        {
                            // cbSize on x86 is 6, on x64 is 8 (Marshal.SizeOf structure)
                            Marshal.WriteInt32(pDetail, IntPtr.Size == 8 ? 8 : 6);
                            if (SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref ifData, pDetail, requiredSize, out _, IntPtr.Zero))
                            {
                                string path = Marshal.PtrToStringUni(new IntPtr(pDetail.ToInt64() + 4))?.ToLowerInvariant() ?? "";
                                if (path.Contains("vid_1d57") && path.Contains("pid_2015"))
                                    wired = true;
                                else if (path.Contains("vid_1d57") && path.Contains("pid_fa60"))
                                    wireless = true;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pDetail);
                        }
                    }

                    idx++;
                    if (wired && wireless) break;
                }
            }
            catch { }
            finally
            {
                if (hDevInfo != IntPtr.Zero && hDevInfo != WinApi.INVALID_HANDLE_VALUE)
                {
                    SetupDiDestroyDeviceInfoList(hDevInfo);
                }
            }

            return (wired, wireless);
        }

        #endregion

        #region Hardware Feature Commands (DPI & Sensor Settings)

        /// <summary>
        /// Sensor calibration register lookup table extracted directly from IO Nova SE original driver (Mouse.exe at 0x41C448).
        /// Contains 220 8-bit calibration registers corresponding to DPI steps from 50 DPI (step 1) up to 11 000 DPI (step 220).
        /// </summary>
        public static readonly byte[] DpiRegisterTable = new byte[220]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12,
            0x13, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x22, 0x23, 0x24, 0x25,
            0x26, 0x27, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x36, 0x37, 0x38,
            0x39, 0x3A, 0x3B, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x4A, 0x4B,
            0x4C, 0x4D, 0x4E, 0x4F, 0x51, 0x52, 0x53, 0x54, 0x55, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5E,
            0x5F, 0x60, 0x61, 0x62, 0x63, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F, 0x70,
            0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7F, 0x80, 0x81, 0x82, 0x83,
            0x84, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8D, 0x8E, 0x8F, 0x90, 0x91, 0x93, 0x94, 0x95, 0x96,
            0x97, 0x98, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA7, 0xA8, 0xA9,
            0xAA, 0xAB, 0xAC, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBB, 0xBC,
            0xBD, 0xBE, 0xBF, 0xC0, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCF,
            0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDD, 0xDE, 0xDF, 0xE0, 0xE1,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xEA, 0xEB, 0x76, 0x77, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7F,
            0x80, 0x81, 0x82, 0x83, 0x84, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8D
        };

        internal static (byte r, byte g, byte b) GetStageColorRgb(AppSettings settings, int stageIndex)
        {
            if (settings?.SensorDpiColors != null && stageIndex < settings.SensorDpiColors.Length)
            {
                string hex = settings.SensorDpiColors[stageIndex]?.TrimStart('#') ?? "";
                if (hex.Length == 6 &&
                    byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
                    byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
                    byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    return (r, g, b);
                }
            }

            return stageIndex switch
            {
                0 => (0xEF, 0x44, 0x44), // Stage 1: Red
                1 => (0x10, 0xB9, 0x81), // Stage 2: Green
                2 => (0x3B, 0x82, 0xF6), // Stage 3: Blue
                3 => (0xEA, 0xB3, 0x08), // Stage 4: Yellow
                4 => (0x06, 0xB6, 0xD4), // Stage 5: Cyan
                5 => (0xA8, 0x55, 0xF7), // Stage 6: Magenta/Purple
                6 => (0xFF, 0x80, 0x00), // Stage 7: Orange
                7 => (0xFF, 0xFF, 0xFF), // Stage 8: White
                _ => (0xFF, 0xFF, 0xFF)
            };
        }

        internal static byte[] BuildSensorReport(AppSettings settings)
        {
            byte[] report = new byte[56];
            report[0] = 0x04; // Report ID 4
            report[1] = 0x38; // Length 56 (0x38 bytes)
            report[2] = 0x01;

            int activeStage = Math.Clamp(settings.SensorActiveDpiStage, 1, 6);
            report[3] = (byte)(activeStage - 1); // active stage (0-based: 0..5)
            report[4] = 0x06; // 6 stages total
            report[5] = 0x3F; // stages 1-6 enabled mask (0b00111111)
            report[6] = 0x00;
            report[7] = 0x00;

            // Stages 1-6 DPI values translated via PAW3311 register calibration table
            int[] dpiStages = settings.SensorDpiStages ?? new int[] { 400, 800, 1600, 2400, 3200, 6400 };
            for (int i = 0; i < 8; i++)
            {
                if (i < 6 && i < dpiStages.Length)
                {
                    int dpi = dpiStages[i];
                    int step = Math.Clamp(dpi / 50, 1, 220); // 50 to 11000 DPI
                    byte regVal = DpiRegisterTable[step - 1];
                    report[8 + i] = regVal;
                    report[16 + i] = (byte)(step > 200 ? 1 : 0); // High-DPI flag (>10 000 DPI)
                }
                else
                {
                    report[8 + i] = 0x00;
                    report[16 + i] = 0x00;
                }
            }

            // Byte 24: Active stage index (1-based: 1..6)
            report[24] = (byte)activeStage;

            // Bytes 25..48: 8 RGB color sets (3 bytes each * 8 stages = 24 bytes)
            for (int i = 0; i < 8; i++)
            {
                var (r, g, b) = GetStageColorRgb(settings, i);
                report[25 + i * 3] = r;
                report[26 + i * 3] = g;
                report[27 + i * 3] = b;
            }

            report[49] = 0x01;

            // 16-bit word sum checksum over bytes 3..49 (exact match to Mouse.exe 0x4143d0)
            ushort checksum = 0;
            for (int i = 3; i < 50; i++)
            {
                checksum += report[i];
            }
            report[50] = (byte)(checksum >> 8);
            report[51] = (byte)(checksum & 0xFF);

            return report;
        }

        internal static byte[] BuildPollingRateReport(int pollingRate)
        {
            byte interval = pollingRate switch
            {
                125 => 8,
                250 => 4,
                500 => 2,
                _ => 1
            };

            byte[] report = new byte[9];
            report[0] = 0x06; // Report ID 6
            report[1] = 0x09; // Length 9
            report[2] = 0x01;
            report[3] = interval;
            report[4] = (byte)(~interval);

            return report;
        }

        public bool SendActiveDpiStage(int stage, AppSettings? settings = null)
        {
            if (stage < 1 || stage > 6) return false;

            var cfg = settings != null
                ? settings
                : new AppSettings { SensorActiveDpiStage = stage };
            cfg.SensorActiveDpiStage = stage;

            return SendSensorConfiguration(cfg);
        }

        public bool SendSensorConfiguration(AppSettings settings)
        {
            if (settings == null) return false;
            if (_setFeature == null || _openFeature == null || _closeFeature == null)
                return false;

            lock (_featureLock)
            {
                try
                {
                    // Ensure Set_VIDPID is synchronized before opening feature device
                    bool wired;
                    lock (_stateLock) { wired = _isWired; }
                    if (wired)
                    {
                        _setVidPid?.Invoke(0x1D57, 0x2015);
                    }
                    else
                    {
                        _setVidPid?.Invoke(0x1D57, 0xFA60);
                    }

                    if (_openFeature() == 0) return false;

                    byte[] sensorReport = BuildSensorReport(settings);
                    IntPtr pSensor = Marshal.AllocHGlobal(sensorReport.Length);

                    try
                    {
                        Marshal.Copy(sensorReport, 0, pSensor, sensorReport.Length);
                        int resSensor = _setFeature(pSensor, sensorReport.Length);

                        // Allow mouse MCU firmware sufficient time to commit settings (matches OEM Mouse.exe behavior)
                        Thread.Sleep(300);

                        return resSensor != 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pSensor);
                        _closeFeature();
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        #endregion

        public void Dispose()
        {
            _running = false;
            try { _closeFeature?.Invoke(); } catch { }
            if (_hWnd != IntPtr.Zero)
            {
                WinApi.PostMessage(_hWnd, WinApi.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            try
            {
                _pumpThread?.Join(400);
            }
            catch { }

            if (_hDll != IntPtr.Zero)
            {
                WinApi.FreeLibrary(_hDll);
                _hDll = IntPtr.Zero;
            }
        }
    }
}
