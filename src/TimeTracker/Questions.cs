using System.Drawing.Drawing2D;
using System.Globalization;
using static TimeTracker.Ui;

namespace TimeTracker;

/// <summary>
/// A small question in the middle of the primary screen, on top of everything, until it is answered.
/// Unlike the task window it stays open when it loses focus. Esc (or Alt+F4) changes nothing.
/// </summary>
abstract class QuestionForm : Form
{
    protected readonly Font TextFont = new("Segoe UI", 9.75f);
    protected readonly Font SmallFont = new("Segoe UI", 8.25f);
    protected readonly Font TitleFont = new("Segoe UI Semibold", 11f);
    protected readonly Font BoldFont = new("Segoe UI Semibold", 9.75f);
    bool _shown;

    protected QuestionForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        Font = TextFont;
        SizeChanged += (_, _) => Center();
    }

    protected int W => Px(400);
    protected int Px(int logical) => LogicalToDeviceUnits(logical);

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000, WS_EX_TOOLWINDOW = 0x80;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW; // not in Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RoundCorners(Handle);
    }

    public void ShowCentered()
    {
        _shown = true;
        Center();
        Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    void Center()
    {
        if (!_shown) return;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    /// <summary>
    /// Replaces the whole content. When triggered by a click on a button it disposes,
    /// call it through BeginInvoke (never inside that button's Click handler).
    /// </summary>
    protected void SetContent(Control content)
    {
        ActiveControl = null;
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();
        Controls.Add(content);
    }

    /// <summary>Hides the question, runs the answer, and closes it once the click is over.</summary>
    protected void Answer(Action answer)
    {
        Hide();
        answer();
        BeginInvoke(Close);
    }

    /// <summary>The "change nothing" answer, for Esc.</summary>
    protected virtual void Dismiss() => Answer(() => { });

    protected Button MakeButton(string text, bool primary) => Ui.MakeButton(text, primary, primary ? BoldFont : TextFont, Px(40));

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Dismiss();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var f in new[] { TextFont, SmallFont, TitleFont, BoldFont }) f.Dispose();
        }
        base.Dispose(disposing);
    }

    protected static string Clock(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>After a long session lock with a task running: was it a break, the same task, or another one?</summary>
sealed class UnlockQuestion : QuestionForm
{
    readonly TrackerData _data;
    readonly string _taskId;
    readonly string _message;
    readonly Action _onBreak;
    readonly Action<string> _onOtherTask;

    public UnlockQuestion(TrackerData data, string taskId, DateTime lockedAt, DateTime unlockedAt, Action onBreak, Action<string> onOtherTask)
    {
        _data = data;
        _taskId = taskId;
        _onBreak = onBreak;
        _onOtherTask = onOtherTask;
        // Locked overnight: say which day it started.
        var from = lockedAt.Date == unlockedAt.Date ? Clock(lockedAt) : lockedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        _message = $"Your PC was locked from {from} to {Clock(unlockedAt)} ({FormatLength(unlockedAt - lockedAt)}). It was:";
        ShowChoices();
    }

    /// <summary>"1 h 03", or "45 min" under an hour (non-breaking spaces: never split across lines).</summary>
    public static string FormatLength(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00}" : $"{d.Minutes} min";

    List<TaskInfo> OtherTasks() => _data.TasksByRecentUse().Where(t => !t.Id.Equals(_taskId, StringComparison.OrdinalIgnoreCase)).ToList();

    Stack Intro()
    {
        var s = new Stack(W, Px(20));
        s.Add(new Label { Text = "Welcome back", Font = TitleFont, ForeColor = Ink, AutoSize = true }, Px(8));
        s.Add(Wrapped(_message, TextFont, ChipText, s.Inner), Px(14));
        return s;
    }

    void ShowChoices()
    {
        var s = Intro();
        bool others = OtherTasks().Count > 0;
        var pause = MakeButton("Break", primary: true);
        pause.Click += (_, _) => Answer(_onBreak);
        var still = MakeButton($"Still on \"{_data.NameOf(_taskId)}\"", primary: false);
        still.AutoEllipsis = true;
        still.Click += (_, _) => Dismiss();
        s.Add(pause, Px(8));
        s.Add(still, others ? Px(8) : 0);
        if (others)
        {
            var other = MakeButton("Another task…", primary: false);
            other.Click += (_, _) => BeginInvoke(ShowTaskList);
            s.Add(other, 0);
        }
        SetContent(s.Panel);
        // A stray Enter right after unlocking changes nothing.
        ActiveControl = still;
    }

    void ShowTaskList()
    {
        var s = Intro();
        var tasks = OtherTasks();
        int rowH = Px(40), gap = Px(4), total = tasks.Count * (rowH + gap) - gap;
        var list = new Panel { Height = Math.Min(total, Px(264)), AutoScroll = true };
        int width = s.Inner - (total > list.Height ? SystemInformation.VerticalScrollBarWidth + Px(4) : 0);
        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var row = TaskButton(task.Name, _data.ColorOf(task.Id));
            row.SetBounds(0, i * (rowH + gap), width, rowH);
            row.Click += (_, _) => Answer(() => _onOtherTask(task.Id));
            list.Controls.Add(row);
        }
        s.Add(list, Px(12));
        var back = MakeButton("Back", primary: false);
        back.Click += (_, _) => BeginInvoke(ShowChoices);
        s.Add(back, 0);
        SetContent(s.Panel);
        ActiveControl = list.Controls[0];
    }

    /// <summary>A task to pick: its color dot and name, left-aligned.</summary>
    Button TaskButton(string name, Color color)
    {
        var b = MakeButton(name, primary: false);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.AutoEllipsis = true;
        b.Padding = new Padding(Px(26), 0, Px(4), 0);
        b.FlatAppearance.BorderColor = Divider;
        b.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int dot = Px(12);
            using var brush = new SolidBrush(color);
            using var shape = RoundedRect(new RectangleF(Px(12), (b.Height - dot) / 2f, dot, dot), Px(3));
            e.Graphics.FillPath(brush, shape);
        };
        return b;
    }
}

/// <summary>At startup, when a task was left running since a previous day: when did it stop?</summary>
sealed class StopTimeQuestion : QuestionForm
{
    readonly DateTime _start;
    readonly DateTimePicker _picker;
    readonly Label _error;
    readonly Action<DateTime> _onStop;

    public StopTimeQuestion(TrackerData data, TimeEvent start, Action<DateTime> onStop)
    {
        _start = start.Time;
        _onStop = onStop;
        var day = _start.Date == DateTime.Today.AddDays(-1) ? "yesterday" : "on " + _start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        var s = new Stack(W, Px(20));
        s.Add(new Label { Text = "Task still running", Font = TitleFont, ForeColor = Ink, AutoSize = true }, Px(8));
        s.Add(Wrapped($"Last task: \"{data.NameOf(start.TaskId)}\", started {day} at {Clock(_start)}. What time did you stop?",
                      TextFont, ChipText, s.Inner), Px(12));

        // Suggest the minute after the start; the date is always the start's.
        var minutes = Math.Min(24 * 60 - 1, (int)_start.TimeOfDay.TotalMinutes + 1);
        _picker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true,
            Font = TitleFont, Value = _start.Date.AddMinutes(minutes),
        };
        s.Add(_picker, Px(4)).Width = Px(100);
        _error = s.Add(new Label { Text = " ", Font = SmallFont, ForeColor = ErrorText, AutoSize = true, UseMnemonic = false }, Px(10));

        var skip = MakeButton("Skip", primary: false);
        skip.Click += (_, _) => Dismiss();
        var add = MakeButton("Add pause", primary: true);
        add.Click += (_, _) => Submit();
        var row = new Panel { Height = add.Height };
        int half = (s.Inner - Px(8)) / 2;
        skip.SetBounds(0, 0, half, add.Height);
        add.SetBounds(s.Inner - half, 0, half, add.Height);
        row.Controls.AddRange([skip, add]);
        s.Add(row, 0);

        AcceptButton = add;
        Controls.Add(s.Panel);
        ActiveControl = _picker;
    }

    void Submit()
    {
        var stop = _start.Date + new TimeSpan(_picker.Value.Hour, _picker.Value.Minute, 0);
        if (stop <= _start) _error.Text = $"Pick a time after {Clock(_start)}.";
        else if (stop > DateTime.Now) _error.Text = "That time is in the future.";
        else Answer(() => _onStop(stop));
    }
}
