using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TimeTracker;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Two instances would fight over the lock on hours.xlsx.
        using var mutex = new Mutex(true, @"Local\TimeTracker.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Time Tracker is already running (see the notification area).", "Time Tracker");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Time Tracker: unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Settings settings;
        try { settings = Settings.Load(); }
        catch (Exception e)
        {
            MessageBox.Show($"Could not read {Settings.FilePath}:\n\n{e.Message}", "Time Tracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrackerContext(settings));
    }
}

/// <summary>
/// The app itself: owns hours.xlsx, the status lines, the tray icon and the task window,
/// and keeps them in sync with the current state.
/// </summary>
sealed class TrackerContext : ApplicationContext
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    static readonly Color ExcelDot = ColorTranslator.FromHtml("#F5C542");

    readonly Settings _settings;
    readonly HoursFile _file;
    readonly List<StatusLineForm> _lines = [];
    readonly NotifyIcon _tray = new();
    readonly TaskWindow _window;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    bool _sawOpenElsewhere;
    string? _currentTaskText;

    public TrackerData Data { get; private set; } = TrackerData.Empty;
    /// <summary>The file is released for editing elsewhere: the app writes nothing.</summary>
    public bool EditMode { get; private set; }
    public string? LastError { get; private set; }
    public string EditStatus => _sawOpenElsewhere
        ? "Open in another program, waiting for it to close…"
        : "Waiting for the file to be opened…";

    public TrackerContext(Settings settings)
    {
        _settings = settings;
        _file = new HoursFile(settings.DataFilePath, settings.BackupCount);
        _window = new TaskWindow(this);

        try { settings.ApplyLaunchAtStartup(); }
        catch (Exception e) { LastError = "Could not update launch at startup: " + e.Message; }

        if (_file.TryLock())
        {
            ReloadOrRelease();
        }
        else
        {
            // Already open in Excel: show what can be read, and wait for it to be closed.
            EditMode = true;
            _sawOpenElsewhere = true;
            try { Data = _file.Load(); }
            catch (Exception e) { LastError = "Could not read the file: " + e.Message; }
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
        _tray.Visible = true;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        RebuildLines();
        Refresh();
    }

    public void StartTask(string id)
    {
        if (Data.IsRunning && Data.CurrentTaskId == id) return;
        Write(() => _file.AppendEvent(Now(), "Start", id));
    }

    public bool CanResume => !Data.IsRunning && Data.CurrentTaskId is { } id && Data.Find(id) != null;

    public void TogglePause()
    {
        if (Data.IsRunning) Write(() => _file.AppendEvent(Now(), "Pause"));
        else if (CanResume) StartTask(Data.CurrentTaskId!);
    }

    public bool CreateTask(string name, string tags, string color) =>
        Write(() => _file.CreateTaskAndStart(name, tags, color, Now()));

    public bool UpdateTask(TaskInfo task) => Write(() => _file.UpdateTask(task));

    /// <summary>Releases the lock and opens the file in its default program, until it is closed again.</summary>
    public void OpenInExcel()
    {
        if (EditMode) return;
        _file.Unlock();
        EditMode = true;
        _sawOpenElsewhere = false;
        LastError = null;
        try { Process.Start(new ProcessStartInfo(_file.FilePath) { UseShellExecute = true }); }
        catch (Exception e) { LastError = "Could not open the file: " + e.Message; }
        Refresh();
    }

    /// <summary>Takes the lock back and reloads everything (automatically, or with the "Done, reload" button).</summary>
    public void LeaveEditMode()
    {
        if (!EditMode) return;
        if (!_file.TryLock())
        {
            LastError = "The file is still open in another program. Close it first.";
            Refresh();
            return;
        }
        LastError = null;
        EditMode = !ReloadOrRelease();
        Refresh();
    }

    /// <summary>Reads the locked file; if it cannot be read, releases it so it can be fixed by hand.</summary>
    bool ReloadOrRelease()
    {
        try
        {
            Data = _file.Load();
            return true;
        }
        catch (Exception e)
        {
            _file.Unlock();
            EditMode = true;
            _sawOpenElsewhere = false;
            LastError = $"Could not read {Path.GetFileName(_file.FilePath)}: {e.Message} Fix it in Excel, then close it.";
            return false;
        }
    }

    bool Write(Func<TrackerData> write)
    {
        if (EditMode) return false;
        try
        {
            Data = write();
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = "Could not write to the file: " + e.Message;
            try { Data = _file.Load(); } catch { /* keep what we had */ }
            if (!_window.Visible) _tray.ShowBalloonTip(5000, "Time Tracker", LastError, ToolTipIcon.Error);
        }
        Refresh();
        return LastError == null;
    }

    static DateTime Now()
    {
        var now = DateTime.Now;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    void OnTick()
    {
        if (EditMode)
        {
            if (_file.IsOpenElsewhere()) _sawOpenElsewhere = true;
            else if (_sawOpenElsewhere) LeaveEditMode();
        }
        UpdateTooltip();
        _window.Tick();
    }

    void ToggleWindow()
    {
        if (_window.Visible) _window.Hide();
        // A click on the icon first deactivates the open window, which hides it: don't reopen it at once.
        else if (Environment.TickCount64 - _window.HiddenAt > 300) _window.ShowNear(Cursor.Position);
    }

    /// <summary>Brings the line, the icon, current_task.txt and the window in line with the state.</summary>
    void Refresh()
    {
        var color = Data.StateColor;
        foreach (var line in _lines) line.SetColor(color);
        UpdateTrayIcon(color);
        UpdateTooltip();
        WriteCurrentTaskFile();
        if (_window.Visible) _window.Rebuild();
    }

    void WriteCurrentTaskFile()
    {
        var text = Data.IsRunning ? Data.NameOf(Data.CurrentTaskId!).ReplaceLineEndings(" ") : "pause";
        if (text == _currentTaskText) return;
        try
        {
            var path = Path.GetFullPath(_settings.CurrentTaskFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write then rename, so a prompt reading it never sees a half-written file.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text + "\n", new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            _currentTaskText = text;
        }
        catch (Exception e)
        {
            LastError ??= "Could not write current_task.txt: " + e.Message;
        }
    }

    void UpdateTooltip()
    {
        var elapsed = Data.Since is { } since ? " · " + TrackerData.FormatDuration(DateTime.Now - since, withSeconds: false) : "";
        var text = (Data.IsRunning ? Data.NameOf(Data.CurrentTaskId!) : "Paused") + elapsed;
        if (EditMode) text += " (open in Excel)";
        if (text.Length > 127) text = text[..126] + "…";
        if (_tray.Text != text) _tray.Text = text;
    }

    void UpdateTrayIcon(Color color)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float inset = size / 8f;
            using (var brush = new SolidBrush(color))
            using (var path = TaskWindow.RoundedRect(new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset), size / 8f))
                g.FillPath(brush, path);
            if (EditMode)
            {
                // Small yellow dot: the file is open in Excel.
                float d = size * 0.45f;
                using var dot = new SolidBrush(ExcelDot);
                using var outline = new Pen(Color.FromArgb(0x1F, 0x26, 0x2E), Math.Max(1, size / 16f));
                g.FillEllipse(dot, size - d - 0.5f, 0.5f, d, d);
                g.DrawEllipse(outline, size - d - 0.5f, 0.5f, d, d);
            }
        }

        var hIcon = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        old?.Dispose();
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => RebuildLines();

    void RebuildLines()
    {
        foreach (var line in _lines) line.Dispose();
        _lines.Clear();

        int thickness = _settings.LineThickness;
        foreach (var screen in Screen.AllScreens)
        {
            var b = screen.Bounds;
            int y = _settings.LineAtBottom ? b.Bottom - thickness : b.Top;
            var line = new StatusLineForm(new Rectangle(b.Left, y, b.Width, thickness), Data.StateColor);
            line.Show();
            _lines.Add(line);
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _window.Dispose();
        foreach (var line in _lines) line.Dispose();
        _file.Dispose();
        base.ExitThreadCore();
    }
}
