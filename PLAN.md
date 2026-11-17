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
| Network folders | ✅ A playlist folder can be a network address (`smb://[user@]server/share/folder`; also `sftp://`, `ftp://`, `webdav(s)://`). The library stores the **address** (mount paths change between sessions); `FolderResolver` mounts it on demand through **kio-fuse** (D-Bus `org.kde.KIOFuse`, KDE) or **gvfs** (`gio mount`/`gio info`, GNOME), with the desktop's own credential store. Windows: SMB → UNC path. Downloads for a network folder run in a local temp dir; only the finished file is copied over |
| Theme | Avalonia Fluent theme, light/dark/system, a single accent colour, compact density |
| Packaging | `dotnet publish` self-contained single-file (`linux-x64`, `win-x64`). AppImage + PKGBUILD (AUR) for Linux, zip (later an installer) for Windows |

## 3. Architecture

```
PlaylistSync.sln
├─ src/PlaylistSync.Core/          # no UI dependencies, fully unit-testable
│  ├─ Models/                      # PlaylistEntry, TrackRecord, TrackStatus, AppSettings, Manifest
│  ├─ Sources/                     # IPlaylistSource, YtDlpPlaylistSource (flat playlist fetch)
│  ├─ Tools/                       # ToolLocator, ToolInstaller (download/update yt-dlp, ffmpeg), ProcessRunner
│  ├─ Download/                    # YtDlpArgsBuilder, YtDlpTrackDownloader, DownloadOutputParser, FilePlacer
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

1. **Fetch remote**: `yt-dlp --ignore-config --flat-playlist --dump-single-json --yes-playlist [--js-runtimes …] [cookies/proxy] -- <url>`
   gives entries (`id`, `title`, `channel`/`uploader`, `duration`, availability). `--ignore-config` so a user's
   yt-dlp config can't change behaviour. Warnings go to the debug log. Single-video and channel URLs are rejected
   with a clear message; duplicates keep their first position; `[Private video]`/`[Deleted video]`/upcoming → unavailable.
   Without cookies YouTube Music lists a deleted video with nothing but its id (no title, channel or duration); such
   entries are unavailable too, so the result doesn't depend on whether cookies are set up (checked live: 325 of 1,420
   entries of a playlist, all of them `[Deleted video]` when signed in).
2. **Plan** (`SyncPlanner`, a pure function, unit-tested):
   - remote ∖ manifest → `Pending` (download)
   - manifest ∖ remote → `RemovedRemote` (keep the file, highlight)
   - in both, but status `RemovedRemote` → back to `Synced` (re-added remotely)
   - in both, `Synced`, file missing on disk → `MissingLocal`. Setting: re-download automatically (default **on**)
   - in both, `Failed` → retry
   - remote entry marked private/deleted → `Unavailable` (file and known metadata kept)
   - manifest ∖ remote, never downloaded (no file recorded) → dropped from the manifest (nothing to highlight)
   - interrupted `Downloading` from a crash → requeued
3. **Execute**: `SyncService` runs the downloads with `Parallel.ForEachAsync` at the configured concurrency (default 2). One yt-dlp
   process per track (`https://www.youtube.com/watch?v=<id>`) using the args from
   `YtDlpArgsBuilder`, `--newline --progress --progress-template` for machine-readable progress (download progress on stdout,
   post-processor steps on stderr), and `--print after_move:filepath` to learn the final filename. Each track downloads into
   `<dir>/.playlist-sync-tmp/<id>/` (hidden, skipped by the folder scan); the finished file is then moved into the folder by
   `FilePlacer`, which never overwrites (collision → ` [<id>]`, then a counter). The manifest is saved after each track (under a
   lock, in order) and the UI gets `SyncEvent`s. ffmpeg is required only when something needs downloading.
   Download errors: private/deleted/"video unavailable" → `Unavailable`; everything else → `Failed` with a user-facing
   message (cookies hint for sign-in walls, rate-limit hint, mutagen hint for covers in opus/ogg/flac).
   **Replacements** (setting, default **on**): a video can be gone although YouTube Music still plays the track — labels
   pull a release and re-upload it under a new id, and YouTube Music silently plays the new upload (e.g. `NDQvKlcNXqY`,
   available in 0 countries → `6bbOVO8kppI`, same song re-credited). When a download fails as unavailable and the manifest
   knows title, uploader and length, `YtDlpReplacementFinder` searches YouTube Music songs
   (`https://music.youtube.com/search?q=<artist title>#songs`, flat), probes up to 3 hits whose title matches
   (`--dump-single-json --skip-download`) and accepts the first with the same artist (Topic suffix and credit lists handled),
   the same title (feat. credits and "(Official Audio)"-style noise ignored, "(Live)"/"(Remix)" kept) and the same length
   (±3 s). The file is downloaded from the replacement; the record keeps the playlist's id (identity unchanged) and stores
   `replacementId`; the row is labelled "Replacement" and "Copy video URL" copies the replacement. A known replacement is
   reused without searching; a gone replacement is forgotten; a search that finds nothing is not repeated for 7 days
   (`replacementSearchedAt`); a failed search (bot check, network) is retried next sync. Live sample of 16 real unavailable
   tracks: 8 found, all correct; misses were songs not on YouTube Music and an artist who renamed (deliberately not matched).
   **Duplicates** (setting "Skip duplicate tracks", default **on**): the same video id listed twice is always one track
   (parser). The same *song* under different ids (Topic upload + official video, re-uploads) is found at plan time by
   the pure `DuplicateFinder` (same normalized title, artist and length ±3 s, reusing the replacement matching rules;
   "Artist - Song" titles and `VEVO`/`- Topic` channel names handled; unknown length never matches). Checked before
   downloading rather than shown afterwards: one hash pass over the playlist, no extra network calls, nothing downloaded
   twice. Already-downloaded files (also removed-in-remote ones) win, then remote order; downloaded tracks are never
   marked. The others get status `Duplicate` with `duplicateOf`, a "Duplicates" filter chip, and are downloaded
   once their original leaves the playlist.
   **Browser cookies** are read once per sync, not per process (`BrowserCookieCache`): `--cookies-from-browser` decrypts
   the whole cookie database each run (≈13 s for 3,100 Brave cookies when pycryptodomex is missing). The sync runs
   `yt-dlp --ignore-config --cookies-from-browser <spec> --cookies <file>` without a URL (it exits 2, but saves the jar),
   keeps only youtube.com/google.com/googlevideo.com/ytimg.com/youtube-nocookie.com cookies **in memory**, and deletes the
   export. The fetch and every track get their own owner-only cookies.txt copy (yt-dlp writes the jar back on exit), deleted
   when the process ends. Folder: `$XDG_RUNTIME_DIR/playlist-sync/cookies` (tmpfs), else `<data>/cookies`. A snapshot is
   reused for 5 minutes (so "Sync all" reads the browser once) and dropped after a sign-in error. If the export fails, the
   sync falls back to `--cookies-from-browser` per process. The toolbox still passes `--cookies-from-browser` (one process,
   any site).
4. **Cancel**: a `CancellationToken` kills the process tree. Finished tracks stay saved, in-flight ones go back to `Pending`.
   Temp dirs of unfinished tracks are kept so yt-dlp resumes the `.part` file; the others (and an empty temp root) are removed.
   A hard kill leaves in-flight tracks as `Downloading`, which the planner requeues.
5. **Unmanaged files**: audio files in the directory that the manifest doesn't know about are shown in a separate "Untracked" filter. They are never touched.

Only one sync per playlist runs at a time. "Sync all" runs the playlists one after another.

## 6. Settings (global, optionally overridden per playlist)

| Group | Settings |
|---|---|
| Audio | format (`mp3`, `m4a`, `opus`, `flac`, `ogg`, `wav`, `best`/no re-encode), quality (VBR 0–10 or CBR 128/192/256/320 kbps) |
| Metadata | embed metadata, embed thumbnail as cover, crop cover to square, embed chapters, parse "Artist - Title" from the title into tags. *(No "prefer YouTube Music metadata" switch: yt-dlp 2026.10 already returns the YT Music artist/album/track for a plain youtube.com URL, checked live, so it would change nothing.)* |
| Files | output filename template (default `%(artist,uploader)s - %(title)s.%(ext)s`), restrict filenames (ASCII/Windows-safe), optional track-number prefix |
| Auth / network | cookies: none / cookies.txt file / `--cookies-from-browser` (browser + profile), proxy, rate limit, sleep between downloads, retries |
| Cover | save the playlist cover into the folder as `cover.jpg` (or `.png`) for music players (off by default; only the playlist's own cover, not YouTube's video-thumbnail fallback; never written if a cover/folder/front/album image exists) |
| Extras | SponsorBlock remove `music_offtopic` segments, re-download missing files, find replacements for unavailable tracks (default on, §5.3), concurrency, extra raw yt-dlp args (free text, advanced) |
| Tools | yt-dlp path, ffmpeg path, JS runtime (deno/node, which yt-dlp now needs for full YouTube support) status + path, "Check / Download / Update" buttons, versions shown |
| App | default folder for new playlists (base path; a new playlist is suggested at `<base>/<playlist title>`; empty = system Music folder; `~` allowed), theme (system/light/dark), log level, open log folder |

Per-playlist overrides: a playlist either follows the global settings or has its own copy of the download options
(Audio, Metadata, Files/Cover, Extras incl. extra args). The **Auth / network group is always global**
(`DownloadOptions.Effective`), so setting up cookies once covers every playlist. Concurrency, tools and app settings are global only.

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
- Toolbox (🧰 next to ⚙ in the sidebar, Ctrl+T): a non-modal window with a one-off download of a single URL (any site
  yt-dlp supports) as audio or video into any picked folder, outside the library and its manifests, plus utility shortcuts
  (open log / settings / data folder, clear toolbox temp files).
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
- **Slow browser cookies**: without `python-pycryptodomex` yt-dlp decrypts Chromium cookies in pure Python. Add it as an
  optional dependency in the PKGBUILD (packaging milestone).
- **Filename collisions** (two videos with the same title): append ` [<id>]` on collision. The manifest maps id → file, so lookups never depend on the name.
- **Renamed remote titles**: identity is the video id, so the file is not re-downloaded or renamed.
- **Network folders over kio-fuse**: rename (also overwrite) and fsync work, so the atomic manifest write is safe. .NET's
  cross-device `File.Move`/`File.Copy` do **not**: kio-fuse answers the clone ioctl with ENOTTY and .NET leaves an empty
  target file behind. `FilePlacer` therefore copies by hand (hidden `.part` file → no-overwrite rename) whenever the temp
  dir is not inside the target folder. A NAS that is off shows up as a folder error (sync) or a warning (add/edit dialog).
- **Windows**: path length and reserved characters → `--restrict-filenames`/`--windows-filenames` is on by default on Windows.

## 10. Resolved questions ✅

1. **Audio format**: default **mp3 VBR 0**. opus / m4a (and the other formats) can be chosen in Settings.
2. **`.m3u8` playlist file** in the remote order: setting, **off** by default.
3. **Track-number prefix** in filenames: setting, **off** by default.
4. **Private/deleted remote videos**: never `RemovedRemote`. They get the status
   **`Unavailable`** ("unavailable in remote"), highlighted with its own colour and label — unless a replacement upload
   of the same song is found (§5.3), in which case it is downloaded from there and labelled "Replacement".

## 11. Progress

- [x] M1 Scaffold: solution (.slnx, central package versions), Core/App/Tests projects, DI, file logging, Fluent theme with light/dark brushes, placeholder main window, AppPaths + tests
- [x] M2 Storage + models: `DownloadOptions`/`AppSettings`/`PlaylistEntry`/`Manifest`, atomic JSON writes, `ConfigStore` (corrupt config → moved aside, defaults), `ManifestStore` (corrupt manifest → error, never overwritten; serialized writes); 10 tests. Concurrency is a global setting (`AppSettings.Concurrency`), not per playlist
- [x] M3 Tools: `ProcessRunner`/`IProcessRunner` (argument list, closed stdin, UTF-8, line streaming, bounded stderr tail, cancel kills the process tree), `ToolLocator` (configured → managed bin dir → PATH; a missing configured path is reported, not silently replaced; ffprobe looked for next to a configured ffmpeg; JS runtime prefers deno, then node, bun), `ToolChecker` (version probe with 20 s timeout, `ToolStatus` with user-facing error); 41 new test cases (57 total)
- [x] M4 Remote fetch + planner: `IPlaylistSource` + `YtDlpPlaylistSource` (tool lookup, user-facing `PlaylistFetchException` with kinds; bot check / sign-in / WL·LL·LM → "set up cookies" hint), `FlatPlaylistParser` (fixture from real yt-dlp 2026.10.26 output), `YouTubeUrl`, `YtDlpArgsBuilder.PlaylistFetch` + shared JS-runtime/cookies/proxy args, pure `SyncPlanner` (new manifest copy, download list in remote order, change list, untracked audio files, different-playlist flag) and `DiskSnapshot` (platform case rules, `/` separators); `Manifest.PlaylistId`; smoke-tested against live YouTube (playlist, YT Music URL, video, channel, bad id, WL); 103 new test cases (160 total). Extra raw args are not applied to the fetch, only to downloads (M5)
- [x] M5 Downloader: `YtDlpArgsBuilder.TrackDownload` (every §6 download setting; artist/title parsed from the title only when YouTube
  has no artist; square cover crop; WAV never embeds a cover; track-number prefix goes before the file name, not a template folder;
  `--no-mtime`; extra args via a shell-free `CommandLineSplitter`, placed last so they can override), `DownloadOutputParser`
  (fixtures recorded from yt-dlp 2026.10.26), `YtDlpTrackDownloader` (per-track temp dir, throttled progress, error
  classification), `FilePlacer`, `SyncService` (one sync per folder, different-playlist guard, `SyncException` kinds, cancel
  handling, temp cleanup), optional `.m3u8` (`M3uWriter`, remote order, removed/missing tracks left out). Smoke-tested live:
  ~100-track sync, hard kill mid-sync + resume, token cancel (no orphan processes), no-op re-sync. 95 new test cases (255 total)
- [x] M6 Library UI: Core `PlaylistLibrary` (load/add/update/remove/reorder, applied in memory only after a successful save;
  removing never touches the folder), pure `PlaylistValidator` (YouTube URL, full path with `~` expansion, no drive root /
  home, no folder shared with or nested in another playlist's, platform case rules), `FolderInspector` (missing / empty /
  has files / same or different playlist manifest / corrupt), `ManifestSummary`, `RelativeTime`, `PathUtil.SafeFolderName`.
  App: `PlaylistLibrary`/`SettingsService`/`IDialogService` in DI; sidebar with status dot (accent = up to date, amber =
  needs attention, red = manifest unreadable, hollow = never synced) and track count; detail header (URL, folder link opens
  the file manager, summary, last sync, ⋯ menu: open folder, copy URL, move up/down, remove); add/edit dialog with a
  debounced yt-dlp check that shows title + track count and prefills name and `~/Music/<title>`, folder picker, inline
  errors, folder notes; remove confirmation (Cancel focused). Ctrl+N adds. Checked in the running app under Xvfb with a live
  playlist. 54 new test cases (309 total)
- [x] M7 Sync view: Core `TrackList` (pure: row order = remote position, then removed-in-remote, then untracked files; filter
  chips All / Removed / Failed (incl. missing files) / Unavailable / Untracked; multi-term search over title, uploader, file,
  id; delete rule; `ResolveFile` refuses paths outside the folder and our own manifest/temp files), `SyncPlanner.FindUntracked`
  public, `SyncService.DeleteFilesAsync` (only removed-in-remote / unavailable tracks; removed ones are forgotten, unavailable
  ones keep their record; refused while the folder syncs; m3u8 rewritten when enabled; permanent delete, no trash).
  App: track list per playlist (lives with the item, so a sync keeps updating it while another playlist is shown; re-read on
  selection), status icons + labels, amber removed rows, grey unavailable rows, inline errors, per-track progress (percent,
  speed, post-processing step), Sync / Cancel in the header (F5), Sync all in the sidebar (Shift+F5, sequential, skips
  running ones), status bar activity + Cancel all, last-sync time saved, "folder used by another playlist" confirmation,
  per-row Delete… button and context menu (Play, Open folder, Copy video URL, Delete local file…), bulk "Delete N files…"
  in the Removed filter (confirmation lists the files), quitting during a sync asks, cancels and waits (15 s max) so no
  yt-dlp outlives the app. Checked under Xvfb: filters, search, bulk delete, live sync of a 182-track playlist, cancel
  (in-flight tracks back to Pending, no orphan processes). The collapsible log panel from §7 is not built yet. 43 new test
  cases (352 total)
- [x] Playlist covers (added after M7): the flat fetch already returns the playlist thumbnails; `FlatPlaylistParser` reads them,
  `PlaylistCover.Pick` prefers the owner's uploaded cover (`i.ytimg.com/pl_c/<id>/…`, square) over YouTube's fallback (first
  video's 16:9 thumbnail), only https YouTube/Google image hosts; stored as `Manifest.CoverUrl` (kept when a fetch has none).
  `CoverCache` downloads it once into `<data>/covers/<playlist>-<url hash>.img` (image/* only, 5 MB cap, old file replaced,
  removed with the playlist; failures → placeholder). Shown in the sidebar (status dot on its corner) and the header.
  Verified with a YouTube Music playlist with a custom cover (unlisted, no cookies needed) and the fallback case.
  Cover URLs are stable between fetches, so the cache isn't refreshed on every sync. 19 new test cases (371 total)
- [x] M8 Settings: Core `SettingsValidator` (pure: template stays inside the folder and ends in `.%(ext)s`, balanced fields;
  quality ranges; cookies file must exist / known browser; proxy URL; rate limit; sleep/retries/concurrency ranges; tool paths
  must be full paths; extra args: unclosed quote = error, options that break how the app drives yt-dlp (`-o`, `-P`, `--print`,
  `-q`, …) = warning; network group skipped for a playlist), `DownloadOptions.Effective` (playlist override + global network
  group; used by sync, delete and the fetch check), `SaveFolderCover` + `FolderCover` (JPEG/PNG by magic bytes, temp file +
  no-overwrite move, existing cover never replaced) run by `SyncService` after a sync through `ICoverStore` (= `CoverCache`;
  best effort, never fails the sync), `LogLevelSwitch` (runtime log filter), `CommandLineSplitter` reports unclosed quotes.
  App: settings window (⚙ in the sidebar, Ctrl+,) with tabs Audio / Metadata / Files / Network / Advanced / Tools / App,
  inline errors and warnings, Save blocked with "fix the marked fields (tabs)"; Tools tab checks the typed-in paths
  (version, path, source) with Browse / Auto per tool; theme and log level apply immediately; open log / settings folder;
  "Restore default download settings" (keeps network + extra args). Per playlist: the same window in playlist mode ("use the
  global settings" / "custom", network tab hidden) from ⋯ → Download settings… and from the add/edit dialog ("Change…",
  saved with the entry); header shows "Own settings: MP3 · VBR 0". Nested dialogs open on top of the current dialog.
  Checked under Xvfb with throwaway config dirs: every tab, validation, save, live theme switch, playlist override (global
  Opus, playlist MP3) on a live 43-track YouTube Music playlist → 43 MP3s + a 720×720 `cover.jpg`. Download / update of tools
  is M9. 73 new test cases (444 total)
- [x] Network folders (smb:// etc., added after M8): `RemoteAddress` (pure: parse/normalise `scheme://[user@]host[:port]/path`,
  decoded segments, share root rejected, UNC, kio-fuse/gvfs mount path → address), `FolderResolver` (kio-fuse via `busctl
  --json`, gio fallback, cached per share, one mount at a time, 2 min timeout for the login prompt, `FolderUnavailableException`
  : IOException), `PathUtil.NormalizeUserPath` keeps addresses (and turns `file://` URIs and mount paths picked in a file dialog
  into paths/addresses), validator hints, `SyncService`/item loads/editor folder check resolve first, network downloads use
  `<data>/downloads/<hash>` as temp root, "Open folder" passes the `smb://` address to the file manager. Checked against a real
  SMB share through kio-fuse: resolve, existing manifest read, file placing with name collision, cover, atomic
  manifest write. 32 new test cases (490 total)
- [x] Play / Shuffle (added after network folders): header buttons. **Play** opens the first downloaded track of the
  playlist (playlist order, removed-in-remote and untracked files skipped) with the default player, same as a row's Play.
  **Shuffle** writes the downloaded tracks in random order to `<data>/queues/<playlist>-shuffled.m3u` (extended M3U,
  absolute paths, titles + durations) and opens it with the default `.m3u` handler. Players have no common way to switch
  their own shuffle mode on from outside, so the app shuffles the queue itself. Core `M3uPlaylist` (build + Fisher–Yates
  shuffle). 3 new test cases (493 total)
- [x] Player setting (Settings → App → Playback): "System default (per file type)", the installed audio players, or
  "Other program…" (full path; the file is its only argument). Stored as `AppSettings.Playback` (`DesktopId` /
  `ExecutablePath`). Used by Play, Shuffle and a row's Play, so the track and the `.m3u` queue open in the same player.
  Core `DesktopEntry` (parse `[Desktop Entry]`, Exec quoting, field codes %f/%F → path, %u/%U → file:// URL, %c %i %k %%),
  `PlayerCatalog` (XDG application folders, user copy hides system one, audio MIME types only; empty on Windows),
  `PlayerLauncher` (argument list, no shell, detached). Installed players are listed off the UI thread. 15 new test
  cases (508 total)
- [x] A row's Play queues what is shown: the clicked track plus the rows displayed after it (current filter chip and
  search, display order, rows with a file and not `MissingLocal`) go to a new `<data>/queues/queue-<time>.m3u`, which opens in
  the player. Only the clicked file's existence is checked, so the queue is instant even for big or network folders
  (players skip entries whose file has gone missing). A single-track queue opens the file itself. Every queue (Shuffle
  too) gets a fresh file name; queue files older than a minute are deleted before a new one is written. Queue entries
  are written as `<dir>/./<file>`: Haruna (1.9, `PlaylistModel::addM3uItems`) starts an opened .m3u at the entry whose
  path equals its `LastPlayedFile` (compared as written, stored cleaned), so plain paths resumed at the last played track.
- [x] Header Play / Shuffle respect the search: while the search box holds text they use only the rows currently shown
  (filter chip + search, rows with a file and not `MissingLocal`/`Downloading`) instead of all downloaded tracks
  (`TrackListViewModel.PlaybackQueue`). With an empty search they behave as before.
- [x] Toolbox (added after M8): Core `SingleDownloader` + `YtDlpArgsBuilder.SingleDownload` (audio: chosen format, quality /
  tags / cover / filename template from the global options; video: `bv*+ba/b`, `-S res:<limit>,ext:mp4:m4a`, merged into
  mp4, else mkv; intermediates in `<data>/toolbox-tmp` via `-P temp:`, so only finished files land in the folder; network
  folders resolved like playlist folders; cookies / proxy / rate limit from the global settings). Last folder, kind, format
  and resolution remembered in `AppSettings.Toolbox`. App: singleton `ToolboxViewModel` (a download keeps running when the
  window is closed), `ToolboxWindow`. 6 new test cases (514 total)
- [x] Browser cookies read once per sync (`BrowserCookieCache`, see §5.3): a new `SyncStage.ReadingCookies` ("Reading
  browser cookies…"), YouTube/Google-only filtered snapshot in memory, a private cookies.txt per yt-dlp process, 5-minute
  reuse, invalidated on sign-in errors, fallback to `--cookies-from-browser`. No-URL export checked against yt-dlp
  2026.10.26. 17 new test cases (531 total)
- [x] Track thumbnails (added after browser cookies): every track download also keeps the video thumbnail
  (`--write-thumbnail -o thumbnail:thumbnail.%(ext)s` into the per-track temp dir; with "embed cover" on, yt-dlp keeps the
  converted/cropped file instead of deleting it). After the file is in place, `TrackThumbnails` runs ffmpeg once (centre
  square crop, 96×96 JPEG, ≈3.5 KB) into `<data>/thumbnails/<video id>.jpg`: app data dir, not the music folder (players
  and network folders get no extra files), keyed by the playlist's video id (also for replacements), shared between
  playlists. Best effort with its own 20 s timeout, never fails or undoes a finished track, not tied to the sync's cancel
  token. Shown 34 px left of the title in the track list (placeholder ♪ otherwise), loaded lazily per realized row and
  reloaded when a row's download finishes. Checked live with yt-dlp 2026.10.26 (embed on → kept jpg, embed off → kept
  webp; both → 96×96 JPEG) and in the running app under Xvfb. 6 new test cases (605 total)
- [x] Thumbnail backfill (tracks downloaded before thumbnails existed, no re-download): when a row of a downloaded track
  without a thumbnail is shown, `TrackThumbnails.CreateMissingAsync` makes one in the background: first from the cover
  embedded in the audio file (ffmpeg `-map 0:v:0`, reads only the tag), else YouTube's 320×180 `i.ytimg.com/vi/<id>/
  mqdefault.jpg` (16:9 without letterbox bars; the replacement's id if the file came from one; 2 MB cap, image/* only;
  404 for gone videos). At most 2 at a time; a track for which nothing works is not retried until restart; a network
  folder that can't be resolved is not tried again (no repeated mount prompts; YouTube image only). Ids are checked
  against `[A-Za-z0-9_-]` before becoming file names or URLs (manifests are user files). Rows being downloaded are
  skipped. Checked live (embedded cover, file without cover → YouTube, 404 for a missing video) and under Xvfb with a
  folder of thumbnail-less tracks. 8 new test cases (613 total)
