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
        private int _selectedDpiStage = 2;
        private bool _isUpdatingDpiUi = false;

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
            TxtKeyName.Text = _settings.ClickerTriggerKeyName;

            if (_settings.ClickerMode == "Toggle")
                RadioModeToggle.IsChecked = true;
            else
                RadioModeHold.IsChecked = true;

            // Load Sensor & DPI Settings
            _selectedDpiStage = _settings.SensorActiveDpiStage;
            UpdateDpiStagesDisplay();

            switch (_settings.SensorPollingRate)
            {
                case 125: RadioRate125.IsChecked = true; break;
                case 250: RadioRate250.IsChecked = true; break;
                case 500: RadioRate500.IsChecked = true; break;
                case 1000: default: RadioRate1000.IsChecked = true; break;
            }

            if (_settings.SensorLod == "2mm")
                RadioLod2mm.IsChecked = true;
            else
                RadioLod1mm.IsChecked = true;

            SliderSensorDebounce.Value = _settings.SensorKeyDebounceMs;
            TxtSensorDebounceVal.Text = $"{_settings.SensorKeyDebounceMs} мс";

            ChkMotionSync.IsChecked = _settings.SensorMotionSync;
            ChkRippleControl.IsChecked = _settings.SensorRippleControl;
            ChkAngleSnapping.IsChecked = _settings.SensorAngleSnapping;

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

        private void TabBtnSensor_Click(object sender, RoutedEventArgs e)
        {
            SwitchTab(PanelSensor, TabBtnSensor);
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
            PanelSensor.Visibility = Visibility.Collapsed;
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
            ApplyTabStyle(TabBtnSensor, _activeTabButton == TabBtnSensor);
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
            UpdateDpiStagesDisplay();
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

        #region Sensor & DPI Implementation

        private void UpdateDpiStagesDisplay()
        {
            if (_settings.SensorDpiStages == null || _settings.SensorDpiStages.Length != 6)
                _settings.SensorDpiStages = new int[] { 400, 800, 1600, 2400, 3200, 6400 };

            TxtStageDpi1.Text = _settings.SensorDpiStages[0].ToString();
            TxtStageDpi2.Text = _settings.SensorDpiStages[1].ToString();
            TxtStageDpi3.Text = _settings.SensorDpiStages[2].ToString();
            TxtStageDpi4.Text = _settings.SensorDpiStages[3].ToString();
            TxtStageDpi5.Text = _settings.SensorDpiStages[4].ToString();
            TxtStageDpi6.Text = _settings.SensorDpiStages[5].ToString();

            void HighlightCard(Border card, int stageNum)
            {
                bool isActive = _settings.SensorActiveDpiStage == stageNum;
                bool isSelected = _selectedDpiStage == stageNum;

                if (isActive)
                {
                    card.SetResourceReference(Border.BackgroundProperty, "HeaderBackgroundBrush");
                    card.SetResourceReference(Border.BorderBrushProperty, "AccentColorBrush");
                    card.BorderThickness = new Thickness(2);
                }
                else if (isSelected)
                {
                    card.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
                    card.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush");
                    card.BorderThickness = new Thickness(1);
                }
                else
                {
                    card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
                    card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    card.BorderThickness = new Thickness(1);
                }
            }

            HighlightCard(CardStage1, 1);
            HighlightCard(CardStage2, 2);
            HighlightCard(CardStage3, 3);
            HighlightCard(CardStage4, 4);
            HighlightCard(CardStage5, 5);
            HighlightCard(CardStage6, 6);

            int activeStage = Math.Clamp(_settings.SensorActiveDpiStage, 1, 6);
            int activeDpi = _settings.SensorDpiStages[activeStage - 1];
            TxtActiveDpiSummary.Text = $"Ступень {activeStage}: {activeDpi} DPI";

            int selStage = Math.Clamp(_selectedDpiStage, 1, 6);
            int currentVal = _settings.SensorDpiStages[selStage - 1];
            TxtSelectedStageTitle.Text = $"Настройка ступени {selStage}:";
            TxtCurrentStageDpiVal.Text = $"{currentVal} DPI";

            _isUpdatingDpiUi = true;
            SliderDpi.Value = currentVal;
            _isUpdatingDpiUi = false;
        }

        private void DpiStageCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag != null && int.TryParse(border.Tag.ToString(), out int stage))
            {
                _selectedDpiStage = stage;
                _settings.SensorActiveDpiStage = stage;
                _settings.Save();
                UpdateDpiStagesDisplay();
                TxtSensorApplyStatus.Text = $"✓ Активна ступень {stage} ({_settings.SensorDpiStages[stage - 1]} DPI)";
            }
        }

        private void SliderDpi_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingDpiUi || !IsLoaded) return;

            int dpi = (int)(Math.Round(SliderDpi.Value / 50.0) * 50);
            dpi = Math.Clamp(dpi, 100, 12000);

            int stageIdx = Math.Clamp(_selectedDpiStage - 1, 0, 5);
            _settings.SensorDpiStages[stageIdx] = dpi;
            TxtCurrentStageDpiVal.Text = $"{dpi} DPI";

            switch (_selectedDpiStage)
            {
                case 1: TxtStageDpi1.Text = dpi.ToString(); break;
                case 2: TxtStageDpi2.Text = dpi.ToString(); break;
                case 3: TxtStageDpi3.Text = dpi.ToString(); break;
                case 4: TxtStageDpi4.Text = dpi.ToString(); break;
                case 5: TxtStageDpi5.Text = dpi.ToString(); break;
                case 6: TxtStageDpi6.Text = dpi.ToString(); break;
            }

            if (_selectedDpiStage == _settings.SensorActiveDpiStage)
            {
                TxtActiveDpiSummary.Text = $"Ступень {_selectedDpiStage}: {dpi} DPI";
            }
        }

        private void SliderDpi_DragCompleted(object sender, MouseButtonEventArgs e)
        {
            _settings.Save();
        }

        private void BtnDpiPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null && int.TryParse(btn.Tag.ToString(), out int presetDpi))
            {
                SliderDpi.Value = presetDpi;
                _settings.Save();
            }
        }

        private void RadioRate_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (RadioRate125.IsChecked == true) _settings.SensorPollingRate = 125;
            else if (RadioRate250.IsChecked == true) _settings.SensorPollingRate = 250;
            else if (RadioRate500.IsChecked == true) _settings.SensorPollingRate = 500;
            else if (RadioRate1000.IsChecked == true) _settings.SensorPollingRate = 1000;

            _settings.Save();
            TxtSensorApplyStatus.Text = $"Частота опроса: {_settings.SensorPollingRate} Гц";
        }

        private void RadioLod_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _settings.SensorLod = RadioLod2mm.IsChecked == true ? "2mm" : "1mm";
            _settings.Save();
            TxtSensorApplyStatus.Text = $"Высота отрыва (LOD): {_settings.SensorLod}";
        }

        private void SliderSensorDebounce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            int ms = (int)SliderSensorDebounce.Value;
            TxtSensorDebounceVal.Text = $"{ms} мс";
            _settings.SensorKeyDebounceMs = ms;
            _settings.Save();
        }

        private void SensorFeature_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _settings.SensorMotionSync = ChkMotionSync.IsChecked == true;
            _settings.SensorRippleControl = ChkRippleControl.IsChecked == true;
            _settings.SensorAngleSnapping = ChkAngleSnapping.IsChecked == true;
            _settings.Save();
        }

        private void BtnApplySensor_Click(object sender, RoutedEventArgs e)
        {
            _settings.Save();
            TxtSensorApplyStatus.Text = "✓ Все настройки сенсора успешно применены!";
            TxtSensorApplyStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentColorBrush");

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (s, ev) =>
            {
                timer.Stop();
                TxtSensorApplyStatus.Text = "Все настройки сенсора синхронизированы";
                TxtSensorApplyStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            };
            timer.Start();
        }

        private void BtnResetSensorDefaults_Click(object sender, RoutedEventArgs e)
        {
            _settings.SensorDpiStages = new int[] { 400, 800, 1600, 2400, 3200, 6400 };
            _settings.SensorActiveDpiStage = 2; // 800 DPI
            _settings.SensorPollingRate = 1000;
            _settings.SensorLod = "1mm";
            _settings.SensorKeyDebounceMs = 4;
            _settings.SensorMotionSync = true;
            _settings.SensorRippleControl = false;
            _settings.SensorAngleSnapping = false;
            _settings.Save();

            _selectedDpiStage = 2;
            UpdateDpiStagesDisplay();

            RadioRate1000.IsChecked = true;
            RadioLod1mm.IsChecked = true;
            SliderSensorDebounce.Value = 4;
            TxtSensorDebounceVal.Text = "4 мс";
            ChkMotionSync.IsChecked = true;
            ChkRippleControl.IsChecked = false;
            ChkAngleSnapping.IsChecked = false;

            TxtSensorApplyStatus.Text = "✓ Восстановлены заводские параметры (800 DPI, 1000 Гц, 1 мм)";
            TxtSensorApplyStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentColorBrush");
        }

        #endregion
    }
}
