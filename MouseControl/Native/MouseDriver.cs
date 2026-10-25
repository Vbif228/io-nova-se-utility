using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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

        private const uint WM_APP_DEVICE = 0x4000;
        private const uint WM_APP_BUTTON = 0x4001;
        private const uint WM_DEVICECHANGE = 0x0219;

        private readonly object _stateLock = new();
        private IntPtr _hDll = IntPtr.Zero;
        private Set_VIDPID_Delegate? _setVidPid;
        private Open_DevMonitor_Delegate? _openMonitor;
        private Close_DevMonitor_Delegate? _closeMonitor;

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

        public bool Initialize()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllPath = Path.Combine(appDir, "hiddriver_1.dll");

            if (!File.Exists(dllPath))
            {
                dllPath = Path.GetFullPath(Path.Combine(appDir, @"..\..\..\Original_Driver\hiddriver_1.dll"));
            }

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

        public void Dispose()
        {
            _running = false;
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
