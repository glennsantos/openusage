# OpenUsage for Windows (preview)

A Windows tray app that shows the same live usage limits as the macOS app, for **Claude**, **Codex** and **Cursor**.

It is a separate .NET app, not a port of the Swift UI. The provider logic follows the Swift providers: the same credential files, the same API calls and the same metric mapping.

## What it does

- Adds an icon to the notification area (system tray). Left-click it to open the usage panel above the taskbar. Right-click it for Open, Refresh and Quit.
- Hovering the icon shows the pinned metrics, for example `Claude 42% · 18%`.
- Refreshes every 5 minutes, and whenever you click **Refresh**.
- Shows only the providers you're signed in to. If you aren't signed in to any of them, all three appear with sign-in instructions.

| Provider | Where the login comes from | Shown by default |
|---|---|---|
| Claude | `%USERPROFILE%\.claude\.credentials.json` (or `CLAUDE_CONFIG_DIR`), written by Claude Code | Session, Weekly, Fable, Extra usage, Rate Limit Resets |
| Codex | `auth.json` in `CODEX_HOME`, `%USERPROFILE%\.config\codex` or `%USERPROFILE%\.codex`, written by the Codex CLI | Session, Weekly, Spark, Spark Weekly, Credits, Rate Limit Resets |
| Cursor | `%APPDATA%\Cursor\User\globalStorage\state.vscdb`, written by the Cursor editor | Total usage, Cursor Models, Other Models, On-demand, Grok Bot |

When a login token is about to expire, the app refreshes it and writes the new token back to the same file, just as the macOS app does.

## Not in this preview

- Spend tiles and the usage trend chart. These need the local log scanning and the pricing engine.
- Settings: reordering, hiding metrics, launch at login.
- Providers other than Claude, Codex and Cursor.
- Auto-update, installer and code signing.
- PostHog error reporting. Errors go to the log file only.

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app can be built on Windows, macOS or Linux, but it only runs on Windows 10 or 11.

```sh
cd windows
dotnet test tests/OpenUsage.Core.Tests          # provider + formatting tests (any OS)
dotnet publish src/OpenUsage.Windows -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o dist/win-x64
```

This produces `dist/win-x64/OpenUsage.exe`, a single file that runs without installing .NET. Use `-r win-arm64` for ARM devices.

## Layout

- `src/OpenUsage.Core/`: the platform-neutral library. It holds the models (`MetricLine`, `ProviderSnapshot`), the providers (an auth store, a mapper and a runtime per provider) and the display formatting.
- `src/OpenUsage.Windows/`: the WPF tray app (tray icon, flyout panel, refresh timer).
- `tests/OpenUsage.Core.Tests/`: xUnit tests for the mappers, the auth stores and the refresh/retry paths.

## Logs

Logs are written to `%LOCALAPPDATA%\OpenUsage\logs\openusage.log`. To open them, click **Open Log** in the panel.
