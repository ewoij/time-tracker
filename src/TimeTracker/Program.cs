using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TimeTracker;

/// <summary>
/// Step 0 prototype: only the status line (one per screen), plus a minimal
/// tray icon to change the test color, move the line and exit.
/// Options: --bottom, --thickness N
/// </summary>
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new PrototypeContext(args));
    }
}

sealed class PrototypeContext : ApplicationContext
{
    static readonly (string Name, Color Color)[] TestColors =
    [
        ("Blue", ColorTranslator.FromHtml("#2F6FD6")),
        ("Teal", ColorTranslator.FromHtml("#127A6E")),
        ("Purple", ColorTranslator.FromHtml("#6B4FBF")),
        ("Gray (paused)", ColorTranslator.FromHtml("#8A919A")),
    ];

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    readonly List<StatusLineForm> _lines = [];
    readonly NotifyIcon _tray = new();
    bool _bottom;
    int _thickness = 3;
    Color _color = TestColors[0].Color;

    public PrototypeContext(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bottom") _bottom = true;
            else if (args[i] == "--thickness" && i + 1 < args.Length && int.TryParse(args[++i], out var t) && t > 0) _thickness = t;
        }

        var menu = new ContextMenuStrip();
        foreach (var (name, color) in TestColors)
            menu.Items.Add(name, null, (_, _) => SetColor(color));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Toggle top / bottom", null, (_, _) => { _bottom = !_bottom; RebuildLines(); });
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.Text = "Time Tracker (step 0 prototype)";
        _tray.Visible = true;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SetColor(_color);
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => RebuildLines();

    void SetColor(Color color)
    {
        _color = color;
        UpdateTrayIcon();
        RebuildLines();
    }

    void RebuildLines()
    {
        foreach (var line in _lines) line.Dispose();
        _lines.Clear();

        foreach (var screen in Screen.AllScreens)
        {
            var b = screen.Bounds;
            int y = _bottom ? b.Bottom - _thickness : b.Top;
            var line = new StatusLineForm(new Rectangle(b.Left, y, b.Width, _thickness), _color);
            line.Show();
            _lines.Add(line);
        }
    }

    void UpdateTrayIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(_color))
            g.FillRectangle(brush, 2, 2, 12, 12);

        var hIcon = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        old?.Dispose();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var line in _lines) line.Dispose();
        base.ExitThreadCore();
    }
}
