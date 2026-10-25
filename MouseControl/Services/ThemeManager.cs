using System;
using System.Windows;
using System.Windows.Media;

namespace MouseControl.Services
{
    /// <summary>
    /// Manages application-wide WPF themes (Dark Precision / Warm Minimalist Light).
    /// Thread-safe resource dictionary updates with frozen brush allocations for optimal WPF rendering.
    /// High-contrast palettes ensuring maximum readability and anti-slop elegance.
    /// </summary>
    public static class ThemeManager
    {
        public static void ApplyTheme(string themeName)
        {
            var app = Application.Current;
            if (app == null) return;

            if (app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => ApplyTheme(themeName));
                return;
            }

            bool isLight = themeName.Equals("Light", StringComparison.OrdinalIgnoreCase);

            if (isLight)
            {
                // Pastel Beige-Gray (Warm Minimalist, high contrast)
                SetBrush(app, "WindowBackgroundBrush", Color.FromRgb(0xF7, 0xF5, 0xF0));
                SetBrush(app, "CardBackgroundBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
                SetBrush(app, "CardBorderBrush", Color.FromRgb(0xDC, 0xD7, 0xCB));
                SetBrush(app, "CardHoverBrush", Color.FromRgb(0xF2, 0xEF, 0xE7));
                SetBrush(app, "TextPrimaryBrush", Color.FromRgb(0x18, 0x18, 0x1B)); // Near-black
                SetBrush(app, "TextSecondaryBrush", Color.FromRgb(0x4B, 0x55, 0x63)); // High-contrast slate
                SetBrush(app, "TextMutedBrush", Color.FromRgb(0x6B, 0x72, 0x80));
                SetBrush(app, "AccentColorBrush", Color.FromRgb(0x05, 0x96, 0x69)); // Emerald
                SetBrush(app, "AccentHoverBrush", Color.FromRgb(0x04, 0x78, 0x57));
                SetBrush(app, "AccentTextBrush", Colors.White);
                SetBrush(app, "HeaderBackgroundBrush", Color.FromRgb(0xEE, 0xEB, 0xE2));
                SetBrush(app, "TrackBrush", Color.FromRgb(0xE2, 0xDD, 0xD2));
                SetBrush(app, "TabSelectedBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
                SetBrush(app, "TabUnselectedBrush", Colors.Transparent);
            }
            else
            {
                // Dark Precision (Matte Charcoal & Zinc, high contrast & readability)
                SetBrush(app, "WindowBackgroundBrush", Color.FromRgb(0x12, 18, 20));
                SetBrush(app, "CardBackgroundBrush", Color.FromRgb(0x18, 0x18, 0x1C));
                SetBrush(app, "CardBorderBrush", Color.FromRgb(0x2E, 0x2E, 0x36));
                SetBrush(app, "CardHoverBrush", Color.FromRgb(0x22, 0x22, 0x28));
                SetBrush(app, "TextPrimaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF)); // Crisp Pure White
                SetBrush(app, "TextSecondaryBrush", Color.FromRgb(0xD4, 0xD4, 0xD8)); // Zinc-300: highly legible
                SetBrush(app, "TextMutedBrush", Color.FromRgb(0xA1, 0xA1, 0xAA)); // Zinc-400
                SetBrush(app, "AccentColorBrush", Color.FromRgb(0x10, 0xB9, 0x81)); // Vibrant Emerald Green
                SetBrush(app, "AccentHoverBrush", Color.FromRgb(0x05, 0x96, 0x69));
                SetBrush(app, "AccentTextBrush", Colors.White);
                SetBrush(app, "HeaderBackgroundBrush", Color.FromRgb(0x15, 0x15, 0x19));
                SetBrush(app, "TrackBrush", Color.FromRgb(0x2A, 0x2A, 0x33));
                SetBrush(app, "TabSelectedBrush", Color.FromRgb(0x2A, 0x2A, 0x33));
                SetBrush(app, "TabUnselectedBrush", Colors.Transparent);
            }
        }

        private static void SetBrush(Application app, string resourceKey, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[resourceKey] = brush;
        }
    }
}
