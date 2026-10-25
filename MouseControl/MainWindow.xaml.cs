using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MouseControl.Models;
using MouseControl.Services;

namespace MouseControl
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private bool _isCapturingKey = false;
        private int _testClickCounter = 0;
        private Button _activeTabButton = null!;

        public MainWindow()
        {
            InitializeComponent();

            _settings = ((App)Application.Current).Settings;
            _activeTabButton = TabBtnBattery;

            LoadSettingsIntoUi();
            UpdateTabButtonsVisual();

            var clicker = ((App)Application.Current).AutoClicker;
            clicker.StateChanged += isClicking =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (isClicking)
                    {
                        TxtClickerStatus.Text = "АКТИВЕН (СПАМ КЛИКОВ)";
                        TxtClickerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    }
                    else
                    {
                        TxtClickerStatus.Text = _settings.ClickerEnabled ? "Готов к работе (активация клавишей)" : "Отключен";
                        TxtClickerStatus.Foreground = (Brush)FindResource("AccentColorBrush");
                    }
                });
            };

            clicker.ClickPerformed += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    _testClickCounter++;
                    TxtTestClickCount.Text = _testClickCounter.ToString();
                });
            };

            clicker.ClicksChanged += (totalL, totalR, sessL, sessR) =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(() => UpdateClicksDisplay(totalL, totalR, sessL, sessR));
            };
            UpdateClicksDisplay(clicker.TotalLmbClicks, clicker.TotalRmbClicks, clicker.SessionLmbClicks, clicker.SessionRmbClicks);

            var driver = ((App)Application.Current).MouseDriver;
            UpdateBatteryDisplay(driver.CurrentBattery, driver.IsCharging, driver.IsConnected, driver.IsWired);
            if (!driver.IsConnected)
            {
                driver.RefreshHardwareState();
            }

            KeyDown += MainWindow_KeyDown;
        }

        private void LoadSettingsIntoUi()
        {
            SliderThresh1.Value = _settings.BatteryWarningThreshold1;
            TxtThresh1Val.Text = $"{_settings.BatteryWarningThreshold1}%";
            SliderThresh2.Value = _settings.BatteryWarningThreshold2;
            TxtThresh2Val.Text = $"{_settings.BatteryWarningThreshold2}%";

            ChkSoundAlert.IsChecked = _settings.BatterySoundAlert;
            ChkToastAlert.IsChecked = _settings.BatteryNotificationAlert;
            ChkFullChargeAlert.IsChecked = _settings.BatteryNotifyFullCharge;

            ChkClickerMaster.IsChecked = _settings.ClickerEnabled;
            SliderCps.Value = _settings.ClickerCps;
            TxtCpsVal.Text = $"{_settings.ClickerCps} CPS ({(int)(1000.0 / _settings.ClickerCps)} мс)";
            ChkHumanizer.IsChecked = _settings.ClickerHumanizer;
            ChkClickerAudio.IsChecked = _settings.ClickerAudioFeedback;
            TxtKeyName.Text = _settings.ClickerTriggerKeyName;

            if (_settings.ClickerMode == "Toggle")
                RadioModeToggle.IsChecked = true;
            else
                RadioModeHold.IsChecked = true;

            ChkDebounceMaster.IsChecked = _settings.DebounceEnabled;
            SliderDebounce.Value = _settings.DebounceMs;
            TxtDebounceVal.Text = $"{_settings.DebounceMs} мс{(_settings.DebounceMs == 20 ? " (рекомендуется для Huano)" : "")}";

            ChkStartup.IsChecked = StartupManager.IsStartupEnabled();
            ChkMinimizeOnClose.IsChecked = _settings.MinimizeToTrayOnClose;

            if (_settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
            {
                RadioThemeLight.IsChecked = true;
                TxtThemeIcon.Text = "☀️";
            }
            else
            {
                RadioThemeDark.IsChecked = true;
                TxtThemeIcon.Text = "🌙";
            }
        }

        private int _currentDisplayPercent = 0;
        private Brush? _currentBatteryBrush = null;
        private double _currentLifespanPct = 0;

        private void ProgTrackGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateBatteryBarVisual();
            UpdateLifespanBarVisual();
        }

        private void LifespanTrackGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLifespanBarVisual();
        }

        private void UpdateClicksDisplay(long totalLmb, long totalRmb, long sessionLmb, long sessionRmb)
        {
            long total = totalLmb + totalRmb;
            long session = sessionLmb + sessionRmb;

            TxtTotalClicksVal.Text = $"{total:N0} кликов".Replace(',', ' ');
            double pct = (total / 80_000_000.0) * 100.0;
            TxtLifespanUsedVal.Text = $"{pct:F3}% ресурса (80 млн)";

            TxtSessionClicksVal.Text = $"{session:N0} кликов".Replace(',', ' ');
            TxtSessionBreakdownVal.Text = $"ЛКМ: {sessionLmb:N0} · ПКМ: {sessionRmb:N0}".Replace(',', ' ');

            _currentLifespanPct = pct;
            UpdateLifespanBarVisual();
        }

        private void UpdateLifespanBarVisual()
        {
            if (LifespanTrackGrid == null || LifespanBarFill == null) return;
            double trackWidth = LifespanTrackGrid.ActualWidth;
            if (trackWidth <= 0) return;

            double fillWidth = Math.Clamp(trackWidth * (_currentLifespanPct / 100.0), (_currentLifespanPct > 0 ? 3 : 0), trackWidth);
            LifespanBarFill.Width = fillWidth;
        }

        private void BtnResetSessionClicks_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).AutoClicker?.ResetSessionClicks();
        }

        private void SetBatteryFill(int percent, Brush brush)
        {
            _currentDisplayPercent = percent;
            _currentBatteryBrush = brush;
            UpdateBatteryBarVisual();
            UpdateLifespanBarVisual();
        }

        private void UpdateBatteryBarVisual()
        {
            if (ProgTrackGrid == null || BatteryBarFill == null) return;
            double trackWidth = ProgTrackGrid.ActualWidth - 2;
            if (trackWidth <= 0) return;

            double fillWidth = Math.Clamp(trackWidth * (_currentDisplayPercent / 100.0), 0, trackWidth);
            BatteryBarFill.Width = fillWidth;
            if (_currentBatteryBrush != null)
            {
                BatteryBarFill.Background = _currentBatteryBrush;
            }
        }

        public void UpdateBatteryDisplay(int percentage, bool isCharging, bool isConnected, bool isWired)
        {
            if (!isConnected)
            {
                TxtBatteryPercent.Text = "--%";
                TxtBatteryStatusSmall.Text = "В РЕЖИМЕ СНА";
                var mutedBrush = (Brush)FindResource("TextMutedBrush");
                TxtBatteryStatusSmall.Foreground = mutedBrush;
                IconBadgeCharging.Visibility = Visibility.Collapsed;
                IconBadgeDot.Visibility = Visibility.Visible;
                IconBadgeDot.Fill = mutedBrush;

                TxtConnectionTitle.Text = "Мышь отключена или находится в режиме сна";
                TxtBatteryModeDesc.Text = "Пошевелите мышь или проверьте подключение";
                SetBatteryFill(0, mutedBrush);
                return;
            }

            int displayPercent = Math.Clamp(percentage, 0, 100);
            TxtBatteryPercent.Text = $"{displayPercent}%";

            if (isWired || isCharging)
            {
                TxtBatteryStatusSmall.Text = "ЗАРЯДКА ПО КАБЕЛЮ";
                var greenBrush = (Brush)FindResource("AccentColorBrush");
                TxtBatteryStatusSmall.Foreground = greenBrush;
                IconBadgeCharging.Visibility = Visibility.Visible;
                IconBadgeCharging.Fill = greenBrush;
                IconBadgeDot.Visibility = Visibility.Collapsed;

                TxtConnectionTitle.Text = "Мышь подключена (кабель USB)";
                TxtBatteryModeDesc.Text = "Режим: Проводной / Зарядка • Неограниченно";
                SetBatteryFill(displayPercent, greenBrush);
            }
            else
            {
                int hours = (int)Math.Round((displayPercent / 100.0) * 45.0);
                string hoursStr = FormatHours(hours);

                TxtConnectionTitle.Text = "Мышь подключена (2.4G Wireless)";
                TxtBatteryModeDesc.Text = $"Осталось примерно ~{hoursStr} работы (при 1000 Гц)";
                IconBadgeCharging.Visibility = Visibility.Collapsed;
                IconBadgeDot.Visibility = Visibility.Visible;

                if (displayPercent > 25)
                {
                    TxtBatteryStatusSmall.Text = "РАБОТАЕТ (2.4G)";
                    var greenBrush = (Brush)FindResource("AccentColorBrush");
                    TxtBatteryStatusSmall.Foreground = greenBrush;
                    IconBadgeDot.Fill = greenBrush;
                    SetBatteryFill(displayPercent, greenBrush);
                }
                else if (displayPercent > 10)
                {
                    TxtBatteryStatusSmall.Text = "СРЕДНИЙ ЗАРЯД";
                    var amberBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    amberBrush.Freeze();
                    TxtBatteryStatusSmall.Foreground = amberBrush;
                    IconBadgeDot.Fill = amberBrush;
                    SetBatteryFill(displayPercent, amberBrush);
                }
                else
                {
                    TxtBatteryStatusSmall.Text = "НИЗКИЙ ЗАРЯД!";
                    var redBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    redBrush.Freeze();
                    TxtBatteryStatusSmall.Foreground = redBrush;
                    IconBadgeDot.Fill = redBrush;
                    SetBatteryFill(displayPercent, redBrush);
                }
            }
        }

        private static string FormatHours(int hours)
        {
            int rem100 = hours % 100;
            int rem10 = hours % 10;
            if (rem100 >= 11 && rem100 <= 19) return $"{hours} часов";
            if (rem10 == 1) return $"{hours} час";
            if (rem10 >= 2 && rem10 <= 4) return $"{hours} часа";
            return $"{hours} часов";
        }

        public void SyncClickerState()
        {
            ChkClickerMaster.IsChecked = _settings.ClickerEnabled;
            TxtClickerStatus.Text = _settings.ClickerEnabled ? "Готов к работе (активация клавишей)" : "Отключен";
        }

        // Title Bar Dragging & Window Controls
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_settings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                base.OnClosing(e);
                ((App)Application.Current).ExitApplication();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.MinimizeToTrayOnClose)
            {
                Hide();
            }
            else
            {
                ((App)Application.Current).ExitApplication();
            }
        }

        private void TabBtnBattery_Click(object sender, RoutedEventArgs e)
        {
            SwitchTab(PanelBattery, TabBtnBattery);
        }

        private void TabBtnClicker_Click(object sender, RoutedEventArgs e)
        {
            SwitchTab(PanelClicker, TabBtnClicker);
        }

        private void TabBtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchTab(PanelSettings, TabBtnSettings);
        }

        private void SwitchTab(FrameworkElement panelToShow, Button tabBtn)
        {
            _activeTabButton = tabBtn;

            PanelBattery.Visibility = Visibility.Collapsed;
            PanelClicker.Visibility = Visibility.Collapsed;
            PanelSettings.Visibility = Visibility.Collapsed;

            panelToShow.Visibility = Visibility.Visible;
            UpdateTabButtonsVisual();
        }

        private void UpdateTabButtonsVisual()
        {
            void ApplyTabStyle(Button btn, bool isSelected)
            {
                if (isSelected)
                {
                    btn.SetResourceReference(BackgroundProperty, "TabSelectedBrush");
                    btn.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                    btn.SetResourceReference(BorderBrushProperty, "CardBorderBrush");
                }
                else
                {
                    btn.SetResourceReference(BackgroundProperty, "TabUnselectedBrush");
                    btn.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
                    btn.SetResourceReference(BorderBrushProperty, "TabUnselectedBrush");
                }
            }

            ApplyTabStyle(TabBtnBattery, _activeTabButton == TabBtnBattery);
            ApplyTabStyle(TabBtnClicker, _activeTabButton == TabBtnClicker);
            ApplyTabStyle(TabBtnSettings, _activeTabButton == TabBtnSettings);
        }

        // Settings change handlers
        private void SliderThresh1_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            int val = (int)SliderThresh1.Value;
            TxtThresh1Val.Text = $"{val}%";
            _settings.BatteryWarningThreshold1 = val;
            _settings.Save();
        }

        private void SliderThresh2_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            int val = (int)SliderThresh2.Value;
            TxtThresh2Val.Text = $"{val}%";
            _settings.BatteryWarningThreshold2 = val;
            _settings.Save();
        }

        private void SliderCps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            int cps = (int)SliderCps.Value;
            int interval = (int)(1000.0 / cps);
            TxtCpsVal.Text = $"{cps} CPS ({interval} мс)";
            _settings.ClickerCps = cps;
            _settings.Save();
        }

        private void SettingChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _settings.BatterySoundAlert = ChkSoundAlert.IsChecked == true;
            _settings.BatteryNotificationAlert = ChkToastAlert.IsChecked == true;
            _settings.BatteryNotifyFullCharge = ChkFullChargeAlert.IsChecked == true;
            _settings.ClickerHumanizer = ChkHumanizer.IsChecked == true;
            _settings.ClickerAudioFeedback = ChkClickerAudio.IsChecked == true;
            _settings.MinimizeToTrayOnClose = ChkMinimizeOnClose.IsChecked == true;
            _settings.Save();
        }

        private void ChkClickerMaster_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _settings.ClickerEnabled = ChkClickerMaster.IsChecked == true;
            _settings.Save();
            ((App)Application.Current).AutoClicker?.UpdateHookState();
            ((App)Application.Current).TrayManager?.UpdateClickerMenuState(_settings.ClickerEnabled);
            TxtClickerStatus.Text = _settings.ClickerEnabled ? "Готов к работе (активация клавишей)" : "Отключен";
        }

        private void RadioMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _settings.ClickerMode = RadioModeToggle.IsChecked == true ? "Toggle" : "Hold";
            _settings.Save();
        }

        private void ChkDebounceMaster_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _settings.DebounceEnabled = ChkDebounceMaster.IsChecked == true;
            _settings.Save();
            ((App)Application.Current).AutoClicker?.UpdateHookState();
        }

        private void SliderDebounce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            int val = (int)SliderDebounce.Value;
            TxtDebounceVal.Text = $"{val} мс{(val == 20 ? " (рекомендуется для Huano)" : "")}";
            _settings.DebounceMs = val;
            _settings.Save();
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            bool isDark = _settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
            string newTheme = isDark ? "Light" : "Dark";
            SetTheme(newTheme);
        }

        private void RadioTheme_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            string newTheme = RadioThemeLight.IsChecked == true ? "Light" : "Dark";
            SetTheme(newTheme);
        }

        private void SetTheme(string theme)
        {
            _settings.Theme = theme;
            _settings.Save();
            ThemeManager.ApplyTheme(theme);

            bool isLight = theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
            TxtThemeIcon.Text = isLight ? "☀️" : "🌙";
            RadioThemeLight.IsChecked = isLight;
            RadioThemeDark.IsChecked = !isLight;

            UpdateTabButtonsVisual();
            if (_currentBatteryBrush != null)
            {
                _currentBatteryBrush = (Brush)FindResource("AccentColorBrush");
            }
            UpdateBatteryBarVisual();
            UpdateLifespanBarVisual();
        }

        private void ChkStartup_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool enable = ChkStartup.IsChecked == true;
            StartupManager.SetStartup(enable);
            _settings.StartWithWindows = enable;
            _settings.Save();
        }

        private void BtnChangeKey_Click(object sender, RoutedEventArgs e)
        {
            _isCapturingKey = true;
            TxtKeyName.Text = "Нажмите клавишу...";
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (_isCapturingKey)
            {
                _isCapturingKey = false;
                Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

                if (key == Key.Escape)
                {
                    TxtKeyName.Text = _settings.ClickerTriggerKeyName;
                    return;
                }

                int vk = KeyInterop.VirtualKeyFromKey(key);
                string keyText = key.ToString();

                if (vk == 0xC0) keyText = "Ё ( ~ )";
                else if (vk == 0x14) keyText = "Caps Lock";
                else if (vk == 0x12) keyText = "Alt";
                else if (vk == 0x11) keyText = "Ctrl";
                else if (vk == 0x10) keyText = "Shift";

                _settings.ClickerTriggerVk = vk;
                _settings.ClickerTriggerKeyName = keyText;
                _settings.Save();

                TxtKeyName.Text = keyText;
                e.Handled = true;
            }
        }

        private void BtnTestClick_Click(object sender, RoutedEventArgs e)
        {
            _testClickCounter++;
            TxtTestClickCount.Text = _testClickCounter.ToString();
        }

        private void BtnResetTestClick_Click(object sender, RoutedEventArgs e)
        {
            _testClickCounter = 0;
            TxtTestClickCount.Text = "0";
        }

        private void BtnOpenBackup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\Original_Driver"));
                if (!Directory.Exists(path))
                    path = Path.GetFullPath("Original_Driver");

                if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
