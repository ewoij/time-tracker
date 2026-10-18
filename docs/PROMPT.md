# Project: minimal time tracker for Windows (C#)

I need to log the time I spend on each task so I can bill my hours. I want a minimalist personal tool that takes as little screen space as possible. No data may leave the machine: everything stays local on my PC. Top priority: keep it simple.

The whole app is in English: UI text, Excel file, sheet and column names, event types.

## Technical constraints

- C# / .NET, WinForms UI (or WPF if you think it is clearly better).
- Deliverable: a single self-contained .exe, nothing to install (`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`).
- Windows 10/11. No account, no network, no telemetry.
- Read/write the Excel file with a permissively licensed library suitable for professional use (e.g. ClosedXML). State the license of every dependency.
- Simple, readable code: this is a small tool, no heavy architecture.

## Development environment

- You run in a temporary Windows VM on Google Cloud, which I access from my Mac over remote desktop. It will be deleted at the end of the project: everything that matters must be in the GitHub repo (code, sample config, build instructions). Push regularly.
- Install whatever is missing (.NET SDK, Git) and tell me what you install.
- You can build and run the app yourself to test it. Visual behaviors (status line, F11 fullscreen, virtual desktops, session lock) are mine to check: each time, give me a short list of things to test.
- Excel may not be installed in the VM: to test edit mode, any program that opens the .xlsx will do (LibreOffice, or a small script).
- I will also validate step 0 on my real work PC.

## Step 0: prototype to validate BEFORE anything else

I often work in fullscreen (F11) in a terminal, across several virtual desktops. First build a mini-prototype that only shows the colored line described below, then stop so I can test:

- is it visible on top of a fullscreen (F11) terminal?
- is it visible on ALL virtual desktops?

Lead: a window with the WS_EX_TOOLWINDOW style seems to appear on all virtual desktops (not officially documented). If that fails, plan B: the VirtualDesktop library (Grabacr07) to pin the window, bearing in mind it relies on internal Windows APIs. If nothing works, we drop the line and keep only the tray icon.

## Interface

### 1. The status line (always visible)

- Borderless window, 3 px high (configurable), spanning the full screen width, at the top or bottom (configurable). One per screen with multiple monitors.
- Always on top, click-through (the mouse passes through it), never takes focus, absent from the taskbar and Alt+Tab.
- Color of the current task; gray when paused. No reminders, no blinking.

### 2. The notification area (tray) icon

- Small square in the same color as the line.
- On hover: task name and elapsed time.
- Click: opens / closes the task management window.

### 3. The task management window (small, opens above the icon)

- Header: current task, elapsed time, Pause / Resume button.
- Task list (color dot, name, tags): clicking a task starts it.
- On each task, a pencil button to edit its name, tags and color.
- "New task" button: name, tags (comma-separated), color → "Create and start".
- "Edit in Excel" button (see "Data").
- The window closes when it loses focus or on Esc.
- Flat structure: no projects or hierarchy, only tags.

### 4. Task colors

- On creation, a color is suggested automatically: generate candidates at fixed lightness and chroma in the OKLCH space (readable with white text), and keep the one whose minimum perceptual distance to the colors of the 5 most recently used tasks is the largest. Avoid grays (reserved for pause).
- On creation and editing, I can pick any color: the standard Windows picker (`ColorDialog`) and a field to type a hex code (#2F6FD6). No restriction on manual choice.

## Data: a single Excel file, the source of truth

All data lives in a single `hours.xlsx` file (configurable location). No other database or state file.

"Tasks" sheet:

| ID | Name | Tags | Color |
| --- | --- | --- | --- |
| T1 | Monthly report | #clientA, #billable | #2F6FD6 |

"Events" sheet (log, one row per state change):

| Timestamp | Type | Task ID |
| --- | --- | --- |
| 17.10.2026 09:00 | Start | T1 |
| 17.10.2026 10:15 | Start | T3 |
| 17.10.2026 12:00 | Pause | |

- Only two event types: "Start" (start or resume a task, which implicitly ends the previous one) and "Pause" (stop without starting anything else). No "Stop" event.
- Durations are not stored: they are derived from the time until the next event. If the app is closed, the last "Start" logically keeps counting.
- The app only writes by appending rows at the end of the events sheet, and only modifies the tasks sheet through its own actions (create, edit).
- When reading, sort events by timestamp (I may insert rows anywhere by hand). Current task = the last event.
- Events reference the task by ID: renaming a task does not break history. An unknown ID (task deleted by hand) must not crash the app: show it as "unknown task (T3)".
- Tags in a single column, comma-separated.
- On startup, if the file does not exist, create it with both sheets and their headers.

### File lock and edit mode (important)

- While the app runs, it keeps the file open and write-locked (read sharing allowed if possible, so I can view it in Excel without being able to save). It is thus the only writer: never any conflict, no queue.
- The "Edit in Excel" button: the app releases the lock, opens the file in the default application, and switches to edit mode. In this mode it writes nothing: switching task, pause, create/edit are disabled, and the window shows "Open in Excel, waiting for it to close". The line and icon keep the current task's color, with a small indicator on the icon.
- When the file is closed again (detect when the lock can be re-acquired), the app reloads everything from the Excel file, takes the lock back and returns to normal mode.
- Before every write, make a dated backup copy (at least one per day, keep the last N) in a subfolder.

## Session lock (Win+L)

- Listen to Windows session lock / unlock events and keep the lock time in memory.
- On unlock, if the lock lasted more than 5 minutes (configurable) and a task was running, show a small window: "Your PC was locked from 12:02 to 13:05 (1 h 03). It was: [Break] · [Still on "Monthly report"] · [Another task…]".
    - Break: add a Pause event at the lock time and a Start of the task at the unlock time.
    - Still on the task: add nothing.
    - Another task: pick a task from the list, add its Start at the lock time.

## Startup check

When the app starts, if the last event is a "Start" from a previous day, ask: "Last task: "Monthly report", started yesterday at 16:30. What time did you stop?", and add the corresponding Pause. This covers forgetting to pause in the evening and a shutdown during a session lock.

## For my Bash prompt (WSL)

On every state change, also write a small text file `current_task.txt` (one line: task name, or "pause"), so my Bash prompt can display it with a simple `cat`, without reading the .xlsx. It is a display output only, not data.

## Settings (simple config file next to the exe)

Path to the .xlsx, line position (top/bottom), line thickness, unlock question threshold, launch at Windows startup.

## Approach

1. Step 0 (line prototype), then stop so I can test.
2. Then the tray icon, the management window, Excel writing with the lock and edit mode.
3. Then session lock handling and the startup check.
4. Explain how to build and publish the exe.

Ask me questions if anything is unclear before coding.
