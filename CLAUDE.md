# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

**Playlist Sync**: a native desktop app that keeps local folders in sync with remote
playlists (YouTube / YouTube Music in v1; Spotify planned) by downloading tracks as audio
files with **yt-dlp** + **ffmpeg**. Syncing is manual (Sync button). Tracks removed remotely
are **highlighted, never auto-deleted**.

- Requirements: `requirements.txt` (original user requirements)
- Implementation plan, decisions, milestones and open questions: `PLAN.md`. Keep it updated when decisions change or milestones are completed.

## Tech stack

- C# / **.NET 10**, **Avalonia UI 12** (Fluent theme), MVVM via `CommunityToolkit.Mvvm`
- `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Logging`
- `System.Text.Json` for all persistence (no database)
- xUnit for tests
- External tools invoked as processes: `yt-dlp`, `ffmpeg`, optional JS runtime (`deno`) used by yt-dlp

Targets: Linux (Arch is primary) and Windows (secondary). Never a web/browser app.

## Layout

```
src/PlaylistSync.Core/   # models, storage, yt-dlp integration, sync logic: NO UI references
src/PlaylistSync.App/    # Avalonia views/viewmodels only
tests/PlaylistSync.Core.Tests/
packaging/               # AppImage, PKGBUILD, .desktop, Windows scripts
```

## Commands

```bash
dotnet build                                   # build everything
dotnet test                                    # run unit tests
dotnet run --project src/PlaylistSync.App      # start the app
dotnet publish src/PlaylistSync.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/PlaylistSync.App -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
```

Dev machine prerequisite (Arch): `sudo pacman -S dotnet-sdk yt-dlp ffmpeg`.

Tests use xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), so there is no VSTest / `Microsoft.NET.Test.Sdk`.

**Claude Code sandbox note:** inside the command sandbox, MSBuild worker nodes and the `dotnet test` ↔ testhost
handshake can't use local IPC (the build hangs, and test runs fail with "Handshake failures"). In the sandbox use
`dotnet build --disable-build-servers -nodeReuse:false`, and run tests directly via
`dotnet tests/PlaylistSync.Core.Tests/bin/Debug/net10.0/PlaylistSync.Core.Tests.dll` (or run `dotnet test` unsandboxed).
Outside the sandbox the normal commands work.

## Rules / conventions

- **Never delete user audio files automatically.** Deletion only happens through an explicit user action in the UI.
- Track identity = YouTube video id (never title or filename). The per-directory manifest
  `<dir>/.playlist-sync.json` is the source of truth for track state. Write it atomically (tmp + rename).
- Keep `SyncPlanner` a pure function (remote list + manifest + disk state → plan) and cover it with tests.
- All yt-dlp argument construction goes through `YtDlpArgsBuilder`. Pass arguments as an
  argument list (`ProcessStartInfo.ArgumentList`), never by concatenating a shell string.
- Every long-running operation is async and takes a `CancellationToken`. Cancelling kills the whole process tree.
- No blocking calls on the UI thread. Marshal progress via `Dispatcher.UIThread`/`IProgress<T>`.
- Paths: use XDG dirs on Linux and `%APPDATA%`/`%LOCALAPPDATA%` on Windows. Never hard-code `/` or `\`.
- UI: minimalistic. Fluent theme, one accent colour, light/dark/system. Removed-in-remote rows are amber-tinted; unavailable-in-remote (private/deleted video) is a separate status with its own tint, never treated as removed.
- Nullable reference types enabled. Treat warnings as errors in Core.
- Do not add Spotify support until it is scheduled in `PLAN.md` (it needs the Web API + YouTube Music matching, since yt-dlp can't download from Spotify).
