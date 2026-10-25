using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using MouseControl.Models;
using MouseControl.Native;
using MouseControl.Services;

namespace MouseControl
{
    /// <summary>
    /// Application entrypoint, single-instance mutex coordinator, and service orchestrator.
    /// Manages non-blocking UI dispatching, clean lifecycle termination, and error logging.
    /// </summary>
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private readonly object _exitLock = new();
        private bool _isExiting = false;

        public AppSettings Settings { get; private set; } = null!;
        public MouseDriver MouseDriver { get; private set; } = null!;
        public AutoClicker AutoClicker { get; private set; } = null!;
        public NotificationManager NotificationManager { get; private set; } = null!;
        public TrayIconManager TrayManager { get; private set; } = null!;
        public MainWindow MainWindowInstance { get; private set; } = null!;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MouseControl", "app.log");

            void Log(string msg)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
                }
                catch { }
            }

            Log("Application_Startup started");
            DispatcherUnhandledException += (s, ev) =>
            {
                Log($"Dispatcher unhandled: {ev.Exception}");
                ev.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                Log($"Unhandled exception: {ev.ExceptionObject}");
            };

            const string appGuid = "MouseControl_Unique_SingleInstance_Guid_2026";
            _mutex = new Mutex(true, appGuid, out bool createdNew);

            if (!createdNew)
            {
                Log("Another instance already running. Exiting.");
                Shutdown();
                return;
            }

            Log("Mutex acquired. Initializing components...");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Settings = AppSettings.Load();
            ThemeManager.ApplyTheme(Settings.Theme);

            MouseDriver = new MouseDriver();
            AutoClicker = new AutoClicker(Settings);
            NotificationManager = new NotificationManager(Settings);

            TrayManager = new TrayIconManager(
                Settings,
                onOpenRequested: () =>
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindowInstance == null)
                        {
                            MainWindowInstance = new MainWindow();
                        }
                        MainWindowInstance.Show();
                        MainWindowInstance.WindowState = WindowState.Normal;
                        MainWindowInstance.Activate();
                    });
                },
                onExitRequested: () =>
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    Dispatcher.BeginInvoke(ExitApplication);
                },
                onToggleClicker: () =>
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        Settings.ClickerEnabled = !Settings.ClickerEnabled;
                        Settings.Save();
                        AutoClicker?.UpdateHookState();
                        MainWindowInstance?.SyncClickerState();
                    });
                }
            );

            NotificationManager.SetNotifyIcon(TrayManager.NotifyIcon);

            MouseDriver.BatteryUpdated += (percentage, isCharging, isConnected, isWired) =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

                        TrayManager?.UpdateIcon(percentage, isCharging, isConnected, isWired);
                        NotificationManager?.ProcessBatteryUpdate(percentage, isCharging, isConnected);
                        MainWindowInstance?.UpdateBatteryDisplay(percentage, isCharging, isConnected, isWired);
                    });
                }
                catch { }
            };

            AutoClicker.Start();
            MouseDriver.Initialize();

            Log("Components initialized. Creating MainWindow...");
            MainWindowInstance = new MainWindow();
            MainWindow = MainWindowInstance;
            Log("MainWindow created.");

            bool startMinimized = e.Args.Contains("--minimized");
            Log($"startMinimized: {startMinimized}");
            if (!startMinimized)
            {
                MainWindowInstance.Show();
                Log("MainWindow.Show() called.");
            }
            Log("Application_Startup finished.");
        }

        public void ExitApplication()
        {
            lock (_exitLock)
            {
                if (_isExiting) return;
                _isExiting = true;
            }

            try { Settings?.Save(); } catch { }
            try { AutoClicker?.Dispose(); } catch { }
            try { MouseDriver?.Dispose(); } catch { }
            try { TrayManager?.Dispose(); } catch { }

            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                try { _mutex.Dispose(); } catch { }
                _mutex = null;
            }

            Shutdown();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MouseControl", "app.log");
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] Application_Exit called with code: {e.ApplicationExitCode}\n"); } catch { }
            Settings?.Save();
        }
    }
}
