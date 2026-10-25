using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MouseControl.Models;
using MouseControl.Native;

namespace MouseControl.Services
{
    /// <summary>
    /// Manages the system tray notify icon, custom battery status rendering, and context menu.
    /// Strict handle lifetime management preventing GDI icon and font exhaustion.
    /// </summary>
    public class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly AppSettings _settings;
        private readonly Action _onOpenRequested;
        private readonly Action _onExitRequested;
        private readonly Action _onToggleClicker;

        private ContextMenuStrip? _contextMenu;
        private ToolStripMenuItem _statusItem = null!;
        private ToolStripMenuItem _clickerToggleItem = null!;
        private Font? _boldFont;
        private bool _disposed = false;

        public NotifyIcon NotifyIcon => _notifyIcon;

        public TrayIconManager(
            AppSettings settings,
            Action onOpenRequested,
            Action onExitRequested,
            Action onToggleClicker)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _onOpenRequested = onOpenRequested ?? throw new ArgumentNullException(nameof(onOpenRequested));
            _onExitRequested = onExitRequested ?? throw new ArgumentNullException(nameof(onExitRequested));
            _onToggleClicker = onToggleClicker ?? throw new ArgumentNullException(nameof(onToggleClicker));

            _notifyIcon = new NotifyIcon();
            BuildContextMenu();

            _notifyIcon.DoubleClick += (s, e) => _onOpenRequested();
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _onOpenRequested();
                }
            };

            UpdateIcon(-1, false, false, false);
            _notifyIcon.Visible = true;
        }

        private void BuildContextMenu()
        {
            _contextMenu = new ContextMenuStrip
            {
                Renderer = new ModernDarkMenuRenderer(),
                BackColor = Color.FromArgb(24, 24, 28),
                ForeColor = Color.FromArgb(244, 244, 245),
                ShowImageMargin = false,
                ShowCheckMargin = false,
                Padding = new Padding(4, 5, 4, 5),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
            };

            _boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var openItem = new ToolStripMenuItem("Открыть Mouse Control")
            {
                Font = _boldFont,
                Padding = new Padding(10, 5, 10, 5)
            };
            openItem.Click += (s, e) => _onOpenRequested();
            _contextMenu.Items.Add(openItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            _statusItem = new ToolStripMenuItem("Батарея: Опрос мышки...")
            {
                Enabled = false,
                Padding = new Padding(10, 5, 10, 5)
            };
            _contextMenu.Items.Add(_statusItem);

            _clickerToggleItem = new ToolStripMenuItem(_settings.ClickerEnabled ? "✓  Автокликер: ВКЛ" : "○  Автокликер: ВЫКЛ")
            {
                Padding = new Padding(10, 5, 10, 5)
            };
            _clickerToggleItem.Click += (s, e) =>
            {
                _onToggleClicker();
                UpdateClickerMenuState(_settings.ClickerEnabled);
            };
            _contextMenu.Items.Add(_clickerToggleItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Выход")
            {
                Padding = new Padding(10, 5, 10, 5)
            };
            exitItem.Click += (s, e) => _onExitRequested();
            _contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = _contextMenu;
        }

        public void UpdateClickerMenuState(bool enabled)
        {
            if (_disposed) return;
            try
            {
                _clickerToggleItem.Text = enabled ? "✓  Автокликер: ВКЛ" : "○  Автокликер: ВЫКЛ";
            }
            catch { }
        }

        public void UpdateIcon(int percentage, bool isCharging, bool isConnected, bool isWired)
        {
            if (_disposed) return;

            try
            {
                string tooltip;
                if (!isConnected || percentage < 0)
                {
                    tooltip = "Mouse Control: Ожидание мышки...";
                    _statusItem.Text = "Статус: Мышь отключена / спит";
                }
                else if (isWired || isCharging)
                {
                    tooltip = $"Mouse Control: {percentage}% (Кабель USB / Зарядка)";
                    _statusItem.Text = $"Батарея: {percentage}% (Кабель USB / Зарядка)";
                }
                else
                {
                    int hours = (int)Math.Round((percentage / 100.0) * 45.0);
                    string hoursShort = hours > 0 ? $" (~{hours} ч.)" : "";
                    string hoursFull = hours > 0 ? $" (~{hours} ч. работы)" : "";
                    tooltip = $"Mouse Control: {percentage}% (2.4G){hoursShort}";
                    _statusItem.Text = $"Батарея: {percentage}%{hoursFull}";
                }

                _notifyIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;

                using var bmp = GenerateMouseTrayBitmap(percentage, isCharging || isWired, isConnected);
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using var tempIcon = Icon.FromHandle(hIcon);
                    var clonedIcon = (Icon)tempIcon.Clone();

                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = clonedIcon;
                    oldIcon?.Dispose();
                }
                finally
                {
                    if (hIcon != IntPtr.Zero)
                    {
                        WinApi.DestroyIcon(hIcon);
                    }
                }
            }
            catch
            {
                // Defensively guard against GDI / Shell failures
            }
        }

        private Bitmap GenerateMouseTrayBitmap(int percentage, bool isCharging, bool isConnected)
        {
            var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // Minimalist White-Line Mouse (Feather / Lucide aesthetic: pure white line drawing)
            // Outer Capsule dimensions (centered at 32x32)
            float mouseX = 8.0f;
            float mouseY = 3.5f;
            float mouseW = 16.0f;
            float mouseH = 25.0f;
            float radius = mouseW / 2.0f;

            Color bodyColor;
            Color wheelColor;

            if (!isConnected || percentage < 0)
            {
                // Disconnected / Sleeping: Dimmed subtle white-gray lines
                bodyColor = Color.FromArgb(110, 210, 210, 210);
                wheelColor = Color.FromArgb(110, 210, 210, 210);
            }
            else
            {
                bodyColor = Color.White;

                if (isCharging)
                {
                    wheelColor = Color.FromArgb(16, 185, 129);
                }
                else if (percentage <= 10)
                {
                    wheelColor = Color.FromArgb(239, 68, 68);
                }
                else if (percentage <= 20)
                {
                    wheelColor = Color.FromArgb(245, 158, 11);
                }
                else
                {
                    wheelColor = Color.White;
                }
            }

            using (var bodyPen = new Pen(bodyColor, 2.0f))
            {
                bodyPen.StartCap = LineCap.Round;
                bodyPen.EndCap = LineCap.Round;
                bodyPen.LineJoin = LineJoin.Round;

                using var bodyPath = CreateRoundedRect(mouseX, mouseY, mouseW, mouseH, radius);
                g.DrawPath(bodyPen, bodyPath);
            }

            using (var wheelPen = new Pen(wheelColor, 2.0f))
            {
                wheelPen.StartCap = LineCap.Round;
                wheelPen.EndCap = LineCap.Round;
                g.DrawLine(wheelPen, 16.0f, 7.5f, 16.0f, 13.0f);
            }

            if (!isConnected || percentage < 0)
            {
                using var slashPen = new Pen(Color.FromArgb(140, 220, 220, 220), 1.5f);
                slashPen.StartCap = LineCap.Round;
                slashPen.EndCap = LineCap.Round;
                g.DrawLine(slashPen, 6.0f, 27.0f, 26.0f, 5.0f);
            }

            return bmp;
        }

        private static GraphicsPath CreateRoundedRect(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _notifyIcon.Visible = false;

                var oldIcon = _notifyIcon.Icon;
                _notifyIcon.Icon = null;
                oldIcon?.Dispose();

                _notifyIcon.ContextMenuStrip = null;
                _contextMenu?.Dispose();
                _contextMenu = null;

                _boldFont?.Dispose();
                _boldFont = null;

                _notifyIcon.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// Modern dark-themed renderer for the system tray context menu, matching Fluent Design.
    /// Eliminates the classic retro WinForms white gutters and harsh selection boxes.
    /// </summary>
    public class ModernDarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public ModernDarkMenuRenderer() : base(new ModernDarkColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
            {
                using var bgBrush = new SolidBrush(Color.FromArgb(24, 24, 28));
                e.Graphics.FillRectangle(bgBrush, e.Item.ContentRectangle);
                return;
            }

            var rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            using var fillBrush = new SolidBrush(Color.FromArgb(42, 42, 48));
            using var path = CreateRoundedPath(rect, 4);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fillBrush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(244, 244, 245) : Color.FromArgb(161, 161, 170);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Color.FromArgb(46, 46, 54));
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(46, 46, 54), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class ModernDarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(24, 24, 28);
        public override Color MenuBorder => Color.FromArgb(46, 46, 54);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.FromArgb(42, 42, 48);
        public override Color ImageMarginGradientBegin => Color.FromArgb(24, 24, 28);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(24, 24, 28);
        public override Color ImageMarginGradientEnd => Color.FromArgb(24, 24, 28);
        public override Color SeparatorDark => Color.FromArgb(46, 46, 54);
        public override Color SeparatorLight => Color.Transparent;
    }
}
