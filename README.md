# Time Tracker

A minimal time tracker for Windows. It stays out of the way: a thin colored line at the top of the screen tells you which task is running, and all your hours go into a plain Excel file on your PC.

No account, no cloud, no telemetry. One `.exe`, nothing to install.

## Features

- **Always-visible status line**: a few pixels in the color of the current task (gray when paused), on every screen and every virtual desktop, even over fullscreen apps. Click it to switch tasks.
- **Tray icon**: hover for the current task and elapsed time.
- **Quick task switcher**: click a task to start it, Pause / Resume, create tasks with tags and colors.
- **Your data in Excel**: everything lives in `hours.xlsx` (tasks, start / pause events, and a daily summary per task and tag, ready to filter). Edit it by hand whenever you want; the app steps aside while it is open.
- **Forgiving**: after you lock your PC for a while, it asks whether that was a break; if you forgot to pause yesterday, it asks when you stopped.
- **Daily backups** of the data file.

## Install

Download `TimeTracker.exe` from the [Releases](https://github.com/ewoij/time-tracker/releases) page and run it. Windows 10 / 11.

Settings (line position and thickness, file paths, launch at startup…) are in `settings.json` next to the exe, reachable from the tray menu.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet run --project src/TimeTracker
```

## More

[docs/DETAILS.md](docs/DETAILS.md) covers the full behavior, settings, data format, publishing a single exe, making a release and dependencies (all MIT / Apache-2.0).

## License

[MIT](LICENSE)
