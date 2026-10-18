using System.Runtime.InteropServices;

namespace TimeTracker;

/// <summary>
/// A thin borderless strip across one screen: always on top, never focused, hidden from
/// the taskbar and Alt+Tab. Click-through, except (if clickable) while the cursor is over it
/// outside the corners: a left click then calls onClick.
/// WS_EX_TOOLWINDOW also makes it visible on every virtual desktop
/// (undocumented behavior, confirmed in step 0).
/// </summary>
sealed class StatusLineForm : Form
{
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_ALPHA = 0x2;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    const int WM_GETMINMAXINFO = 0x0024;
    const int GWL_EXSTYLE = -20;
    /// <summary>Logical pixels at each end of the line that stay click-through (e.g. the close button of a maximized window).</summary>
    const int CornerZone = 40;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO
    {
        public Point ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 50 };
    readonly Rectangle _bounds;
    readonly Action<Point>? _onClick;
    bool _clickable;

    /// <param name="onClick">Called with the screen point of a left click; null keeps the line click-through everywhere.</param>
    public StatusLineForm(Rectangle bounds, Color color, Action<Point>? onClick)
    {
        _bounds = bounds;
        _onClick = onClick;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = color;
        Bounds = bounds;

        // Other topmost windows (or a fullscreen app) can end up above us: re-assert regularly.
        _topmostTimer.Tick += (_, _) => BringToTop();
        _topmostTimer.Start();

        if (onClick != null)
        {
            // HTTRANSPARENT would only pass clicks on to windows of this thread: instead, drop
            // WS_EX_TRANSPARENT only while the cursor is over the clickable part.
            Cursor = Cursors.Hand;
            _hoverTimer.Tick += (_, _) => SetClickable(IsClickable(_bounds, Cursor.Position, LogicalToDeviceUnits(CornerZone)));
            _hoverTimer.Start();
        }
    }

    /// <summary>True if the point is on the line and not within cornerZone pixels of either end.</summary>
    internal static bool IsClickable(Rectangle bounds, Point cursor, int cornerZone) =>
        bounds.Contains(cursor) && cursor.X >= bounds.Left + cornerZone && cursor.X < bounds.Right - cornerZone;

    void SetClickable(bool clickable)
    {
        if (clickable == _clickable || !IsHandleCreated) return;
        _clickable = clickable;
        int style = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, clickable ? style & ~WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) _onClick?.Invoke(PointToScreen(e.Location));
    }

    public void SetColor(Color color) => BackColor = color;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _clickable = false; // created with WS_EX_TRANSPARENT
        // A layered window stays invisible until its attributes are set.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // WinForms and Windows may enlarge the window on creation: apply the exact size again.
        SetWindowPos(Handle, HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Windows enforces a minimum window height by default; allow a few pixels.
        if (m.Msg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            info.ptMinTrackSize = new Point(1, 1);
            Marshal.StructureToPtr(info, m.LParam, false);
        }
    }

    void BringToTop() =>
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _topmostTimer.Dispose();
            _hoverTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
