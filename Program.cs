using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace UsageTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(false, "UsageTray", out bool firstInstance);
        if (!firstInstance)
            return;

        ApplicationConfiguration.Initialize();
        using var tray = new TrayApp();
        Application.Run(tray);
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "UsageTray";
    private const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly UsageWidget _widget;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _widgetTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _clickTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private UsageReading _claude = new();
    private UsageReading _codex = new();
    private DateTimeOffset? _updatedAt;
    private DateTimeOffset? _claudeAt;
    private DateTimeOffset? _codexAt;
    private DateTimeOffset _claudeDue = DateTimeOffset.MinValue;
    private DateTimeOffset _claudeBlockedUntil = DateTimeOffset.MinValue;
    private TimeSpan _claudeWait = TimeSpan.FromMinutes(5);
    private UsagePanel? _panel;
    private bool _refreshing;
    private bool _closing;

    public TrayApp()
    {
        var startup = new ToolStripMenuItem("Arrancar con Windows");
        _menu.Items.Add("Actualizar ahora", null, async (_, _) => await RefreshAllAsync());
        _menu.Items.Add(startup);
        _menu.Items.Add("Salir", null, (_, _) => ExitThread());
        _menu.Opening += (_, _) =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            startup.Checked = key?.GetValue(RunValue) is not null;
        };
        startup.Click += (_, _) =>
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (startup.Checked)
                    key.DeleteValue(RunValue, throwOnMissingValue: false);
                else
                    key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
                startup.Checked = !startup.Checked;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                MessageBox.Show("No se pudo cambiar el inicio con Windows.", RunValue);
            }
        };

        _widget = new UsageWidget(this);
        _widget.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                _clickTimer.Start();
            else if (args.Button == MouseButtons.Right)
                _menu.Show(Cursor.Position);
        };
        _widget.MouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Left && args.Clicks == 2)
                _clickTimer.Stop();
        };
        _widget.MouseDoubleClick += async (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                _clickTimer.Stop();
                await RefreshAllAsync();
            }
        };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            TogglePanel();
        };
        _widgetTimer.Tick += (_, _) => _widget.UpdatePosition();
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync(manual: false);
        Application.Idle += Start;
    }

    private async void Start(object? sender, EventArgs args)
    {
        Application.Idle -= Start;
        _widget.UpdatePosition();
        _widgetTimer.Start();
        _refreshTimer.Start();
        await RefreshAllAsync();
    }

    // El sondeo de Claude parte de 5 min y dobla la espera hasta 15 min si falla.
    // El bloqueo indicado por el servidor también se aplica al actualizar a mano.
    private async Task RefreshAllAsync(bool manual = true)
    {
        if (_refreshing || _closing)
            return;

        _refreshing = true;
        try
        {
            UpdateDisplay();
            var now = DateTimeOffset.Now;
            bool readClaude = now >= _claudeBlockedUntil && (manual || now >= _claudeDue);
            var claudeTask = readClaude ? UsageSources.ReadClaudeAsync() : Task.FromResult(_claude);
            var readings = await Task.WhenAll(claudeTask, Task.Run(UsageSources.ReadCodex));
            if (_closing)
                return;

            if (readClaude)
            {
                ApplyReading(readings[0], ref _claude, ref _claudeAt);
                if (readings[0].RetryAt is { } retryAt)
                {
                    _claudeBlockedUntil = _claudeDue = retryAt;
                }
                else
                {
                    _claudeWait = readings[0].Error is null
                        ? TimeSpan.FromMinutes(5)
                        : TimeSpan.FromMinutes(Math.Min(_claudeWait.TotalMinutes * 2, 15));
                    _claudeDue = DateTimeOffset.Now + _claudeWait;
                }
            }
            ApplyReading(readings[1], ref _codex, ref _codexAt);
        }
        finally
        {
            _refreshing = false;
            if (!_closing)
                UpdateDisplay();
        }
    }

    private void ApplyReading(UsageReading reading, ref UsageReading current, ref DateTimeOffset? lastGood)
    {
        if (reading.Error is { } reason)
            current = current with { Error = reason };
        else
        {
            current = reading;
            lastGood = _updatedAt = DateTimeOffset.Now;
        }
    }

    private static string? ReadingError(UsageReading reading, DateTimeOffset? lastGood) =>
        reading.Error is { } reason && lastGood is { } updated
            ? $"sin actualizar desde {updated:HH:mm}: {reason}"
            : reading.Error;

    private void UpdateDisplay()
    {
        _widget.UpdateReading();
        _panel?.UpdateReading();
    }

    private void TogglePanel()
    {
        if (_panel is not null)
        {
            _panel.Close();
            return;
        }

        _panel = new UsagePanel(this);
        _panel.FormClosed += (_, _) => _panel = null;
        _panel.Show();
        _panel?.Activate();
    }

    private static string PercentText(UsageWindow? window, string suffix = "") =>
        window is null ? "--" : $"{Math.Round(window.Percent, MidpointRounding.AwayFromZero):0}{suffix}";

    private static Color UsageColor(UsageWindow? window) => window?.Percent switch
    {
        >= 90 => Color.FromArgb(0xF0, 0x5A, 0x5A),
        >= 75 => Color.FromArgb(0xEF, 0xB0, 0x27),
        _ => Color.FromArgb(0x5D, 0xCA, 0x5A),
    };

    private static bool IsDarkTheme()
    {
        using var theme = Registry.CurrentUser.OpenSubKey(ThemeKey);
        return theme?.GetValue("AppsUseLightTheme") is 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closing)
        {
            _closing = true;
            Application.Idle -= Start;
            _refreshTimer.Dispose();
            _clickTimer.Dispose();
            _widgetTimer.Dispose();
            _panel?.Close();
            _widget.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class UsageWidget : Form
    {
        private const int ToolWindow = 0x80;
        private const int NoActivate = 0x08000000;
        private const int Layered = 0x80000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint EventSystemForeground = 0x0003;
        private const uint EventObjectReorder = 0x8004;
        private const uint WinEventOutOfContext = 0;
        private const uint WinEventSkipOwnProcess = 2;
        private const uint UlwAlpha = 2;
        private const byte AcSrcAlpha = 1;
        private const uint DwmwaCloaked = 14;
        private static readonly nint HwndTopmost = -1;

        private readonly TrayApp _tray;
        private readonly ToolTip _tip = new();
        // Los logos no van en el repo (son marcas): se leen de las apps de Claude y Codex instaladas.
        private readonly Icon? _claudeDark = LoadIcon("Claude_", "Tray-Win32-Dark.ico");
        private readonly Icon? _codexDark = LoadIcon("OpenAI.Codex_", "chatgpt-tray-dark.ico");
        private readonly Icon? _claudeLight = LoadIcon("Claude_", "Tray-Win32.ico");
        private readonly Icon? _codexLight = LoadIcon("OpenAI.Codex_", "chatgpt-tray-light.ico");
        private readonly WinEventProc _winEvent;
        private readonly nint _foregroundHook;
        private readonly nint _reorderHook;
        private Font? _numberFont;
        private Rectangle _layerBounds;
        private int _layoutDpi;
        private int _columnWidth;
        private bool _dark;
        private bool _hover;
        private bool _taskbarWasHidden;
        private bool _wasFullscreen;

        public UsageWidget(TrayApp tray)
        {
            _tray = tray;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            _tip.ShowAlways = true;
            UpdateReading();
            _winEvent = OnWinEvent;
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _winEvent,
                0, 0, WinEventOutOfContext);
            _reorderHook = SetWinEventHook(EventObjectReorder, EventObjectReorder, 0, _winEvent,
                0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= ToolWindow | NoActivate | Layered;
                return parameters;
            }
        }

        public int ScalePixel(int value) => (int)Math.Round(value * DeviceDpi / 96d);

        public void UpdateReading()
        {
            _tip.SetToolTip(this, Tip("Claude", _tray._claude, _tray._claudeAt) + "\n"
                + Tip("Codex", _tray._codex, _tray._codexAt));
            Render();

            static string Tip(string name, UsageReading reading, DateTimeOffset? lastGood) =>
                ReadingError(reading, lastGood) is { } reason
                ? $"{name} · {reason}"
                : $"{name} · 5 h {PercentText(reading.Session, " %")} · semana {PercentText(reading.Week, " %")}";
        }

        private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId,
            uint threadId, uint eventTime)
        {
            if (!_tray._closing && childId == 0 && (eventType == EventObjectReorder || objectId == 0))
                UpdatePosition();
        }

        public void UpdatePosition()
        {
            nint handle = Handle;
            var screen = Screen.PrimaryScreen ?? Screen.FromPoint(Point.Empty);
            nint tray = FindWindow("Shell_TrayWnd", null);
            var taskbar = new WindowRect();
            bool hasTaskbar = tray != 0 && GetWindowRect(tray, out taskbar);
            bool taskbarHidden = hasTaskbar && taskbar.Top >= screen.Bounds.Bottom - 2;
            bool fullscreen = IsFullscreen(screen.Bounds);
            bool hide = (taskbarHidden && _taskbarWasHidden) || (fullscreen && _wasFullscreen);
            _taskbarWasHidden = taskbarHidden;
            _wasFullscreen = fullscreen;
            if (hide)
            {
                _hover = false;
                Hide();
                return;
            }

            // Conserva la última posición mientras la barra autooculta confirma su estado.
            if (taskbarHidden)
            {
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                return;
            }

            bool dark = IsDarkTheme();
            bool dpiChanged = _layoutDpi != DeviceDpi;
            if (dpiChanged)
            {
                _layoutDpi = DeviceDpi;
                _numberFont?.Dispose();
                _numberFont = new Font("Segoe UI", 15f * DeviceDpi / 96f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var measurement = new Bitmap(1, 1);
                using var graphics = Graphics.FromImage(measurement);
                using var format = StringFormat.GenericTypographic;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                _columnWidth = (int)Math.Ceiling(graphics.MeasureString("100", _numberFont, PointF.Empty,
                    format).Width);
            }

            int width = 3 * ScalePixel(8) + ScalePixel(16) + 2 * _columnWidth + ScalePixel(6);
            int height = Math.Max(1, (hasTaskbar ? taskbar.Bottom - taskbar.Top : ScalePixel(48)) - ScalePixel(6));
            nint notify = hasTaskbar ? FindWindowEx(tray, 0, "TrayNotifyWnd", null) : 0;
            var location = notify != 0 && GetWindowRect(notify, out var notification)
                ? new Point(notification.Left - width - ScalePixel(4), taskbar.Top + (taskbar.Bottom - taskbar.Top - height) / 2)
                : new Point(screen.Bounds.Right - width, screen.WorkingArea.Bottom - height);
            var bounds = new Rectangle(location, new Size(width, height));
            if (bounds != _layerBounds || bounds != Bounds || dpiChanged || dark != _dark || !Visible)
            {
                _dark = dark;
                _layerBounds = bounds;
                Render();
            }

            if (!Visible && !_layerBounds.IsEmpty)
                Show();
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Render();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Render();
        }

        private void Render()
        {
            if (_layerBounds.IsEmpty || _numberFont is null)
                return;

            var location = _layerBounds.Location;
            var size = _layerBounds.Size;
            nint dc = CreateCompatibleDC(0);
            if (dc == 0)
            {
                _layerBounds = Rectangle.Empty;
                return;
            }

            nint dib = 0;
            nint previous = 0;
            try
            {
                var header = new BitmapInfoHeader { Size = 40, Width = size.Width, Height = -size.Height,
                    Planes = 1, BitCount = 32 };
                dib = CreateDIBSection(dc, in header, 0, out nint pixels, 0, 0);
                if (dib == 0 || (previous = SelectObject(dc, dib)) == 0)
                {
                    _layerBounds = Rectangle.Empty;
                    return;
                }

                // GDI+ pinta directamente en el DIB con el alfa premultiplicado que requiere Win32.
                using (var bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var alignment = StringFormat.GenericTypographic)
                {
                    alignment.Alignment = StringAlignment.Far;
                    alignment.LineAlignment = StringAlignment.Center;
                    graphics.Clear(Color.FromArgb(1, Color.Black));
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    if (_hover)
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        int diameter = Math.Max(1, Math.Min(ScalePixel(12), Math.Min(size.Width, size.Height)));
                        using var outline = new GraphicsPath();
                        outline.AddArc(0, 0, diameter, diameter, 180, 90);
                        outline.AddArc(size.Width - diameter, 0, diameter, diameter, 270, 90);
                        outline.AddArc(size.Width - diameter, size.Height - diameter, diameter, diameter, 0, 90);
                        outline.AddArc(0, size.Height - diameter, diameter, diameter, 90, 90);
                        outline.CloseFigure();
                        using var fill = new SolidBrush(_dark ? Color.FromArgb(20, Color.White) : Color.FromArgb(15, Color.Black));
                        graphics.FillPath(fill, outline);
                    }

                    int half = size.Height / 2;
                    DrawRow(_dark ? _claudeDark : _claudeLight, "Cl", _tray._claude, _tray._claudeAt, 0, half);
                    DrawRow(_dark ? _codexDark : _codexLight, "Cx", _tray._codex, _tray._codexAt, half, size.Height - half);

                    void DrawRow(Icon? logo, string fallback, UsageReading reading, DateTimeOffset? lastGood, int top, int height)
                    {
                        int margin = ScalePixel(8);
                        int logoSize = ScalePixel(16);
                        if (logo is not null)
                        {
                            using var scaledLogo = new Icon(logo, logoSize, logoSize);
                            using var logoBitmap = scaledLogo.ToBitmap();
                            graphics.DrawImage(logoBitmap, margin, top + (height - logoSize) / 2, logoSize, logoSize);
                        }
                        else
                        {
                            using var labelFont = new Font("Segoe UI", ScalePixel(11), FontStyle.Bold, GraphicsUnit.Pixel);
                            using var labelBrush = new SolidBrush(_dark ? Color.WhiteSmoke : Color.FromArgb(28, 28, 28));
                            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                            graphics.DrawString(fallback, labelFont, labelBrush,
                                new RectangleF(margin - ScalePixel(2), top, logoSize + ScalePixel(4), height), center);
                        }

                        var column = new RectangleF(2 * margin + logoSize, top, _columnWidth, height);
                        if (reading.Error is not null && lastGood is null)
                        {
                            DrawNumber("!", Color.Gray);
                            return;
                        }

                        DrawNumber(PercentText(reading.Session), UsageColor(reading.Session));
                        column.X += _columnWidth + ScalePixel(6);
                        DrawNumber(PercentText(reading.Week), UsageColor(reading.Week));

                        void DrawNumber(string text, Color color)
                        {
                            var shadow = column;
                            shadow.Offset(0, ScalePixel(1));
                            using var shade = new SolidBrush(Color.FromArgb(_tray._refreshing ? 77 : 153,
                                _dark ? Color.Black : Color.White));
                            using var number = new SolidBrush(Color.FromArgb(_tray._refreshing ? 128 : 255, color));
                            graphics.DrawString(text, _numberFont, shade, shadow, alignment);
                            graphics.DrawString(text, _numberFont, number, column, alignment);
                        }
                    }
                }

                var source = Point.Empty;
                var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
                if (!UpdateLayeredWindow(Handle, 0, in location, in size, dc, in source, 0, in blend, UlwAlpha))
                    _layerBounds = Rectangle.Empty;
            }
            finally
            {
                if (previous != 0)
                    SelectObject(dc, previous);
                if (dib != 0)
                    DeleteObject(dib);
                DeleteDC(dc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_foregroundHook != 0)
                    UnhookWinEvent(_foregroundHook);
                if (_reorderHook != 0)
                    UnhookWinEvent(_reorderHook);
                _tip.Dispose();
                _numberFont?.Dispose();
                _claudeDark?.Dispose();
                _codexDark?.Dispose();
                _claudeLight?.Dispose();
                _codexLight?.Dispose();
            }
            base.Dispose(disposing);
        }

        // Busca el .ico en la carpeta del paquete instalado. WindowsApps no se puede listar, así que
        // la carpeta sale del registro de paquetes del usuario. Si no está, se dibuja una etiqueta.
        private static Icon? LoadIcon(string package, string file)
        {
            const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            try
            {
                using var packages = Registry.CurrentUser.OpenSubKey(Packages);
                var path = (packages?.GetSubKeyNames() ?? [])
                    .Where(name => name.StartsWith(package, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name =>
                    {
                        using var key = packages!.OpenSubKey(name);
                        return key?.GetValue("PackageRootFolder") is string root
                            ? Path.Combine(root, "app", "resources", file) : null;
                    })
                    .FirstOrDefault(File.Exists);
                return path is null ? null : new Icon(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or System.Security.SecurityException)
            {
                return null;
            }
        }

        private static bool IsFullscreen(Rectangle screen)
        {
            nint foreground = GetForegroundWindow();
            if (foreground == 0 || !IsWindowVisible(foreground) || IsZoomed(foreground)
                || !GetWindowRect(foreground, out var bounds)
                || bounds.Left != screen.Left || bounds.Top != screen.Top
                || bounds.Right != screen.Right || bounds.Bottom != screen.Bottom)
                return false;

            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == Environment.ProcessId
                || (DwmGetWindowAttribute(foreground, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                return false;

            var name = new StringBuilder(256);
            GetClassName(foreground, name, name.Capacity);
            return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow"
                or "XamlExplorerHostIslandWindow");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlendFunction
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId,
            uint threadId, uint eventTime);

        [DllImport("user32.dll")]
        private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback,
            uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(nint hook);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, in Point destination,
            in Size size, nint sourceDc, in Point source, uint colorKey, in BlendFunction blend, uint flags);

        [DllImport("gdi32.dll")]
        private static extern nint CreateCompatibleDC(nint dc);

        [DllImport("gdi32.dll")]
        private static extern nint CreateDIBSection(nint dc, in BitmapInfoHeader header, uint usage,
            out nint bits, nint section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern nint SelectObject(nint dc, nint gdiObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(nint gdiObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(nint dc);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, uint size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
        private static extern nint FindWindow(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
        private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

        // Win32 writes a RECT (left, top, right, bottom) into the four integer fields.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint window, out WindowRect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
        private static extern int GetClassName(nint window, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    }

    private sealed class UsagePanel : Form
    {
        private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
        private readonly TrayApp _tray;
        private readonly LinkLabel _refreshLink = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        private Font? _refreshFont;

        public UsagePanel(TrayApp tray)
        {
            _tray = tray;
            Text = RunValue;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            _refreshLink.LinkClicked += async (_, _) => await _tray.RefreshAllAsync();
            Controls.Add(_refreshLink);
            UpdateReading();
            Place();
            Deactivate += (_, _) => Close();
        }

        private int ScalePixel(int value) => (int)Math.Round(value * DeviceDpi / 96d);

        private void Place()
        {
            var widget = _tray._widget.Bounds;
            var screen = Screen.FromRectangle(widget).Bounds;
            ClientSize = new Size(Math.Min(ScalePixel(320), screen.Width), Math.Min(ScalePixel(252), screen.Height));
            int gap = _tray._widget.ScalePixel(8);
            Location = new Point(
                Math.Clamp(widget.Right - Width, screen.Left, screen.Right - Width),
                Math.Clamp(widget.Top - gap - Height, screen.Top, screen.Bottom - Height));
            _refreshLink.Bounds = new Rectangle(ScalePixel(14), ScalePixel(225), ScalePixel(130), ScalePixel(18));
            // No se libera la fuente anterior: WinForms la guarda en caché para medir el enlace
            // y liberarla provoca "Parameter is not valid". Solo cambia al cambiar de DPI.
            int fontSize = ScalePixel(12);
            if (_refreshFont is null || (int)_refreshFont.Size != fontSize)
            {
                _refreshFont = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                _refreshLink.Font = _refreshFont;
            }
        }

        public void UpdateReading()
        {
            _refreshLink.Enabled = !_tray._refreshing;
            _refreshLink.Text = _tray._refreshing ? "Actualizando…" : $"Actualizar · {_tray._updatedAt?.ToString("HH:mm") ?? "--:--"}";
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                _refreshFont?.Dispose();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Place();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Place();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            bool dark = IsDarkTheme();
            Color background = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);
            Color foreground = dark ? Color.WhiteSmoke : Color.FromArgb(28, 28, 28);
            Color muted = dark ? Color.Silver : Color.FromArgb(85, 85, 85);
            Color track = dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(225, 225, 225);
            _refreshLink.BackColor = background;
            _refreshLink.LinkColor = _refreshLink.ActiveLinkColor = _refreshLink.VisitedLinkColor = foreground;
            _refreshLink.DisabledLinkColor = muted;
            _refreshLink.LinkBehavior = _tray._refreshing ? LinkBehavior.NeverUnderline : LinkBehavior.AlwaysUnderline;
            e.Graphics.Clear(background);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, track, ButtonBorderStyle.Solid);

            using var title = new Font("Segoe UI", ScalePixel(15), FontStyle.Bold, GraphicsUnit.Pixel);
            using var body = new Font("Segoe UI", ScalePixel(13), FontStyle.Regular, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", ScalePixel(12), FontStyle.Regular, GraphicsUnit.Pixel);
            using var trackBrush = new SolidBrush(track);
            var now = DateTimeOffset.Now;

            Section("Claude", _tray._claude, _tray._claudeAt, 12);
            Section("Codex", _tray._codex, _tray._codexAt, 116);
            e.Graphics.FillRectangle(trackBrush, ScalePixel(14), ScalePixel(219), ScalePixel(292), ScalePixel(1));
            if (_tray._codex.Timestamp is { } timestamp)
            {
                var age = now - timestamp;
                string ago = age.TotalMinutes < 1 ? "menos de 1 min"
                    : age.TotalHours < 1 ? $"{(long)age.TotalMinutes} min"
                    : age.TotalDays < 1 ? $"{(long)age.TotalHours} h"
                    : (long)age.TotalDays == 1 ? "1 día"
                    : $"{(long)age.TotalDays} días";
                Draw($"dato de hace {ago}", small, muted, 144, 225, 162, 18, right: true);
            }

            void Section(string name, UsageReading reading, DateTimeOffset? lastGood, int y)
            {
                Draw(name, title, foreground, 14, y, 85, 20);
                if (ReadingError(reading, lastGood) is { } reason)
                {
                    var bounds = new Rectangle(ScalePixel(76), ScalePixel(y - 3), ScalePixel(230), ScalePixel(27));
                    TextRenderer.DrawText(e.Graphics, reason, small, bounds, muted,
                        TextFormatFlags.Right | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
                Row("Sesión 5 h", reading.Session, y + 24);
                Row("Semanal", reading.Week, y + 60);
            }

            void Row(string label, UsageWindow? window, int y)
            {
                Draw(label, body, foreground, 14, y, 80, 20);
                Draw(PercentText(window, " %"), body, foreground, 96, y, 50, 20, right: true);
                if (window?.ResetAt is { } reset)
                {
                    var local = reset.ToLocalTime();
                    string day = local.Date == now.Date ? "" : local.ToString("ddd", Spanish).TrimEnd('.') + " ";
                    Draw($"reinicio {day}{local:HH:mm}", small, muted, 152, y, 154, 20, right: true);
                }

                var bar = new Rectangle(ScalePixel(14), ScalePixel(y + 22), ScalePixel(292), ScalePixel(5));
                e.Graphics.FillRectangle(trackBrush, bar);
                if (window is { Percent: > 0 })
                {
                    using var fill = new SolidBrush(UsageColor(window));
                    bar.Width = (int)Math.Round(bar.Width * (Math.Min(window.Percent, 100) / 100));
                    e.Graphics.FillRectangle(fill, bar);
                }
            }

            void Draw(string text, Font font, Color color, int x, int y, int width, int height, bool right = false)
            {
                var bounds = new Rectangle(ScalePixel(x), ScalePixel(y), ScalePixel(width), ScalePixel(height));
                var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(e.Graphics, text, font, bounds, color, flags | (right ? TextFormatFlags.Right : 0));
            }
        }
    }
}
