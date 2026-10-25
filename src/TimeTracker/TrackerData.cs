using System.Text.RegularExpressions;

namespace TimeTracker;

sealed record TaskInfo(string Id, string Name, string Tags, string Color);

sealed record TimeEvent(DateTime Time, bool IsStart, string TaskId);

/// <summary>
/// Everything read from hours.xlsx, and the state derived from it.
/// Events are sorted by time; the current state is the last event.
/// </summary>
sealed class TrackerData
{
    public static readonly Color PauseColor = ColorTranslator.FromHtml("#8A919A");
    public static readonly Color UnknownColor = ColorTranslator.FromHtml("#555555");
    public static readonly TrackerData Empty = new([], [], 0);

    public IReadOnlyList<TaskInfo> Tasks { get; }
    public IReadOnlyList<TimeEvent> Events { get; }
    /// <summary>Rows of the Events sheet that could not be read (bad date, unknown type…).</summary>
    public int SkippedRows { get; }

    public TrackerData(List<TaskInfo> tasks, List<TimeEvent> events, int skippedRows)
    {
        Tasks = tasks;
        // OrderBy is stable: events with the same timestamp keep their row order.
        Events = events.OrderBy(e => e.Time).ToList();
        SkippedRows = skippedRows;
    }

    public TimeEvent? Last => Events.Count > 0 ? Events[^1] : null;
    public bool IsRunning => Last?.IsStart == true;
    /// <summary>Time of the last event: start of the running task, or of the pause.</summary>
    public DateTime? Since => Last?.Time;
    /// <summary>The running task or, when paused, the last one started (what Resume restarts).</summary>
    public string? CurrentTaskId => Events.LastOrDefault(e => e.IsStart)?.TaskId;

    public TaskInfo? Find(string id) => Tasks.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public string NameOf(string id) => Find(id)?.Name ?? $"unknown task ({id})";
    public Color ColorOf(string id) => TryParseColor(Find(id)?.Color, out var c) ? c : UnknownColor;
    public Color StateColor => IsRunning ? ColorOf(CurrentTaskId!) : PauseColor;

    /// <summary>Most recently started first; never started tasks follow in sheet order.</summary>
    public List<TaskInfo> TasksByRecentUse()
    {
        var lastStart = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Events)
            if (e.IsStart) lastStart[e.TaskId] = e.Time;
        return Tasks.OrderByDescending(t => lastStart.GetValueOrDefault(t.Id, DateTime.MinValue)).ToList();
    }

    /// <summary>Colors of the most recently used tasks, completed with the newest tasks if needed.</summary>
    public List<Color> RecentColors(int count = 5)
    {
        var ids = TasksByRecentUse().Select(t => t.Id).Take(count).ToList();
        foreach (var t in Tasks.Reverse())
            if (ids.Count < count && !ids.Contains(t.Id)) ids.Add(t.Id);
        return ids.Select(id => Find(id)).OfType<TaskInfo>()
            .Select(t => TryParseColor(t.Color, out var c) ? c : (Color?)null).OfType<Color>().ToList();
    }

    /// <summary>
    /// Time spent per day and task: each Start lasts until the next event, the last one until
    /// <paramref name="now"/> if it is still running. Pauses count nothing; midnight splits an interval.
    /// </summary>
    public List<(DateTime Day, string TaskId, TimeSpan Time)> DailyTotals(DateTime now)
    {
        var totals = new Dictionary<(DateTime Day, string TaskId), TimeSpan>();
        for (int i = 0; i < Events.Count; i++)
        {
            var e = Events[i];
            if (!e.IsStart) continue;
            // The ID as written in Tasks, so "t1" and "T1" add up.
            var id = Find(e.TaskId)?.Id ?? e.TaskId;
            var end = i + 1 < Events.Count ? Events[i + 1].Time : now;
            for (var from = e.Time; from < end; from = from.Date.AddDays(1))
            {
                var to = end < from.Date.AddDays(1) ? end : from.Date.AddDays(1);
                totals[(from.Date, id)] = totals.GetValueOrDefault((from.Date, id)) + (to - from);
            }
        }
        return totals.Select(t => (t.Key.Day, t.Key.TaskId, t.Value)).ToList();
    }

    public static string FormatDuration(TimeSpan d, bool withSeconds)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        int h = (int)d.TotalHours;
        return withSeconds ? $"{h:00}:{d.Minutes:00}:{d.Seconds:00}" : $"{h}:{d.Minutes:00}";
    }

    /// <summary>"#2f6fd6", "2F6FD6" → "#2F6FD6"; anything else → null.</summary>
    public static string? NormalizeHex(string? text)
    {
        var t = text?.Trim().TrimStart('#') ?? "";
        return Regex.IsMatch(t, "^[0-9a-fA-F]{6}$") ? "#" + t.ToUpperInvariant() : null;
    }

    public static bool TryParseColor(string? hex, out Color color)
    {
        var h = NormalizeHex(hex);
        color = h != null ? ColorTranslator.FromHtml(h) : Color.Empty;
        return h != null;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Tags used by the tasks, those of the most recently worked on tasks first. Spellings that
    /// differ only by case or "#" count as one tag, written the most common way.
    /// </summary>
    public List<string> KnownTags() =>
        // GroupBy keeps the order of first appearance.
        TasksByRecentUse().SelectMany(t => SplitTags(t.Tags))
            .GroupBy(TagKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.GroupBy(t => t).OrderByDescending(s => s.Count()).First().Key)
            .ToList();

    /// <summary>"#clientA" and "clientA" are the same tag (case is ignored too).</summary>
    public static string TagKey(string tag) => tag.Trim().TrimStart('#');

    /// <summary>"a,  b ,," → ["a", "b"]</summary>
    public static IEnumerable<string> SplitTags(string text) =>
        text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);

    /// <summary>"a,  b ,," → "a, b"</summary>
    public static string NormalizeTags(string text) => string.Join(", ", SplitTags(text));
}
