# Time Tracker

A minimal personal time tracker for Windows (C# / .NET): a thin colored line at the top of the screen, a tray icon, and all data in a single local `hours.xlsx` file.

- Specification: [docs/PROMPT.md](docs/PROMPT.md)
- Interactive UI mockup: https://claude.ai/artifact/6VbbWGJqTxWDKTyER8p2Sy (private)

## Status: step 1

Done: status line, tray icon, task management window, `hours.xlsx` with file lock and edit mode, `current_task.txt`, `settings.json`.
Next (step 2): session lock (Win+L) question and startup check.

## Usage

- **Status line**: color of the current task, gray when paused. One per screen.
- **Tray icon**: same color; hover for the task name and elapsed time. Left click opens / closes the task window; right click → Exit. A yellow dot means the file is open for editing.
- **Task window**: Pause / Resume in the header; click a task to start it (most recently used first); pencil to edit name, tags and color; "+ New task" suggests a color automatically ("Other" for another suggestion). Closes when it loses focus or on Esc.
- **Edit in Excel**: the app releases the file, opens it in its default program and writes nothing until the file is closed again; then it reloads everything. If the editor does not lock the file, use "Done, reload".

## Settings

`settings.json` next to the exe, created with the defaults on first run. Paths may use environment variables. Restart the app to apply changes.

```json
{
  "DataFile": "%USERPROFILE%\\Documents\\TimeTracker\\hours.xlsx",
  "CurrentTaskFile": "%USERPROFILE%\\Documents\\TimeTracker\\current_task.txt",
  "LinePosition": "Top",
  "LineThickness": 3,
  "UnlockQuestionMinutes": 5,
  "LaunchAtStartup": false,
  "BackupCount": 30
}
```

- `LinePosition`: `Top` or `Bottom`. `LineThickness`: pixels.
- `UnlockQuestionMinutes`: used from step 2 (session lock question).
- `LaunchAtStartup`: adds / removes the app in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- `BackupCount`: number of daily backups kept.

## Data

`hours.xlsx` is the only data store, created if missing:

- **Tasks** sheet: `ID | Name | Tags | Color` (e.g. `T1 | Monthly report | #clientA, #billable | #2F6FD6`).
- **Events** sheet: `Timestamp | Type | Task ID`, with `Type` = `Start` or `Pause`. Durations are the time until the next event.

The app keeps the file open and write-locked while it runs (others can only read it), only appends rows to Events, and only changes Tasks when you create or edit a task. Rows can be added anywhere by hand: events are sorted by timestamp when read; unreadable rows are ignored (the window says how many). A task ID missing from Tasks shows as "unknown task (T3)".

Before the first write of each day, a copy is saved to `backups\hours_YYYY-MM-DD.xlsx` next to the file (the newest `BackupCount` are kept).

`current_task.txt` holds one line: the running task's name, or `pause`. For a Bash prompt in WSL:

```bash
cat /mnt/c/Users/<you>/Documents/TimeTracker/current_task.txt
```

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`).

```
dotnet run --project src/TimeTracker
```

Single self-contained exe (no .NET needed on the target PC), written to `publish/TimeTracker.exe`:

```
dotnet publish src/TimeTracker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Dependencies

All permissive licenses, suitable for professional use:

| Package | License | Role |
| --- | --- | --- |
| .NET 10 / Windows Forms | MIT | Runtime and UI |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) 0.105.1 | MIT | Read / write `hours.xlsx` |
| DocumentFormat.OpenXml (+ .Framework) 3.1.1 | MIT | Used by ClosedXML |
| ClosedXML.Parser 2.0.0 | MIT | Used by ClosedXML |
| ExcelNumberFormat 1.1.0 | MIT | Used by ClosedXML |
| RBush.Signed 4.0.0 | MIT | Used by ClosedXML |
| SixLabors.Fonts 1.0.0 | Apache-2.0 | Used by ClosedXML |
