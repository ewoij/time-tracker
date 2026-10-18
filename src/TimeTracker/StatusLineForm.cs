using System.Runtime.InteropServices;

namespace TimeTracker;

/// <summary>
/// A thin borderless strip across one screen: always on top, click-through,
/// never focused, hidden from the taskbar and Alt+Tab.
/// WS_EX_TOOLWINDOW should also make it visible on every virtual desktop
/// (undocumented behavior, to be confirmed in step 0).
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

    readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = 1000 };
    readonly Rectangle _bounds;

    public StatusLineForm(Rectangle bounds, Color color)
    {
        _bounds = bounds;
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
    }

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
        if (disposing) _topmostTimer.Dispose();
        base.Dispose(disposing);
    }
}
