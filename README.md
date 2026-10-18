# Time Tracker

A minimal personal time tracker for Windows (C# / .NET): a thin colored line at the top of the screen, a tray icon, and all data in a single local `hours.xlsx` file.

- Specification: [docs/PROMPT.md](docs/PROMPT.md)
- Interactive UI mockup: https://claude.ai/artifact/6VbbWGJqTxWDKTyER8p2Sy (private)

## Status: step 0 prototype

The app currently only shows the status line (one per screen) and a small tray icon. Right-click the tray icon to change the test color, move the line top/bottom, or exit.

Command-line options: `--bottom`, `--thickness N` (pixels, default 3).

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

None so far besides .NET itself (MIT).
