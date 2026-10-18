using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TimeTracker;

static class Program
{
    /// <summary>Passed to the new instance by Restart.</summary>
    public const string RestartArg = "--restart";

    [STAThread]
    static void Main(string[] args)
    {
        // Two instances would fight over the lock on hours.xlsx.
        using var mutex = new Mutex(true, @"Local\TimeTracker.SingleInstance", out bool owned);
        if (!owned && args.Contains(RestartArg))
        {
            // Started by Restart: the previous instance is exiting, wait until it releases the mutex
            // (it does so after releasing hours.xlsx).
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; /* it exited without releasing it: ours now */ }
        }
        if (!owned)
        {
            MessageBox.Show("Time Tracker is already running (see the notification area).", "Time Tracker");
            return;
        }
        try { Run(); }
        finally { mutex.ReleaseMutex(); }
    }

    static void Run()
    {
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
    DateTime? _lockedAt;
    QuestionForm? _question;

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
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Restart", null, (_, _) => Restart());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
        _tray.Visible = true;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        RebuildLines();
        Refresh();

        // A task left running since a previous day: forgot to pause, or shut down while locked.
        if (!EditMode && AskAtStartup(Data, DateTime.Today) is { } start)
            ShowQuestion(new StopTimeQuestion(Data, start, stop => AnswerIfUnchanged(start, () => _file.AppendEvent(stop, "Pause"))));
    }

    /// <summary>The Start event to ask about at startup: the last event, if it is a Start before today.</summary>
    internal static TimeEvent? AskAtStartup(TrackerData data, DateTime today) =>
        data.Last is { IsStart: true } last && last.Time.Date < today ? last : null;

    /// <summary>
    /// The Start event to ask about after an unlock: the lock lasted more than the threshold,
    /// and that task was running when it began, with nothing written since.
    /// </summary>
    internal static TimeEvent? AskAfterUnlock(TrackerData data, DateTime lockedAt, DateTime unlockedAt, int thresholdMinutes) =>
        unlockedAt - lockedAt > TimeSpan.FromMinutes(thresholdMinutes) && data.Last is { IsStart: true } last && last.Time <= lockedAt
            ? last : null;

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _lockedAt = Now();
        else if (e.Reason == SessionSwitchReason.SessionUnlock && _lockedAt is { } lockedAt)
        {
            _lockedAt = null;
            OnUnlock(lockedAt, Now());
        }
    }

    void OnUnlock(DateTime lockedAt, DateTime unlockedAt)
    {
        // In edit mode the app cannot write the answer anyway.
        if (EditMode || _question != null) return;
        if (AskAfterUnlock(Data, lockedAt, unlockedAt, _settings.UnlockQuestionMinutes) is not { } running) return;
        ShowQuestion(new UnlockQuestion(Data, running.TaskId, lockedAt, unlockedAt,
            onBreak: () => AnswerIfUnchanged(running,
                () => _file.AppendEvents((lockedAt, "Pause", ""), (unlockedAt, "Start", running.TaskId))),
            onOtherTask: id => AnswerIfUnchanged(running, () => _file.AppendEvent(lockedAt, "Start", id))));
    }

    void ShowQuestion(QuestionForm question)
    {
        // The task window would hide anyway when the question takes the focus.
        _window.Hide();
        _question = question;
        question.FormClosed += (_, _) => _question = null;
        question.ShowCentered();
    }

    /// <summary>Writes the answer, unless something else was written while the question was open.</summary>
    void AnswerIfUnchanged(TimeEvent askedAbout, Func<TrackerData> write)
    {
        if (Data.Last == askedAbout) Write(write);
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

    void OpenSettings()
    {
        try { Settings.OpenInEditor(); }
        catch (Exception e) { _tray.ShowBalloonTip(5000, "Time Tracker", "Could not open settings.json: " + e.Message, ToolTipIcon.Error); }
    }

    /// <summary>
    /// Starts a new instance (to apply the settings) and exits this one. The new one waits in Main
    /// until this one has released hours.xlsx and the single-instance mutex.
    /// </summary>
    void Restart()
    {
        // The new instance would only show the error and stop: keep this one running instead.
        try { Settings.Load(); }
        catch (Exception e)
        {
            MessageBox.Show($"Could not read {Settings.FilePath}:\n\n{e.Message}\n\nFix it, then restart.", "Time Tracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try { Process.Start(Environment.ProcessPath!, Program.RestartArg); }
        catch (Exception e)
        {
            _tray.ShowBalloonTip(5000, "Time Tracker", "Could not restart: " + e.Message, ToolTipIcon.Error);
            return;
        }
        ExitThread();
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

    /// <summary>
    /// Reads the locked file and regenerates its Summary sheet (it may have been edited by hand);
    /// if it cannot be read, releases it so it can be fixed by hand.
    /// </summary>
    bool ReloadOrRelease()
    {
        try
        {
            Data = _file.Load();
        }
        catch (Exception e)
        {
            _file.Unlock();
            EditMode = true;
            _sawOpenElsewhere = false;
            LastError = $"Could not read {Path.GetFileName(_file.FilePath)}: {e.Message} Fix it in Excel, then close it.";
            return false;
        }
        // Nothing to sum up: don't rewrite the file.
        if (Data.Events.Count == 0) return true;
        try { Data = _file.RefreshSummary(); }
        catch (Exception e) { LastError = "Could not update the Summary sheet: " + e.Message; }
        return true;
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
            using (var path = Ui.RoundedRect(new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset), size / 8f))
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
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _question?.Dispose();
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _window.Dispose();
        foreach (var line in _lines) line.Dispose();
        _file.Dispose();
        base.ExitThreadCore();
    }
}
