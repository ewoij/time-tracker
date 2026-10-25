using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TimeTracker;

/// <summary>Colors and building blocks shared by the task window and the questions.</summary>
static class Ui
{
    public static readonly Color Ink = ColorTranslator.FromHtml("#1C1C1E");
    public static readonly Color Muted = ColorTranslator.FromHtml("#4A5562");
    public static readonly Color Border = ColorTranslator.FromHtml("#C9CFD6");
    public static readonly Color Divider = ColorTranslator.FromHtml("#E8EBEF");
    public static readonly Color RowCurrent = ColorTranslator.FromHtml("#EEF1F4");
    public static readonly Color RowHover = ColorTranslator.FromHtml("#F4F6F8");
    public static readonly Color ChipBack = ColorTranslator.FromHtml("#E8ECF0");
    public static readonly Color ChipText = ColorTranslator.FromHtml("#3D4752");
    public static readonly Color ErrorText = ColorTranslator.FromHtml("#B3261E");

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Rounded corners on Windows 11 (ignored on Windows 10).</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    /// <summary>Flat button: dark primary, or white with a border.</summary>
    public static Button MakeButton(string text, bool primary, Font font, int height)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, Font = font, Height = height,
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false, UseMnemonic = false, Margin = Padding.Empty,
            BackColor = primary ? Ink : Color.White, ForeColor = primary ? Color.White : Ink,
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = primary ? Blend(Ink, Color.White, 0.15) : RowHover;
        b.FlatAppearance.MouseDownBackColor = primary ? Color.Black : RowCurrent;
        return b;
    }

    public static Label Wrapped(string text, Font font, Color color, int width) => new()
    {
        Text = text, Font = font, ForeColor = color, AutoSize = true, MaximumSize = new Size(width, 0), UseMnemonic = false,
    };

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>
/// Stacks controls vertically at full inner width, with fixed gaps.
/// Follows their height when it changes (e.g. the tag editor growing).
/// </summary>
sealed class Stack
{
    readonly int _pad;
    readonly List<(Control Control, int GapAfter)> _items = [];

    public Stack(int width, int pad)
    {
        _pad = pad;
        Panel = new Panel { Width = width, Height = 2 * pad, Margin = Padding.Empty };
    }

    public Panel Panel { get; }
    public int Inner => Panel.Width - 2 * _pad;
    public Color BackColor { set => Panel.BackColor = value; }

    public T Add<T>(T c, int gapAfter) where T : Control
    {
        if (c is not Label { AutoSize: true }) c.Width = Inner;
        Panel.Controls.Add(c);
        _items.Add((c, gapAfter));
        c.SizeChanged += (_, _) => Arrange();
        Arrange();
        return c;
    }

    void Arrange()
    {
        int y = _pad;
        foreach (var (c, gapAfter) in _items)
        {
            c.Location = new Point(_pad, y);
            y += c.Height + gapAfter;
        }
        Panel.Height = y - _items[^1].GapAfter + _pad;
    }
}
