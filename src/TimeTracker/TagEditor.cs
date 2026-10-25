using System.Drawing.Drawing2D;
using static TimeTracker.Ui;

namespace TimeTracker;

/// <summary>
/// Tag field of the task forms: the task's tags as chips (click to remove) followed by a text box,
/// and below it the tags already used by the tasks, added with a click. Typing filters them, and
/// a typed tag that matches one of them (case and "#" ignored) takes its spelling, so no duplicates.
/// Enter or a comma adds the typed tag; Backspace in the empty box removes the last one.
/// Grows in height as tags are added.
/// </summary>
sealed class TagEditor : Panel
{
    const int MaxSuggestionLines = 2;

    readonly Func<int, int> _px;
    readonly Font _chipFont;
    readonly List<string> _tags = [];
    readonly IReadOnlyList<string> _known;
    readonly Panel _box = new() { BackColor = Color.White };
    readonly TagInput _input;
    readonly List<Chip> _chips = [], _suggestions = [];
    bool _layingOut;

    public TagEditor(string tags, IReadOnlyList<string> known, Font inputFont, Font chipFont, Func<int, int> px)
    {
        _px = px;
        _chipFont = chipFont;
        _known = known;
        foreach (var tag in TrackerData.SplitTags(tags)) Add(tag, matchKnown: false);
        Margin = Padding.Empty;

        _input = new TagInput { Font = inputFont, BorderStyle = BorderStyle.None, BackColor = Color.White };
        _input.EnterPressed = () =>
        {
            if (_input.Text.Trim().Length == 0) return false; // let Enter save the form
            CommitInput();
            return true;
        };
        _input.KeyPress += (_, e) =>
        {
            if (e.KeyChar != ',') return;
            e.Handled = true;
            CommitInput();
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Back || _input.TextLength > 0 || _tags.Count == 0) return;
            e.SuppressKeyPress = true;
            _tags.RemoveAt(_tags.Count - 1);
            UpdateChips();
        };
        _input.TextChanged += (_, _) =>
        {
            // Pasted "#a, #b": add all but the part still being typed.
            if (_input.Text.Contains(','))
            {
                var parts = _input.Text.Split(',');
                foreach (var p in parts[..^1]) Add(p, matchKnown: true);
                _input.Text = parts[^1].TrimStart();
                _input.SelectionStart = _input.TextLength;
            }
            UpdateChips();
        };
        _input.GotFocus += (_, _) => _box.Invalidate();
        _input.LostFocus += (_, _) => _box.Invalidate();

        _box.Paint += (_, e) =>
        {
            using var pen = new Pen(_input.Focused ? Muted : Border);
            e.Graphics.DrawRectangle(pen, 0, 0, _box.Width - 1, _box.Height - 1);
        };
        _box.Click += (_, _) => _input.Focus();
        _box.Controls.Add(_input);
        Controls.Add(_box);
        UpdateChips();
    }

    /// <summary>The tags, including the one still being typed, as "#a, #b".</summary>
    public string Read()
    {
        Add(_input.Text, matchKnown: true);
        _input.Text = "";
        return string.Join(", ", _tags);
    }

    static bool Same(string a, string b) =>
        string.Equals(TrackerData.TagKey(a), TrackerData.TagKey(b), StringComparison.OrdinalIgnoreCase);

    void Add(string text, bool matchKnown)
    {
        var tag = text.Trim();
        if (tag.Length == 0) return;
        if (matchKnown) tag = _known.FirstOrDefault(k => Same(k, tag)) ?? tag;
        if (!_tags.Any(t => Same(t, tag))) _tags.Add(tag);
    }

    void CommitInput()
    {
        Add(_input.Text, matchKnown: true);
        _input.Text = ""; // updates the chips
    }

    /// <summary>
    /// Runs a chip's action after its Click event: the update disposes the chips,
    /// which must not happen inside the clicked chip's own handler.
    /// </summary>
    void AfterClick(Action action) => BeginInvoke(() =>
    {
        action();
        UpdateChips();
        _input.Focus();
    });

    void UpdateChips()
    {
        SuspendLayout();
        foreach (var c in _chips.Concat(_suggestions)) c.Dispose();
        _chips.Clear();
        _suggestions.Clear();

        foreach (var tag in _tags)
        {
            var chip = new Chip(tag, removable: true, _chipFont, _px);
            chip.Click += (_, _) => AfterClick(() => _tags.Remove(tag));
            _chips.Add(chip);
        }
        _box.Controls.AddRange(_chips.ToArray());

        var filter = TrackerData.TagKey(_input.Text.Trim());
        foreach (var tag in _known)
        {
            if (_tags.Any(t => Same(t, tag))) continue;
            if (!TrackerData.TagKey(tag).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var chip = new Chip(tag, removable: false, _chipFont, _px);
            chip.Click += (_, _) => AfterClick(() =>
            {
                Add(tag, matchKnown: false);
                _input.Text = "";
            });
            _suggestions.Add(chip);
        }
        Controls.AddRange(_suggestions.ToArray());

        _input.PlaceholderText = _tags.Count == 0 ? "#clientC, #billable" : "add a tag";
        ResumeLayout();
        LayoutContent();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutContent();
    }

    void LayoutContent()
    {
        if (_layingOut || Width <= 0) return;
        _layingOut = true;
        int pad = _px(4), gap = _px(4), lineH = _px(22), inner = Width - 2 * pad;

        // In the box: the chips, then the text box on the rest of the last line, or on a new one.
        int x = 0, y = 0;
        foreach (var c in _chips)
        {
            int w = Math.Min(c.PreferredWidth, inner);
            if (x > 0 && x + w > inner) { x = 0; y += lineH + gap; }
            c.SetBounds(pad + x, pad + y, w, lineH);
            x += w + gap;
        }
        if (x > 0 && inner - x < _px(90)) { x = 0; y += lineH + gap; }
        _input.SetBounds(pad + x + _px(2), pad + y + (lineH - _input.Height) / 2, inner - x - _px(2), _input.Height);
        _box.SetBounds(0, 0, Width, y + lineH + 2 * pad);

        // Below: the known tags, in order, on at most two lines.
        int bottom = _box.Bottom, lines = 0;
        x = Width;
        foreach (var s in _suggestions)
        {
            int w = Math.Min(s.PreferredWidth, Width);
            if (x > 0 && x + w > Width)
            {
                if (++lines > MaxSuggestionLines) { s.Visible = false; continue; }
                x = 0;
                bottom += gap;
                bottom += lineH;
            }
            s.SetBounds(x, bottom - lineH, w, lineH);
            s.Visible = lines <= MaxSuggestionLines;
            x += w + gap;
        }
        Height = bottom;
        _layingOut = false;
    }

    sealed class TagInput : TextBox
    {
        /// <summary>Returns true when it handled the key.</summary>
        public Func<bool>? EnterPressed;

        protected override bool ProcessDialogKey(Keys keyData) =>
            keyData == Keys.Enter && EnterPressed?.Invoke() == true || base.ProcessDialogKey(keyData);
    }

    /// <summary>A tag of the task (gray, with ×) or a known tag to add (outlined, with +).</summary>
    sealed class Chip : Control
    {
        const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        readonly bool _removable;
        readonly Func<int, int> _px;
        bool _hover;

        public Chip(string tag, bool removable, Font font, Func<int, int> px)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            _removable = removable;
            _px = px;
            Text = tag;
            Font = font;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            AccessibleName = (removable ? "Remove " : "Add ") + tag;
            // Text, plus the × or + mark and the margins.
            const TextFormatFlags measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            PreferredWidth = TextRenderer.MeasureText(tag, font, Size.Empty, measure).Width + px(30);
        }

        public int PreferredWidth { get; }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int Px(int v) => _px(v);
            using var shape = RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), Px(4));
            if (_removable)
            {
                using var fill = new SolidBrush(_hover ? Border : ChipBack);
                g.FillPath(fill, shape);
            }
            else
            {
                using var fill = new SolidBrush(_hover ? RowCurrent : Color.White);
                using var pen = new Pen(Border);
                g.FillPath(fill, shape);
                g.DrawPath(pen, shape);
            }

            // The mark: × on the right to remove, + on the left to add.
            float m = Px(3), cy = Height / 2f;
            float mx = _removable ? Width - Px(11) : Px(10);
            using (var pen = new Pen(_hover ? Ink : ChipText, 1.3f * Px(16) / 16f))
            {
                if (_removable)
                {
                    g.DrawLine(pen, mx - m, cy - m, mx + m, cy + m);
                    g.DrawLine(pen, mx + m, cy - m, mx - m, cy + m);
                }
                else
                {
                    g.DrawLine(pen, mx - m, cy, mx + m, cy);
                    g.DrawLine(pen, mx, cy - m, mx, cy + m);
                }
            }
            int textLeft = _removable ? Px(8) : Px(18);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Width - Px(28), Height), _hover ? Ink : ChipText, Flags);
        }
    }
}
