using System;
using System.Media;
using System.Windows.Forms;
using MouseControl.Models;

namespace MouseControl.Services
{
    /// <summary>
    /// Handles user notifications for battery thresholds and charging status.
    /// Defensively handles audio playback and Windows Shell balloon tip limitations.
    /// </summary>
    public class NotificationManager
    {
        private readonly AppSettings _settings;
        private NotifyIcon? _notifyIcon;

        private bool _warnedThreshold1 = false;
        private bool _warnedThreshold2 = false;
        private bool _warnedFullCharge = false;

        public NotificationManager(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void SetNotifyIcon(NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
        }

        public void ProcessBatteryUpdate(int percentage, bool isCharging, bool isConnected)
        {
            if (!isConnected || percentage < 0) return;

            if (isCharging)
            {
                // Reset low warnings when connected to cable
                _warnedThreshold1 = false;
                _warnedThreshold2 = false;

                if (percentage >= 100 && !_warnedFullCharge)
                {
                    _warnedFullCharge = true;
                    if (_settings.BatteryNotifyFullCharge)
                    {
                        ShowNotification(
                            "Батарея полностью заряжена",
                            "Мышь заряжена на 100%. Можно отключить кабель зарядки.",
                            ToolTipIcon.Info
                        );
                    }
                }
            }
            else
            {
                // Discharging
                _warnedFullCharge = false;

                // Threshold 2 (Critical, e.g. <= 10%)
                if (percentage <= _settings.BatteryWarningThreshold2)
                {
                    if (!_warnedThreshold2)
                    {
                        _warnedThreshold2 = true;
                        _warnedThreshold1 = true; // also mark threshold 1 as triggered

                        TriggerLowBatteryAlert(
                            "Критический разряд мышки!",
                            $"Осталось всего {percentage}%! Мышь скоро отключится, срочно подключите кабель.",
                            ToolTipIcon.Error
                        );
                    }
                }
                // Threshold 1 (Warning, e.g. <= 20%)
                else if (percentage <= _settings.BatteryWarningThreshold1)
                {
                    if (!_warnedThreshold1)
                    {
                        _warnedThreshold1 = true;

                        TriggerLowBatteryAlert(
                            "Низкий заряд батареи мышки",
                            $"Осталось {percentage}%. Рекомендуется подключить кабель для зарядки.",
                            ToolTipIcon.Warning
                        );
                    }
                }
                else
                {
                    // Battery is healthy above thresholds
                    _warnedThreshold1 = false;
                    _warnedThreshold2 = false;
                }
            }
        }

        private void TriggerLowBatteryAlert(string title, string text, ToolTipIcon icon)
        {
            if (_settings.BatterySoundAlert)
            {
                try
                {
                    SystemSounds.Exclamation.Play();
                }
                catch
                {
                    // Audio service or sound playback failure ignored safely
                }
            }

            if (_settings.BatteryNotificationAlert)
            {
                ShowNotification(title, text, icon);
            }
        }

        public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try
            {
                if (_notifyIcon != null && _notifyIcon.Visible)
                {
                    string safeTitle = string.IsNullOrWhiteSpace(title) ? "Mouse Control" : title;
                    string safeText = text ?? string.Empty;

                    // Shell balloon tip limits
                    if (safeTitle.Length > 63) safeTitle = safeTitle.Substring(0, 63);
                    if (safeText.Length > 255) safeText = safeText.Substring(0, 255);

                    _notifyIcon.ShowBalloonTip(4000, safeTitle, safeText, icon);
                }
            }
            catch
            {
                // System notification / balloon tip service failure ignored safely
            }
        }
    }
}
