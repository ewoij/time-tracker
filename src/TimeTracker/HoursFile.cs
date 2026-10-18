using System.Globalization;
using ClosedXML.Excel;

namespace TimeTracker;

/// <summary>
/// hours.xlsx, the single source of truth. While locked, the app keeps the file
/// open with write access and lets other programs only read it, so it is the only writer.
/// Writes only append rows to Events, or create / edit rows of Tasks. The Summary sheet is
/// a derived view, regenerated on every write; it is never read.
/// </summary>
sealed class HoursFile : IDisposable
{
    const string TasksSheet = "Tasks", EventsSheet = "Events", SummarySheet = "Summary";
    const string DateFormat = "dd.mm.yyyy hh:mm", DayFormat = "dd.mm.yyyy";
    static readonly string[] TextDateFormats =
        ["dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss"];

    readonly int _backupCount;
    FileStream? _stream;

    public HoursFile(string path, int backupCount)
    {
        FilePath = Path.GetFullPath(path);
        _backupCount = backupCount;
    }

    public string FilePath { get; }
    public bool IsLocked => _stream != null;

    /// <summary>Opens the file (creating it if missing) and keeps it locked. False if another program has it open.</summary>
    public bool TryLock()
    {
        if (_stream != null) return true;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        try
        {
            if (File.Exists(FilePath))
            {
                _stream = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
            else
            {
                _stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                using var wb = new XLWorkbook();
                EnsureSheets(wb);
                WriteSummary(wb, TrackerData.Empty, DateTime.Now);
                Save(wb);
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Unlock()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>
    /// Whether another program seems to have the file open: it is locked, or an Excel / LibreOffice
    /// lock file sits next to it. Only meaningful while the app itself does not hold the lock.
    /// </summary>
    public bool IsOpenElsewhere()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        var name = Path.GetFileName(FilePath);
        string[] lockFiles = ["~$" + name, "~$" + name[Math.Min(2, name.Length)..], ".~lock." + name + "#"];
        if (lockFiles.Any(f => File.Exists(Path.Combine(dir, f)))) return true;
        if (!File.Exists(FilePath)) return false;
        try
        {
            using var probe = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Reads everything. Without the lock (file open elsewhere), reads it in shared mode.</summary>
    public TrackerData Load()
    {
        using var wb = OpenWorkbook();
        return Parse(wb);
    }

    public TrackerData AppendEvent(DateTime time, string type, string taskId = "") =>
        Modify(wb => AppendEventRow(Sheet(wb, EventsSheet), time, type, taskId));

    /// <summary>Several events in one write (a single backup and save).</summary>
    public TrackerData AppendEvents(params (DateTime Time, string Type, string TaskId)[] events) => Modify(wb =>
    {
        var ws = Sheet(wb, EventsSheet);
        foreach (var e in events) AppendEventRow(ws, e.Time, e.Type, e.TaskId);
    });

    /// <summary>Adds the task with the next free ID (T1, T2…) and starts it, in one write.</summary>
    public TrackerData CreateTaskAndStart(string name, string tags, string color, DateTime time) => Modify(wb =>
    {
        var ws = Sheet(wb, TasksSheet);
        int last = LastRow(ws);
        int max = 0;
        for (int r = 2; r <= last; r++)
        {
            var id = Text(ws.Cell(r, 1));
            if (id.Length > 1 && char.ToUpperInvariant(id[0]) == 'T' && int.TryParse(id[1..], out var n)) max = Math.Max(max, n);
        }
        var newId = "T" + (max + 1);
        WriteTask(ws, Math.Max(2, last + 1), new TaskInfo(newId, name, tags, color));
        AppendEventRow(Sheet(wb, EventsSheet), time, "Start", newId);
    });

    public TrackerData UpdateTask(TaskInfo task) => Modify(wb =>
    {
        var ws = Sheet(wb, TasksSheet);
        for (int r = 2; r <= LastRow(ws); r++)
        {
            if (Text(ws.Cell(r, 1)).Equals(task.Id, StringComparison.OrdinalIgnoreCase))
            {
                WriteTask(ws, r, task);
                return;
            }
        }
        throw new InvalidOperationException($"Task {task.Id} is no longer in the Tasks sheet.");
    });

    /// <summary>Only regenerates the Summary sheet, e.g. after the file was edited by hand.</summary>
    public TrackerData RefreshSummary() => Modify(_ => { });

    public void Dispose() => Unlock();

    TrackerData Modify(Action<XLWorkbook> change)
    {
        if (_stream == null) throw new InvalidOperationException("The app does not hold the lock on the file.");
        Backup();
        using var wb = OpenWorkbook();
        change(wb);
        var data = Parse(wb);
        WriteSummary(wb, data, DateTime.Now);
        Save(wb);
        return data;
    }

    XLWorkbook OpenWorkbook()
    {
        var ms = new MemoryStream();
        if (_stream != null)
        {
            _stream.Position = 0;
            _stream.CopyTo(ms);
        }
        else
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.CopyTo(ms);
        }
        ms.Position = 0;
        return new XLWorkbook(ms);
    }

    void Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        _stream!.SetLength(0);
        ms.Position = 0;
        ms.CopyTo(_stream);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>Before writing: one dated copy per day in backups\, keeping the newest ones.</summary>
    void Backup()
    {
        var dir = Path.Combine(Path.GetDirectoryName(FilePath)!, "backups");
        var stem = Path.GetFileNameWithoutExtension(FilePath);
        var target = Path.Combine(dir, $"{stem}_{DateTime.Now:yyyy-MM-dd}.xlsx");
        if (File.Exists(target)) return;

        Directory.CreateDirectory(dir);
        _stream!.Position = 0;
        using (var fs = new FileStream(target, FileMode.CreateNew))
            _stream.CopyTo(fs);

        foreach (var old in Directory.GetFiles(dir, stem + "_????-??-??.xlsx").OrderDescending().Skip(_backupCount))
            File.Delete(old);
    }

    static TrackerData Parse(XLWorkbook wb)
    {
        var tasks = new List<TaskInfo>();
        if (wb.TryGetWorksheet(TasksSheet, out var ts))
        {
            for (int r = 2; r <= LastRow(ts); r++)
            {
                var id = Text(ts.Cell(r, 1));
                if (id.Length == 0) continue;
                tasks.Add(new TaskInfo(id, Text(ts.Cell(r, 2)), Text(ts.Cell(r, 3)), Text(ts.Cell(r, 4))));
            }
        }

        var events = new List<TimeEvent>();
        int skipped = 0;
        if (wb.TryGetWorksheet(EventsSheet, out var es))
        {
            for (int r = 2; r <= LastRow(es); r++)
            {
                var type = Text(es.Cell(r, 2));
                var id = Text(es.Cell(r, 3));
                if (es.Cell(r, 1).Value.IsBlank && type.Length == 0 && id.Length == 0) continue;

                bool start = type.Equals("Start", StringComparison.OrdinalIgnoreCase);
                bool pause = type.Equals("Pause", StringComparison.OrdinalIgnoreCase);
                if (!TryGetTime(es.Cell(r, 1), out var time) || !(start || pause) || (start && id.Length == 0))
                {
                    skipped++;
                    continue;
                }
                events.Add(new TimeEvent(time, start, start ? id : ""));
            }
        }
        return new TrackerData(tasks, events, skipped);
    }

    static bool TryGetTime(IXLCell cell, out DateTime time)
    {
        var v = cell.Value;
        time = default;
        if (v.IsDateTime) time = v.GetDateTime();
        else if (v.IsNumber && v.GetNumber() is var n && n > 0 && n < 2958466) time = DateTime.FromOADate(n);
        else if (!(v.IsText && DateTime.TryParseExact(v.GetText().Trim(), TextDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time)))
            return false;
        // Excel stores dates as floating point numbers: round to the second.
        time = new DateTime((time.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        return true;
    }

    /// <summary>
    /// Replaces the Summary sheet (always the last one): hours per day and task, as an Excel table
    /// whose totals row only adds up the rows left visible by the filters.
    /// </summary>
    static void WriteSummary(XLWorkbook wb, TrackerData data, DateTime now)
    {
        EnsureSheets(wb);
        if (wb.TryGetWorksheet(SummarySheet, out var old)) old.Delete();
        var ws = AddSheet(wb, SummarySheet, ["Date", "Task ID", "Task", "Tags", "Hours"], [13, 11, 40, 30, 10])!;

        var rows = data.DailyTotals(now)
            .Select(t => (t.Day, t.TaskId, Name: data.NameOf(t.TaskId), Tags: data.Find(t.TaskId)?.Tags ?? "", Hours: t.Time.TotalHours))
            .OrderBy(t => t.Day).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        // Headers only without data: some programs reject an empty table.
        if (rows.Count == 0) return;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = ws.Row(i + 2);
            row.Cell(1).Value = rows[i].Day;
            row.Cell(2).Value = rows[i].TaskId;
            row.Cell(3).Value = rows[i].Name;
            row.Cell(4).Value = rows[i].Tags;
            row.Cell(5).Value = rows[i].Hours;
        }
        var table = ws.Range(1, 1, rows.Count + 1, 5).CreateTable("SummaryTable");
        table.ShowTotalsRow = true;
        table.Field(0).TotalsRowLabel = "Total";
        // SUBTOTAL(109, …): the sum of the rows left visible by the filters. Spelled out, because
        // XLTotalsRowFunction.Sum writes "[Hours]" without the table name, which LibreOffice cannot resolve.
        table.Field(4).TotalsRowFormulaA1 = "SUBTOTAL(109,SummaryTable[Hours])";
        ws.Column(1).Style.NumberFormat.Format = DayFormat;
        ws.Column(5).Style.NumberFormat.Format = "0.00";
    }

    static void AppendEventRow(IXLWorksheet ws, DateTime time, string type, string taskId)
    {
        int r = Math.Max(2, LastRow(ws) + 1);
        ws.Cell(r, 1).Value = time;
        ws.Cell(r, 1).Style.NumberFormat.Format = DateFormat;
        ws.Cell(r, 2).Value = type;
        ws.Cell(r, 3).Value = taskId;
    }

    static void WriteTask(IXLWorksheet ws, int row, TaskInfo t)
    {
        ws.Cell(row, 1).Value = t.Id;
        ws.Cell(row, 2).Value = t.Name;
        ws.Cell(row, 3).Value = t.Tags;
        var color = ws.Cell(row, 4);
        color.Value = t.Color;
        // Show the color itself in the cell, for convenience when editing in Excel.
        color.Style.Fill.BackgroundColor = XLColor.FromHtml(t.Color);
        color.Style.Font.FontColor = XLColor.White;
    }

    static IXLWorksheet Sheet(XLWorkbook wb, string name)
    {
        EnsureSheets(wb);
        return wb.Worksheet(name);
    }

    static void EnsureSheets(XLWorkbook wb)
    {
        AddSheet(wb, TasksSheet, ["ID", "Name", "Tags", "Color"], [8, 40, 30, 12]);
        var events = AddSheet(wb, EventsSheet, ["Timestamp", "Type", "Task ID"], [18, 10, 10]);
        events?.Column(1).Style.NumberFormat.SetFormat(DateFormat);
    }

    /// <summary>Creates the sheet with its headers if it does not exist; returns it only when created.</summary>
    static IXLWorksheet? AddSheet(XLWorkbook wb, string name, string[] headers, double[] widths)
    {
        if (wb.TryGetWorksheet(name, out _)) return null;
        var ws = wb.Worksheets.Add(name);
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Column(i + 1).Width = widths[i];
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        return ws;
    }

    static int LastRow(IXLWorksheet ws) => ws.LastRowUsed()?.RowNumber() ?? 1;

    static string Text(IXLCell cell) => cell.Value.IsBlank ? "" : cell.GetFormattedString().Trim();
}
