using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MouseControl.Models;
using MouseControl.Native;

namespace MouseControl.Services
{
    /// <summary>
    /// High-performance low-latency auto clicker with hardware mouse and keyboard hooks.
    /// Thread-safe click worker with deterministic mouse button release guarantees.
    /// </summary>
    public class AutoClicker : IDisposable
    {
        private readonly AppSettings _settings;
        private Thread? _clickThread;
        private Thread? _hookThread;

        private volatile bool _running = false;
        private volatile bool _isClicking = false;
        private volatile bool _toggleState = false;

        private volatile bool _physicalLmbDown = false;
        private volatile bool _physicalTriggerDown = false;
        private volatile bool _isButtonDown = false;

        // Debounce & Click Counter tracking
        private long _lastPhysicalLmbDownTick = 0;
        private long _lastPhysicalLmbUpTick = 0;
        private long _lastPhysicalRmbDownTick = 0;
        private long _lastPhysicalRmbUpTick = 0;

        private long _totalLmbClicks = 0;
        private long _totalRmbClicks = 0;
        private long _sessionLmbClicks = 0;
        private long _sessionRmbClicks = 0;

        private int _dirtyClicks = 0;
        private int _dispatchPending = 0;
        private readonly object _hookLock = new();

        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _kbdHook = IntPtr.Zero;
        private volatile uint _hookThreadNativeId = 0;
        private WinApi.LowLevelProc? _mouseProc;
        private WinApi.LowLevelProc? _kbdProc;

        private bool _timerPeriodActive = false;
        private readonly object _disposeLock = new();
        private bool _disposed = false;

        public event Action<bool>? StateChanged;
        public event Action? ClickPerformed;
        public event Action<long, long, long, long>? ClicksChanged; // totalLmb, totalRmb, sessionLmb, sessionRmb

        public long TotalLmbClicks => Interlocked.Read(ref _totalLmbClicks);
        public long TotalRmbClicks => Interlocked.Read(ref _totalRmbClicks);
        public long SessionLmbClicks => Interlocked.Read(ref _sessionLmbClicks);
        public long SessionRmbClicks => Interlocked.Read(ref _sessionRmbClicks);
        public long TotalAllClicks => TotalLmbClicks + TotalRmbClicks;
        public long SessionAllClicks => SessionLmbClicks + SessionRmbClicks;

        public bool IsClicking
        {
            get => _isClicking;
            private set
            {
                if (_isClicking != value)
                {
                    _isClicking = value;
                    try
                    {
                        StateChanged?.Invoke(_isClicking);
                    }
                    catch { }
                }
            }
        }

        public AutoClicker(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _totalLmbClicks = _settings.TotalLmbClicks;
            _totalRmbClicks = _settings.TotalRmbClicks;
        }

        public void ResetSessionClicks()
        {
            Interlocked.Exchange(ref _sessionLmbClicks, 0);
            Interlocked.Exchange(ref _sessionRmbClicks, 0);
            ClicksChanged?.Invoke(TotalLmbClicks, TotalRmbClicks, 0, 0);
        }

        public void FlushClicksToSettings()
        {
            try
            {
                _settings.TotalLmbClicks = Interlocked.Read(ref _totalLmbClicks);
                _settings.TotalRmbClicks = Interlocked.Read(ref _totalRmbClicks);
                _settings.Save();
            }
            catch { }
        }

        private const uint WM_UPDATE_HOOKS = WinApi.WM_APP + 101;

        public void UpdateHookState()
        {
            if (_hookThreadNativeId != 0)
            {
                WinApi.PostThreadMessage(_hookThreadNativeId, WM_UPDATE_HOOKS, UIntPtr.Zero, IntPtr.Zero);
            }
        }

        public void Start()
        {
            lock (_disposeLock)
            {
                if (_running || _disposed) return;

                if (WinApi.TimeBeginPeriod(1) == 0)
                {
                    _timerPeriodActive = true;
                }

                _running = true;

                _hookThread = new Thread(HookThreadWorker)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                    Name = "AutoClicker_HookThread"
                };
                _hookThread.Start();

                _clickThread = new Thread(ClickerLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                    Name = "AutoClicker_ClickThread"
                };
                _clickThread.Start();
            }
        }

        private void HookThreadWorker()
        {
            try
            {
                _hookThreadNativeId = WinApi.GetCurrentThreadId();
                _mouseProc = MouseHookCallback;
                _kbdProc = KeyboardHookCallback;

                UpdateHooksInternal();

                while (_running && WinApi.GetMessage(out WinApi.MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_UPDATE_HOOKS)
                    {
                        UpdateHooksInternal();
                        continue;
                    }

                    WinApi.TranslateMessage(ref msg);
                    WinApi.DispatchMessage(ref msg);
                }
            }
            catch { }
            finally
            {
                UnhookHandles();
                _hookThreadNativeId = 0;
            }
        }

        private void UpdateHooksInternal()
        {
            lock (_hookLock)
            {
                if (!_running)
                {
                    UnhookHandles();
                    return;
                }

                // Mouse hook is needed for AutoClicker OR Debounce
                bool needMouse = _settings.ClickerEnabled || _settings.DebounceEnabled;
                // Keyboard hook is ONLY needed if Clicker is enabled
                bool needKbd = _settings.ClickerEnabled;

                IntPtr hMod = WinApi.GetModuleHandle(null);

                if (needMouse)
                {
                    if (_mouseHook == IntPtr.Zero && _mouseProc != null)
                    {
                        _mouseHook = WinApi.SetWindowsHookEx(WinApi.WH_MOUSE_LL, _mouseProc, hMod, 0);
                    }
                }
                else
                {
                    if (_mouseHook != IntPtr.Zero)
                    {
                        WinApi.UnhookWindowsHookEx(_mouseHook);
                        _mouseHook = IntPtr.Zero;
                    }
                    _physicalLmbDown = false;
                }

                if (needKbd)
                {
                    if (_kbdHook == IntPtr.Zero && _kbdProc != null)
                    {
                        _kbdHook = WinApi.SetWindowsHookEx(WinApi.WH_KEYBOARD_LL, _kbdProc, hMod, 0);
                    }
                }
                else
                {
                    if (_kbdHook != IntPtr.Zero)
                    {
                        WinApi.UnhookWindowsHookEx(_kbdHook);
                        _kbdHook = IntPtr.Zero;
                    }
                    _physicalTriggerDown = false;
                    _toggleState = false;
                }
            }
        }

        private void UnhookHandles()
        {
            lock (_hookLock)
            {
                if (_mouseHook != IntPtr.Zero)
                {
                    WinApi.UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = IntPtr.Zero;
                }
                if (_kbdHook != IntPtr.Zero)
                {
                    WinApi.UnhookWindowsHookEx(_kbdHook);
                    _kbdHook = IntPtr.Zero;
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Fast-path: bypass non-button messages immediately to eliminate 1000Hz polling overhead.
            uint msg = (uint)wParam;
            if (msg != WinApi.WM_LBUTTONDOWN && msg != WinApi.WM_LBUTTONUP &&
                msg != WinApi.WM_RBUTTONDOWN && msg != WinApi.WM_RBUTTONUP)
            {
                return WinApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (nCode >= 0)
            {
                // MSLLHOOKSTRUCT: flags at offset 12
                uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                bool isPhysical = (flags & WinApi.LLMHF_INJECTED) == 0;

                if (isPhysical)
                {
                    long now = Environment.TickCount64;

                    if (msg == WinApi.WM_LBUTTONDOWN)
                    {
                        if (_settings.DebounceEnabled)
                        {
                            int debounceThreshold = Math.Clamp(_settings.DebounceMs, 5, 50);
                            long diffDown = now - _lastPhysicalLmbDownTick;
                            long diffUp = now - _lastPhysicalLmbUpTick;

                            if (diffDown < debounceThreshold || diffUp < debounceThreshold)
                            {
                                return (IntPtr)1; // Suppress phantom bounce
                            }
                        }

                        _lastPhysicalLmbDownTick = now;
                        _physicalLmbDown = true;

                        long totalL = Interlocked.Increment(ref _totalLmbClicks);
                        long sessL = Interlocked.Increment(ref _sessionLmbClicks);
                        long totalR = Interlocked.Read(ref _totalRmbClicks);
                        long sessR = Interlocked.Read(ref _sessionRmbClicks);

                        ScheduleClicksDispatch(totalL, totalR, sessL, sessR);
                    }
                    else if (msg == WinApi.WM_LBUTTONUP)
                    {
                        _lastPhysicalLmbUpTick = now;
                        _physicalLmbDown = false;
                    }
                    else if (msg == WinApi.WM_RBUTTONDOWN)
                    {
                        if (_settings.DebounceEnabled)
                        {
                            int debounceThreshold = Math.Clamp(_settings.DebounceMs, 5, 50);
                            long diffDown = now - _lastPhysicalRmbDownTick;
                            long diffUp = now - _lastPhysicalRmbUpTick;

                            if (diffDown < debounceThreshold || diffUp < debounceThreshold)
                            {
                                return (IntPtr)1; // Suppress phantom bounce
                            }
                        }

                        _lastPhysicalRmbDownTick = now;

                        long totalR = Interlocked.Increment(ref _totalRmbClicks);
                        long sessR = Interlocked.Increment(ref _sessionRmbClicks);
                        long totalL = Interlocked.Read(ref _totalLmbClicks);
                        long sessL = Interlocked.Read(ref _sessionLmbClicks);

                        ScheduleClicksDispatch(totalL, totalR, sessL, sessR);
                    }
                    else if (msg == WinApi.WM_RBUTTONUP)
                    {
                        _lastPhysicalRmbUpTick = now;
                    }
                }
            }

            return WinApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void ScheduleClicksDispatch(long totalLmb, long totalRmb, long sessionLmb, long sessionRmb)
        {
            if (Interlocked.Exchange(ref _dispatchPending, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        ClicksChanged?.Invoke(totalLmb, totalRmb, sessionLmb, sessionRmb);
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref _dispatchPending, 0);
                    }
                });
            }

            int dirty = Interlocked.Increment(ref _dirtyClicks);
            if (dirty >= 50)
            {
                Interlocked.Exchange(ref _dirtyClicks, 0);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _settings.TotalLmbClicks = Interlocked.Read(ref _totalLmbClicks);
                        _settings.TotalRmbClicks = Interlocked.Read(ref _totalRmbClicks);
                        _settings.Save();
                    }
                    catch { }
                });
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            uint msg = (uint)wParam;
            if (msg != WinApi.WM_KEYDOWN && msg != WinApi.WM_KEYUP && msg != WinApi.WM_SYSKEYDOWN && msg != WinApi.WM_SYSKEYUP)
            {
                return WinApi.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
            }

            if (nCode >= 0 && _settings.ClickerEnabled)
            {
                uint vkCode = (uint)Marshal.ReadInt32(lParam, 0); // KBDLLHOOKSTRUCT.vkCode (offset 0)
                if (vkCode == (uint)_settings.ClickerTriggerVk)
                {
                    uint flags = (uint)Marshal.ReadInt32(lParam, 8); // flags is at offset 8 (vkCode 4, scanCode 4)
                    if ((flags & WinApi.LLKHF_INJECTED) == 0)
                    {
                        if (msg == WinApi.WM_KEYDOWN || msg == WinApi.WM_SYSKEYDOWN)
                        {
                            if (!_physicalTriggerDown)
                            {
                                _physicalTriggerDown = true;
                                if (_settings.ClickerMode == "Toggle")
                                {
                                    _toggleState = !_toggleState;
                                    if (_settings.ClickerAudioFeedback)
                                    {
                                        PlayTone(_toggleState ? 900 : 500);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _physicalTriggerDown = false;
                        }
                    }
                }
            }
            return WinApi.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
        }

        private void ClickerLoop()
        {
            var sw = new Stopwatch();

            try
            {
                while (_running)
                {
                    if (!_settings.ClickerEnabled)
                    {
                        EnsureMouseReleased();
                        IsClicking = false;
                        _toggleState = false;
                        Thread.Sleep(50);
                        continue;
                    }

                    // Failsafe check: also check GetAsyncKeyState in case hook had a hitch
                    bool asyncTrigger = (WinApi.GetAsyncKeyState(_settings.ClickerTriggerVk) & 0x8000) != 0;
                    bool isTriggerActive = _physicalTriggerDown || asyncTrigger;

                    bool shouldClick;
                    if (_settings.ClickerMode == "Toggle")
                    {
                        bool asyncLmb = (WinApi.GetAsyncKeyState(WinApi.VK_LBUTTON) & 0x8000) != 0;
                        shouldClick = _toggleState && (_physicalLmbDown || asyncLmb);
                    }
                    else // Hold mode
                    {
                        bool asyncLmb = (WinApi.GetAsyncKeyState(WinApi.VK_LBUTTON) & 0x8000) != 0;
                        shouldClick = isTriggerActive && (_physicalLmbDown || asyncLmb);
                    }

                    if (shouldClick)
                    {
                        if (!IsClicking && _settings.ClickerAudioFeedback && _settings.ClickerMode != "Toggle")
                        {
                            PlayTone(850);
                        }
                        IsClicking = true;

                        // Calculate timing based on CPS (1 to 60 CPS)
                        int cps = Math.Clamp(_settings.ClickerCps, 1, 60);
                        double totalIntervalMs = 1000.0 / cps;

                        if (_settings.ClickerHumanizer && cps > 3)
                        {
                            // Add jitter +/- 2 to 4 ms
                            int jitter = Random.Shared.Next(-3, 4);
                            totalIntervalMs = Math.Max(12.0, totalIntervalMs + jitter);
                        }

                        int downDurationMs;
                        double upDurationMs;

                        if (cps <= 4)
                        {
                            // For slow rates (1-4 CPS), hold button down for clean 25ms, wait the rest
                            downDurationMs = 25;
                            upDurationMs = Math.Max(20.0, totalIntervalMs - downDurationMs);
                        }
                        else
                        {
                            downDurationMs = Math.Clamp((int)(totalIntervalMs * 0.4), 8, 18);
                            upDurationMs = Math.Max(6.0, totalIntervalMs - downDurationMs);
                        }

                        WinApi.mouse_event(WinApi.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        _isButtonDown = true;

                        sw.Restart();
                        while (sw.Elapsed.TotalMilliseconds < downDurationMs && _running)
                        {
                            Thread.Sleep(1);
                        }

                        WinApi.mouse_event(WinApi.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        _isButtonDown = false;

                        try
                        {
                            ClickPerformed?.Invoke();
                        }
                        catch { }

                        sw.Restart();
                        while (sw.Elapsed.TotalMilliseconds < upDurationMs && _running)
                        {
                            if (upDurationMs - sw.Elapsed.TotalMilliseconds > 2)
                                Thread.Sleep(1);
                            else
                                Thread.SpinWait(40);
                        }
                    }
                    else
                    {
                        if (IsClicking || _isButtonDown)
                        {
                            EnsureMouseReleased();
                            IsClicking = false;
                        }
                        Thread.Sleep(6);
                    }
                }
            }
            catch { }
            finally
            {
                EnsureMouseReleased();
                IsClicking = false;
            }
        }

        private void EnsureMouseReleased()
        {
            if (_isButtonDown)
            {
                _isButtonDown = false;
                WinApi.mouse_event(WinApi.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
        }

        private void PlayTone(int frequency)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Console.Beep(frequency, 35);
                }
                catch { }
            });
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;
                _running = false;
            }

            EnsureMouseReleased();
            IsClicking = false;
            _toggleState = false;

            if (_hookThreadNativeId != 0)
            {
                WinApi.PostThreadMessage(_hookThreadNativeId, WinApi.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            }

            try { _hookThread?.Join(300); } catch { }
            try { _clickThread?.Join(300); } catch { }

            UnhookHandles();
            EnsureMouseReleased();
            FlushClicksToSettings();

            if (_timerPeriodActive)
            {
                WinApi.TimeEndPeriod(1);
                _timerPeriodActive = false;
            }
        }
    }
}
