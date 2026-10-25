using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static TimeTracker.Ui;

namespace TimeTracker;

/// <summary>
/// The small task management window above the tray icon: header with the current task
/// and Pause / Resume, the task list with inline editing, and the new task form.
/// Hidden (not closed) when it loses focus or on Esc; rebuilt each time it is shown.
/// </summary>
sealed class TaskWindow : Form
{
    static readonly Color Subtle = ColorTranslator.FromHtml("#5B6570");
    static readonly Color Invalid = ColorTranslator.FromHtml("#FDECEA");
    static readonly Color ExcelDot = ColorTranslator.FromHtml("#F5C542");

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    readonly TrackerContext _app;
    readonly Font _font = new("Segoe UI", 9.75f);
    readonly Font _bold = new("Segoe UI Semibold", 9.75f);
    readonly Font _small = new("Segoe UI", 8.25f);
    readonly Font _kickerFont = new("Segoe UI Semibold", 7.5f);
    readonly Font _titleFont = new("Segoe UI Semibold", 11f);
    readonly Font _mono = new("Consolas", 10.5f);
    readonly Font _monoField = new("Consolas", 9.75f);
    readonly Font _monoSmall = new("Consolas", 8.25f);

    readonly FlowLayoutPanel _root;
    readonly Panel _header = new();
    readonly Label _kicker, _name, _time;
    readonly Button _pause;
    Label? _editStatus;
    Control? _focusTarget;
    bool _creating, _dialogOpen, _rebuildPending;
    string? _editingId;
    readonly List<Color> _rejectedColors = [];
    Point _anchor;

    public long HiddenAt { get; private set; }

    public TaskWindow(TrackerContext app)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        Font = _font;

        _root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        Controls.Add(_root);

        _header.Margin = Padding.Empty;
        _kicker = new Label { Font = _kickerFont, ForeColor = Color.White, BackColor = Color.Transparent, UseMnemonic = false };
        _name = new Label { Font = _titleFont, ForeColor = Color.White, BackColor = Color.Transparent, AutoEllipsis = true, UseMnemonic = false };
        _time = new Label { Font = _mono, ForeColor = Color.White, BackColor = Color.Transparent, AutoSize = true };
        _pause = new Button { FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = _bold, Cursor = Cursors.Hand, UseVisualStyleBackColor = false, TabStop = false };
        _pause.FlatAppearance.BorderSize = 0;
        _pause.Click += (_, _) => _app.TogglePause();
        _header.Controls.AddRange([_kicker, _name, _time, _pause]);

        SizeChanged += (_, _) => Reposition();
    }

    int W => Px(360);
    int Px(int logical) => LogicalToDeviceUnits(logical);

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

    public void ShowNear(Point cursor)
    {
        _anchor = cursor;
        RebuildNow();
        Show();
        Activate();
        SetForegroundWindow(Handle);
        _focusTarget?.Focus();
    }

    /// <summary>Called every second by the app.</summary>
    public void Tick()
    {
        if (!Visible) return;
        if (_header.Parent != null) UpdateHeader();
        if (_editStatus != null) _editStatus.Text = _app.EditStatus;
    }

    /// <summary>
    /// Rebuilds the content right after the current event: it is often triggered by a click on
    /// a button that the rebuild disposes, which must not happen inside its own Click handler.
    /// </summary>
    public void Rebuild()
    {
        if (!IsHandleCreated)
        {
            RebuildNow();
            return;
        }
        if (_rebuildPending) return;
        _rebuildPending = true;
        BeginInvoke(RebuildNow);
    }

    void RebuildNow()
    {
        _rebuildPending = false;
        // Keep the focus on the window while its focused control is disposed,
        // otherwise Windows may treat the window as deactivated.
        ActiveControl = null;
        SuspendLayout();
        _root.SuspendLayout();
        var old = _root.Controls.Cast<Control>().ToList();
        _root.Controls.Clear();
        foreach (var c in old)
            if (c != _header) c.Dispose();
        _editStatus = null;
        _focusTarget = null;
        AcceptButton = null;

        if (_app.EditMode)
        {
            _creating = false;
            _editingId = null;
        }
        if (_creating)
        {
            BuildNewTask();
        }
        else
        {
            _header.Size = new Size(W, Px(64));
            _root.Controls.Add(_header);
            UpdateHeader();
            if (_app.EditMode) BuildEditMode();
            else BuildList();
        }
        if (_app.LastError is { } error)
        {
            var s = new Stack(W, Px(16));
            s.Add(Wrapped(error, _small, ErrorText, s.Inner), 0);
            _root.Controls.Add(s.Panel);
        }

        _root.ResumeLayout();
        ResumeLayout();
        Reposition();
        if (Visible) _focusTarget?.Focus();
    }

    void UpdateHeader()
    {
        var d = _app.Data;
        var id = d.CurrentTaskId;
        _header.BackColor = d.StateColor;
        _kicker.Text = _app.EditMode ? "EDIT MODE" : d.IsRunning ? "RUNNING" : "PAUSED";
        _name.Text = d.IsRunning ? d.NameOf(id!) : id != null ? "Last task: " + d.NameOf(id) : "No task yet";
        _time.Text = d.Since is { } since ? TrackerData.FormatDuration(DateTime.Now - since, withSeconds: true) : "";
        _pause.Visible = !_app.EditMode;
        _pause.Text = d.IsRunning ? "Pause" : "Resume";
        _pause.Enabled = d.IsRunning || _app.CanResume;
        _pause.BackColor = Blend(d.StateColor, Color.White, 0.22);
        _pause.FlatAppearance.MouseOverBackColor = Blend(d.StateColor, Color.White, 0.32);
        _pause.FlatAppearance.MouseDownBackColor = Blend(d.StateColor, Color.White, 0.40);
        LayoutHeader();
    }

    void LayoutHeader()
    {
        int h = _header.Height, left = Px(16), right = W - Px(14);
        // Not _pause.Visible: it reads false while the window itself is still hidden.
        if (!_app.EditMode)
        {
            var size = new Size(TextRenderer.MeasureText("Resume", _bold).Width + Px(28), Px(34));
            _pause.SetBounds(right - size.Width, (h - size.Height) / 2, size.Width, size.Height);
            right = _pause.Left - Px(12);
        }
        _time.Location = new Point(right - _time.Width, (h - _time.Height) / 2);
        right = _time.Left - Px(12);
        int kickerH = TextRenderer.MeasureText("A", _kickerFont).Height, nameH = TextRenderer.MeasureText("Ag", _titleFont).Height;
        int top = (h - kickerH - nameH) / 2;
        _kicker.SetBounds(left, top, right - left, kickerH);
        _name.SetBounds(left, top + kickerH, right - left, nameH);
    }

    // ---- Normal mode: task list ----

    void BuildList()
    {
        var titleRow = new Panel { Size = new Size(W, Px(44)), Margin = Padding.Empty };
        var title = new Label { Text = "Switch task", Font = _bold, ForeColor = Muted, AutoSize = true, Location = new Point(Px(16), Px(14)) };
        var close = IconButton(DrawClose, "Close");
        close.Location = new Point(W - Px(12) - close.Width, Px(8));
        close.Click += (_, _) => Hide();
        titleRow.Controls.AddRange([title, close]);
        _root.Controls.Add(titleRow);

        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty,
            Padding = new Padding(Px(8), 0, Px(8), Px(8)),
            Width = W,
        };
        var rows = BuildRows(W - list.Padding.Horizontal);
        int ContentHeight() => rows.Sum(r => r.Height + r.Margin.Vertical) + list.Padding.Vertical;
        bool scroll = ContentHeight() > Px(300);
        if (scroll)
        {
            // Too many tasks: scroll, and leave room for the scrollbar.
            foreach (var r in rows) r.Dispose();
            rows = BuildRows(W - list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        }
        list.Height = scroll ? Px(300) : ContentHeight();
        list.Controls.AddRange(rows.ToArray());
        _root.Controls.Add(list);
        if (_editingId != null && rows.FirstOrDefault(r => r is Panel) is { } editor)
        {
            list.ScrollControlIntoView(editor);
            // The editor grows with its tags: without a scrollbar, the list grows with it.
            if (!scroll) editor.SizeChanged += (_, _) => list.Height = ContentHeight();
        }

        if (_app.Data.SkippedRows > 0)
        {
            var s = new Stack(W, Px(16));
            s.Add(Wrapped($"{_app.Data.SkippedRows} invalid row(s) ignored in the Events sheet.", _small, Subtle, s.Inner), 0);
            _root.Controls.Add(s.Panel);
        }

        var footer = new Panel { Size = new Size(W, Px(62)), Margin = Padding.Empty };
        footer.Paint += (_, e) => { using var pen = new Pen(Divider); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var newTask = MakeButton("+ New task", primary: true);
        newTask.Click += (_, _) => { _creating = true; _editingId = null; _rejectedColors.Clear(); Rebuild(); };
        var excel = MakeButton("Edit in Excel", primary: false);
        excel.Click += (_, _) => { _editingId = null; _app.OpenInExcel(); };
        var buttons = ButtonRow(W - Px(24), (newTask, 1), (excel, 1));
        buttons.Location = new Point(Px(12), Px(11));
        footer.Controls.Add(buttons);
        _root.Controls.Add(footer);
    }

    List<Control> BuildRows(int width)
    {
        var d = _app.Data;
        var rows = new List<Control>();
        foreach (var task in d.TasksByRecentUse())
        {
            if (task.Id == _editingId) rows.Add(BuildEditor(task, width));
            else rows.Add(new TaskRow(this, task, d.ColorOf(task.Id), d.IsRunning && task.Id == d.CurrentTaskId, width));
        }
        if (rows.Count == 0)
            rows.Add(new Label { Text = "No tasks yet: create one below.", Font = _font, ForeColor = Subtle, AutoSize = true, Margin = new Padding(Px(8), Px(4), 0, Px(12)) });
        return rows;
    }

    void Pick(string id)
    {
        _app.StartTask(id);
        if (_app.LastError == null) Hide();
    }

    void StartEditing(string id)
    {
        _editingId = id;
        Rebuild();
    }

    Control BuildEditor(TaskInfo task, int width)
    {
        var s = new Stack(width, Px(10)) { BackColor = RowHover };
        s.Panel.Margin = new Padding(0, 0, 0, Px(2));
        var fields = AddTaskFields(s, task, "Color", suggest: false);
        var cancel = MakeButton("Cancel", primary: false);
        cancel.Click += (_, _) => { _editingId = null; Rebuild(); };
        var save = MakeButton("Save", primary: true);
        save.Click += (_, _) =>
        {
            if (!fields.TryRead(out var name, out var tags, out var color)) return;
            _editingId = null;
            if (!_app.UpdateTask(task with { Name = name, Tags = tags, Color = color })) Rebuild();
        };
        s.Add(ButtonRow(s.Inner, (cancel, 1), (save, 1)), 0);
        AcceptButton = save;
        _focusTarget = fields.Name;
        return s.Panel;
    }

    // ---- New task ----

    void BuildNewTask()
    {
        var s = new Stack(W, Px(16));
        s.Add(new Label { Text = "New task", Font = _titleFont, ForeColor = Ink, AutoSize = true }, Px(12));
        var fields = AddTaskFields(s, null, "Color (suggested, editable)", suggest: true);
        var cancel = MakeButton("Cancel", primary: false);
        cancel.Click += (_, _) => { _creating = false; Rebuild(); };
        var create = MakeButton("Create and start", primary: true);
        create.Click += (_, _) =>
        {
            if (!fields.TryRead(out var name, out var tags, out var color)) return;
            _creating = false;
            if (_app.CreateTask(name, tags, color)) Hide();
            else Rebuild();
        };
        s.Add(ButtonRow(s.Inner, (cancel, 1), (create, 1.4f)), 0);
        AcceptButton = create;
        _focusTarget = fields.Name;
        _root.Controls.Add(s.Panel);
    }

    // ---- Edit mode ----

    void BuildEditMode()
    {
        var s = new Stack(W, Px(16));
        var titleRow = new Panel { Height = Px(22) };
        var dot = new Panel { Size = new Size(Px(10), Px(10)), Location = new Point(0, Px(6)) };
        dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(ExcelDot);
            e.Graphics.FillEllipse(b, 0, 0, dot.Width - 1, dot.Height - 1);
        };
        var title = new Label { Text = "Open in Excel", Font = _bold, ForeColor = Ink, AutoSize = true, Location = new Point(Px(18), Px(1)) };
        titleRow.Controls.AddRange([dot, title]);
        s.Add(titleRow, Px(6));
        s.Add(Wrapped("The app has released the file and writes nothing. Edit what you want, save and close it: " +
                      "everything is reloaded automatically.", _font, ChipText, s.Inner), Px(8));
        _editStatus = s.Add(Wrapped(_app.EditStatus, _small, Subtle, s.Inner), Px(12));
        var done = MakeButton("Done, reload", primary: false);
        done.Click += (_, _) => _app.LeaveEditMode();
        s.Add(done, 0);
        _root.Controls.Add(s.Panel);
    }

    // ---- Task fields (new task and inline edit) ----

    sealed class TaskFields
    {
        public required TextBox Name, Hex;
        public required TagEditor Tags;

        public bool TryRead(out string name, out string tags, out string color)
        {
            name = Name.Text.Trim();
            tags = Tags.Read();
            color = TrackerData.NormalizeHex(Hex.Text) ?? "";
            Name.BackColor = name.Length == 0 ? Invalid : Color.White;
            Hex.BackColor = color.Length == 0 ? Invalid : Color.White;
            if (name.Length == 0) Name.Focus();
            else if (color.Length == 0) Hex.Focus();
            return name.Length > 0 && color.Length > 0;
        }
    }

    TaskFields AddTaskFields(Stack s, TaskInfo? task, string colorLabel, bool suggest)
    {
        var name = new TextBox { Text = task?.Name ?? "", Font = _font, PlaceholderText = "e.g. Client C offer" };
        var tags = new TagEditor(task?.Tags ?? "", _app.Data.KnownTags(), _monoField, _monoSmall, Px);
        var initial = suggest ? ColorSuggester.Suggest(_app.Data.RecentColors())
            : TrackerData.TryParseColor(task?.Color, out var c) ? c : TrackerData.UnknownColor;
        var hex = new TextBox { Text = TrackerData.ToHex(initial), Font = _monoField, PlaceholderText = "#2F6FD6" };

        s.Add(FieldLabel("Name"), Px(4));
        s.Add(name, Px(10));
        s.Add(FieldLabel("Tags (Enter to add; click a tag below to reuse it)"), Px(4));
        s.Add(tags, Px(10));
        s.Add(FieldLabel(colorLabel), Px(4));

        var row = new Panel { Height = hex.Height };
        var swatch = new Button { FlatStyle = FlatStyle.Flat, BackColor = initial, Cursor = Cursors.Hand, UseVisualStyleBackColor = false, AccessibleName = "Pick a color" };
        swatch.FlatAppearance.BorderColor = Border;
        swatch.FlatAppearance.MouseOverBackColor = initial;
        swatch.SetBounds(0, 0, Px(44), hex.Height);
        swatch.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = swatch.BackColor, FullOpen = true, AnyColor = true };
            _dialogOpen = true;
            try { if (dialog.ShowDialog(this) == DialogResult.OK) hex.Text = TrackerData.ToHex(dialog.Color); }
            finally { _dialogOpen = false; Activate(); }
        };
        hex.TextChanged += (_, _) =>
        {
            if (!TrackerData.TryParseColor(hex.Text, out var col)) return;
            swatch.BackColor = swatch.FlatAppearance.MouseOverBackColor = col;
            hex.BackColor = Color.White;
        };
        int hexLeft = swatch.Right + Px(8), hexRight = s.Inner;
        if (suggest)
        {
            var other = MakeButton("Other", primary: false);
            other.Font = _small;
            other.Height = hex.Height;
            other.Width = TextRenderer.MeasureText(other.Text, _small).Width + Px(20);
            other.Location = new Point(s.Inner - other.Width, 0);
            other.Click += (_, _) =>
            {
                // Next best suggestion, also avoiding the ones already proposed.
                if (TrackerData.TryParseColor(hex.Text, out var current)) _rejectedColors.Add(current);
                hex.Text = TrackerData.ToHex(ColorSuggester.Suggest(_app.Data.RecentColors().Concat(_rejectedColors)));
            };
            row.Controls.Add(other);
            hexRight = other.Left - Px(8);
        }
        hex.SetBounds(hexLeft, 0, hexRight - hexLeft, hex.Height);
        row.Controls.AddRange([swatch, hex]);
        s.Add(row, Px(14));

        return new TaskFields { Name = name, Tags = tags, Hex = hex };
    }

    Label FieldLabel(string text) => new() { Text = text, Font = _small, ForeColor = Muted, AutoSize = true };

    // ---- Building blocks ----

    Button MakeButton(string text, bool primary) => Ui.MakeButton(text, primary, primary ? _bold : _font, Px(38));

    /// <summary>Buttons side by side, widths proportional to their weights.</summary>
    Panel ButtonRow(int width, params (Button Button, float Weight)[] items)
    {
        var row = new Panel { Size = new Size(width, items[0].Button.Height), Margin = Padding.Empty };
        int gap = Px(8), available = width - gap * (items.Length - 1), x = 0;
        float total = items.Sum(i => i.Weight);
        for (int i = 0; i < items.Length; i++)
        {
            int w = i == items.Length - 1 ? width - x : (int)(available * items[i].Weight / total);
            items[i].Button.SetBounds(x, 0, w, row.Height);
            row.Controls.Add(items[i].Button);
            x += w + gap;
        }
        return row;
    }

    Button IconButton(Action<Graphics, Color> drawIcon, string accessibleName)
    {
        var b = new Button
        {
            FlatStyle = FlatStyle.Flat, Size = new Size(Px(32), Px(32)), BackColor = Color.Transparent, ForeColor = Subtle,
            Cursor = Cursors.Hand, AccessibleName = accessibleName, TabStop = false, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Divider;
        b.FlatAppearance.MouseDownBackColor = Border;
        b.Paint += (_, e) =>
        {
            // Icons are drawn on a 16×16 grid, centered and scaled to the DPI.
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Px(16) / 16f;
            g.TranslateTransform((b.Width - Px(16)) / 2f, (b.Height - Px(16)) / 2f);
            g.ScaleTransform(scale, scale);
            drawIcon(g, b.ForeColor);
            g.ResetTransform();
        };
        return b;
    }

    static void DrawClose(Graphics g, Color color)
    {
        using var pen = new Pen(color, 1.6f);
        g.DrawLine(pen, 4, 4, 12, 12);
        g.DrawLine(pen, 12, 4, 4, 12);
    }

    static void DrawPencil(Graphics g, Color color)
    {
        using var pen = new Pen(color, 1.5f) { LineJoin = LineJoin.Round };
        g.DrawPolygon(pen, new PointF[] { new(10.5f, 2.5f), new(13.5f, 5.5f), new(6, 13), new(3, 13), new(3, 10) });
    }

    /// <summary>One task in the list: click to start it, pencil to edit it.</summary>
    sealed class TaskRow : Control
    {
        readonly TaskWindow _w;
        readonly TaskInfo _task;
        readonly Color _color;
        readonly bool _current;
        readonly string[] _tags;
        readonly Button _edit;
        bool _hover;

        public TaskRow(TaskWindow w, TaskInfo task, Color color, bool current, int width)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _w = w;
            _task = task;
            _color = color;
            _current = current;
            _tags = TrackerData.SplitTags(task.Tags).ToArray();
            Size = new Size(width, w.Px(52));
            Margin = new Padding(0, 0, 0, w.Px(2));
            Cursor = Cursors.Hand;
            BackColor = Color.White;
            AccessibleName = task.Name;

            _edit = w.IconButton(DrawPencil, "Edit task");
            _edit.Location = new Point(width - w.Px(4) - _edit.Width, (Height - _edit.Height) / 2);
            _edit.Click += (_, _) => w.StartEditing(task.Id);
            _edit.MouseEnter += (_, _) => SetHover(true);
            Controls.Add(_edit);
        }

        void SetHover(bool hover)
        {
            if (_hover == hover) return;
            _hover = hover;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); SetHover(true); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position))); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); _w.Pick(_task.Id); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int Px(int v) => _w.Px(v);

            if (_current || _hover)
            {
                using var bg = new SolidBrush(_current ? RowCurrent : RowHover);
                using var shape = RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), Px(8));
                g.FillPath(bg, shape);
            }

            int dot = Px(12), x = Px(10);
            using (var brush = new SolidBrush(_color))
            using (var shape = RoundedRect(new RectangleF(x, (Height - dot) / 2f, dot, dot), Px(3)))
                g.FillPath(brush, shape);
            x += dot + Px(12);

            int right = _edit.Left - Px(8);
            if (_current)
            {
                var size = TextRenderer.MeasureText("CURRENT", _w._kickerFont);
                TextRenderer.DrawText(g, "CURRENT", _w._kickerFont, new Point(right - size.Width, (Height - size.Height) / 2), ChipText);
                right -= size.Width + Px(8);
            }

            const TextFormatFlags measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            const TextFormatFlags flags = measure | TextFormatFlags.EndEllipsis;
            int nameH = TextRenderer.MeasureText(g, "Ag", _w._bold, Size.Empty, measure).Height;
            int chipH = TextRenderer.MeasureText(g, "Ag", _w._monoSmall, Size.Empty, measure).Height + Px(2);
            int gap = Px(4);
            int top = _tags.Length == 0 ? (Height - nameH) / 2 : (Height - nameH - gap - chipH) / 2;
            TextRenderer.DrawText(g, _task.Name, _w._bold, new Rectangle(x, top, right - x, nameH), Ink, flags);

            int cx = x, cy = top + nameH + gap;
            using var chipBrush = new SolidBrush(ChipBack);
            foreach (var tag in _tags)
            {
                int w = TextRenderer.MeasureText(g, tag, _w._monoSmall, Size.Empty, measure).Width + Px(12);
                if (cx + w > right) break;
                using (var shape = RoundedRect(new RectangleF(cx, cy, w, chipH), Px(4)))
                    g.FillPath(chipBrush, shape);
                TextRenderer.DrawText(g, tag, _w._monoSmall, new Rectangle(cx, cy, w, chipH), ChipText,
                    flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                cx += w + Px(4);
            }
        }
    }

    // ---- Window behavior ----

    void Reposition()
    {
        var area = Screen.FromPoint(_anchor).WorkingArea;
        int margin = Px(12);
        bool bottom = _anchor.Y >= area.Top + area.Height / 2; // taskbar at the bottom (usual) or at the top
        int x = Math.Clamp(_anchor.X - Width / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        int y = bottom ? area.Bottom - Height - margin : area.Top + margin;
        Location = new Point(x, y);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_dialogOpen) return;
        // Check once things have settled: hide only if another window really took the foreground.
        BeginInvoke(() =>
        {
            if (Visible && !_dialogOpen && GetForegroundWindow() != Handle) Hide();
        });
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        HiddenAt = Environment.TickCount64;
        _editingId = null;
        _creating = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var f in new[] { _font, _bold, _small, _kickerFont, _titleFont, _mono, _monoField, _monoSmall }) f.Dispose();
        }
        base.Dispose(disposing);
    }
}
