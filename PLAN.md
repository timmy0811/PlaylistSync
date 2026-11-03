# Playlist Sync — Implementation Plan

Source requirements: `requirements.txt`. Decisions confirmed with the user are marked ✅.
Open points are collected at the end (§10).

## 1. Goal

A native desktop app (Linux first, Windows second) that manages a list of
**Playlist ↔ Local directory** pairs. Pressing **Sync** downloads every track of the
remote playlist with yt-dlp as an audio file into the directory. Tracks removed
remotely are **highlighted only, never deleted automatically**. There is no auto-sync.

## 2. Decisions

| Topic | Decision |
|---|---|
| Language / UI | ✅ C# / .NET 10 (LTS) + **Avalonia UI 12**, MVVM with `CommunityToolkit.Mvvm` |
| Sources (v1) | ✅ **YouTube / YouTube Music only**. Use a `IPlaylistSource` abstraction so Spotify can be added later (Web API + YT Music matching) |
| Removed tracks | ✅ Highlight in the app only. The file stays put; a manual "Delete file" action is available per track / in bulk |
| yt-dlp / ffmpeg | ✅ Use the binaries on PATH. If missing, offer to download them into the app data dir. "Update yt-dlp" button (only for app-managed binaries; a pacman install stays pacman's job) |
| Persistence | JSON files (no DB needed): global app config + one manifest per synced directory |
| Theme | Avalonia Fluent theme, light/dark/system, a single accent colour, compact density |
| Packaging | `dotnet publish` self-contained single-file (`linux-x64`, `win-x64`). AppImage + PKGBUILD (AUR) for Linux, zip (later an installer) for Windows |

## 3. Architecture

```
PlaylistSync.sln
├─ src/PlaylistSync.Core/          # no UI dependencies, fully unit-testable
│  ├─ Models/                      # PlaylistEntry, TrackRecord, TrackStatus, AppSettings, Manifest
│  ├─ Sources/                     # IPlaylistSource, YtDlpPlaylistSource (flat playlist fetch)
│  ├─ Tools/                       # ToolLocator, ToolInstaller (download/update yt-dlp, ffmpeg), ProcessRunner
│  ├─ Download/                    # YtDlpArgsBuilder, YtDlpDownloader (progress parsing), DownloadQueue
│  ├─ Sync/                        # SyncPlanner (pure diff), SyncService (orchestration, cancellation)
│  └─ Storage/                     # ConfigStore (app dir), ManifestStore (per directory), atomic writes
├─ src/PlaylistSync.App/           # Avalonia UI
│  ├─ Views/ ViewModels/ Converters/ Assets/
│  └─ App.axaml, Program.cs
├─ tests/PlaylistSync.Core.Tests/  # xUnit: planner diff, args builder, progress parser, manifest IO
└─ packaging/                      # AppImage recipe, PKGBUILD, .desktop, icons, Windows scripts
```

Dependency direction: App → Core. Core never references Avalonia.
Services are wired with `Microsoft.Extensions.DependencyInjection`, logging goes through
`Microsoft.Extensions.Logging` to a rolling file plus the in-app log panel.

## 4. Data model & storage

**App config dir:** Linux `$XDG_CONFIG_HOME/playlist-sync/` (fallback `~/.config/...`),
Windows `%APPDATA%\PlaylistSync\`. Managed binaries go in `$XDG_DATA_HOME/playlist-sync/bin/`
or `%LOCALAPPDATA%\PlaylistSync\bin\`.

- `settings.json`: `AppSettings` (see §6).
- `library.json`: the list of tuples:
  `PlaylistEntry { Guid Id, string Name, string Url, string Directory, DateTime? LastSync, PerPlaylistOverrides? }`

**Per-directory manifest** `<dir>/.playlist-sync.json`. This is the source of truth for
track state, so a directory can be moved or re-added and still keep its history:

```jsonc
{
  "version": 1,
  "playlistUrl": "...",
  "playlistTitle": "...",
  "tracks": {
    "<videoId>": {
      "title": "...", "uploader": "...", "durationSec": 213,
      "file": "Artist - Title.mp3",          // relative to dir, null if not downloaded
      "status": "Synced",                     // see below
      "remoteIndex": 12,
      "firstSeen": "...", "lastSeenRemote": "...", "downloadedAt": "...",
      "lastError": null
    }
  }
}
```

`TrackStatus`: `Pending`, `Downloading`, `Synced`, `RemovedRemote`, `Unavailable`
(private/deleted on YouTube), `Failed`, `MissingLocal` (in the manifest but the file was deleted by the user).

Writes are atomic (write to `*.tmp`, then rename). The manifest is saved after each track
finishes, so a crash or cancel loses at most the in-flight tracks.

## 5. Sync algorithm

1. **Fetch remote**: `yt-dlp --flat-playlist -J --no-warnings <url>` gives entries
   (`id`, `title`, `uploader`/`channel`, `duration`, availability). Stream the stdout
   JSON with `System.Text.Json`.
2. **Plan** (`SyncPlanner`, a pure function, unit-tested):
   - remote ∖ manifest → `Pending` (download)
   - manifest ∖ remote → `RemovedRemote` (keep the file, highlight)
   - in both, but status `RemovedRemote` → back to `Synced` (re-added remotely)
   - in both, `Synced`, file missing on disk → `MissingLocal`. Setting: re-download automatically (default **on**)
   - in both, `Failed` → retry
   - remote entry marked private/deleted → `Unavailable`
3. **Execute**: `DownloadQueue` with configurable concurrency (default 2). One yt-dlp
   process per track (`https://www.youtube.com/watch?v=<id>`) using the args from
   `YtDlpArgsBuilder`, `--newline --progress-template` for machine-readable progress, and
   `--print after_move:filepath` to learn the final filename. Update the manifest and UI after each track.
4. **Cancel**: a `CancellationToken` kills the process tree. Partial `.part` files are left for yt-dlp to resume or clean up.
5. **Unmanaged files**: audio files in the directory that the manifest doesn't know about are shown in a separate "Untracked" filter. They are never touched.

Only one sync per playlist runs at a time. "Sync all" runs the playlists one after another.

## 6. Settings (global, optionally overridden per playlist)

| Group | Settings |
|---|---|
| Audio | format (`mp3`, `m4a`, `opus`, `flac`, `ogg`, `wav`, `best`/no re-encode), quality (VBR 0–10 or CBR 128/192/256/320 kbps) |
| Metadata | embed metadata, embed thumbnail as cover, crop cover to square, embed chapters, parse "Artist - Title" from the title into tags, prefer YouTube Music metadata |
| Files | output filename template (default `%(artist,uploader)s - %(title)s.%(ext)s`), restrict filenames (ASCII/Windows-safe), optional track-number prefix |
| Auth / network | cookies: none / cookies.txt file / `--cookies-from-browser` (browser + profile), proxy, rate limit, sleep between downloads, retries |
| Extras | SponsorBlock remove `music_offtopic` segments, re-download missing files, concurrency, extra raw yt-dlp args (free text, advanced) |
| Tools | yt-dlp path, ffmpeg path, JS runtime (deno/node, which yt-dlp now needs for full YouTube support) status + path, "Check / Download / Update" buttons, versions shown |
| App | theme (system/light/dark), log level, open log folder |

## 7. UI (minimalistic)

```
┌───────────────────────────────────────────────────────────────────┐
│ Playlist Sync                                   [Sync all] [⚙]    │
├──────────────────┬────────────────────────────────────────────────┤
│ + Add playlist   │ Chill Mix                       [⟳ Sync] [⋯]   │
│                  │ youtube.com/playlist?list=…  →  ~/Music/Chill  │
│ ● Chill Mix  42  │ 40 synced · 1 removed · 1 failed · last 2h ago │
│ ● Workout    18  │ [All] [Removed] [Failed] [Untracked]  🔍       │
│ ○ Lo-Fi       —  │ ───────────────────────────────────────────────│
│                  │ ✓ Artist – Title              3:41  file.mp3   │
│                  │ ⚠ Artist – Gone track (removed in remote) [🗑] │  ← amber row
│                  │ ↓ Artist – Title  ███████░░ 64%                │
├──────────────────┴────────────────────────────────────────────────┤
│ ▸ Log   Downloading 3/12 …                                [Cancel]│
└───────────────────────────────────────────────────────────────────┘
```

- Left: the playlist list with a status dot (synced / has changes / error) and track count.
- Right: a header with name, URL, folder (click to open in the file manager), and Sync / Cancel. Below it a track list with status icons, inline progress, filter chips and search.
- Removed-in-remote rows are tinted amber and labelled. Context menu: "Delete local file", "Open folder", "Copy URL".
- Add/Edit dialog: URL (validated by a quick yt-dlp fetch showing the title and track count), folder picker, name (prefilled from the playlist title), optional per-playlist overrides.
- Settings: a dialog with tabs matching §6.
- Collapsible log panel at the bottom. On first start, if tools are missing, a banner with "Download yt-dlp / ffmpeg".

## 8. Milestones

1. **Scaffold**: solution, projects, DI, logging, Fluent theme, empty main window, CI-able `dotnet build`/`dotnet test`.
2. **Core: storage + models**: ConfigStore, ManifestStore (atomic), tests.
3. **Core: tools**: ToolLocator (PATH + configured + managed dir), version probing, ProcessRunner with cancellation and line streaming.
4. **Core: remote fetch + planner**: YtDlpPlaylistSource, SyncPlanner with thorough unit tests.
5. **Core: downloader**: YtDlpArgsBuilder (tests for every setting), progress parser (tests on recorded output), DownloadQueue, SyncService.
6. **UI: library**: playlist list, add/edit/remove dialog, folder picker, persistence.
7. **UI: sync view**: track list, statuses, highlighting, progress, cancel, filters, delete-file action.
8. **UI: settings**: all of §6, per-playlist overrides.
9. **Tool installer**: download yt-dlp (GitHub release asset per OS), ffmpeg (yt-dlp/FFmpeg-Builds on Windows; on Linux show a package-manager hint plus a static build option), update button.
10. **Polish**: icons, empty states, error messages, keyboard shortcuts, remember window size.
11. **Packaging**: self-contained publish profiles, AppImage, PKGBUILD, `.desktop` file, Windows zip; test on Arch + a Windows VM.
12. *(Later)* Spotify source via Web API + YouTube Music matching.

## 9. Risks / notes

- **YouTube changes**: yt-dlp breaks regularly. Mitigated by the update button and clear error surfacing. Recent yt-dlp also needs a JS runtime (deno) for YouTube; detect it and warn.
- **Rate limiting / age-restricted / members-only content** needs cookies. Expose that clearly in the error text, linking to the cookies setting.
- **Filename collisions** (two videos with the same title): append ` [<id>]` on collision. The manifest maps id → file, so lookups never depend on the name.
- **Renamed remote titles**: identity is the video id, so the file is not re-downloaded or renamed.
- **Windows**: path length and reserved characters → `--restrict-filenames`/`--windows-filenames` is on by default on Windows.

## 10. Resolved questions ✅

1. **Audio format**: default **mp3 VBR 0**. opus / m4a (and the other formats) can be chosen in Settings.
2. **`.m3u8` playlist file** in the remote order: setting, **off** by default.
3. **Track-number prefix** in filenames: setting, **off** by default.
4. **Private/deleted remote videos**: never `RemovedRemote`. They get the status
   **`Unavailable`** ("unavailable in remote"), highlighted with its own colour and label.

## 11. Progress

- [x] M1 Scaffold: solution (.slnx, central package versions), Core/App/Tests projects, DI, file logging, Fluent theme with light/dark brushes, placeholder main window, AppPaths + tests
- [ ] M2 Storage + models
