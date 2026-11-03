using System.Runtime.Versioning;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class ToolLocatorTests
{
    private static readonly string Bin = Path.Combine("/data", "bin");

    private static ToolLocator Linux(string path, params string[] executables)
    {
        var set = new HashSet<string>(executables, StringComparer.Ordinal);
        return new ToolLocator(Bin, isWindows: false, k => k == "PATH" ? path : null, set.Contains);
    }

    [Fact]
    public void Finds_tool_on_path_in_order()
    {
        var loc = Linux("/usr/local/bin:/usr/bin", "/usr/bin/yt-dlp", "/usr/local/bin/yt-dlp")
            .Locate(ToolKind.YtDlp, new ToolSettings());

        Assert.NotNull(loc);
        Assert.Equal("/usr/local/bin/yt-dlp", loc.Path);
        Assert.Equal(ToolSource.SystemPath, loc.Source);
        Assert.True(loc.Exists);
    }

    [Fact]
    public void Managed_bin_dir_wins_over_path()
    {
        var managed = Path.Combine(Bin, "yt-dlp");
        var loc = Linux("/usr/bin", "/usr/bin/yt-dlp", managed).Locate(ToolKind.YtDlp, new ToolSettings());

        Assert.Equal(managed, loc?.Path);
        Assert.Equal(ToolSource.Managed, loc?.Source);
    }

    [Fact]
    public void Configured_path_wins_over_everything()
    {
        var managed = Path.Combine(Bin, "ffmpeg");
        var loc = Linux("/usr/bin", "/usr/bin/ffmpeg", managed, "/opt/ff/ffmpeg")
            .Locate(ToolKind.Ffmpeg, new ToolSettings { FfmpegPath = "/opt/ff/ffmpeg" });

        Assert.Equal("/opt/ff/ffmpeg", loc?.Path);
        Assert.Equal(ToolSource.Configured, loc?.Source);
    }

    [Fact]
    public void Missing_configured_path_is_reported_not_silently_replaced()
    {
        var loc = Linux("/usr/bin", "/usr/bin/yt-dlp")
            .Locate(ToolKind.YtDlp, new ToolSettings { YtDlpPath = "/nope/yt-dlp" });

        Assert.NotNull(loc);
        Assert.Equal(ToolSource.Configured, loc.Source);
        Assert.False(loc.Exists);
    }

    [Fact]
    public void Blank_configured_path_means_auto_detect()
    {
        var loc = Linux("/usr/bin", "/usr/bin/yt-dlp").Locate(ToolKind.YtDlp, new ToolSettings { YtDlpPath = "  " });
        Assert.Equal(ToolSource.SystemPath, loc?.Source);
    }

    [Fact]
    public void Not_found_returns_null()
    {
        Assert.Null(Linux("/usr/bin").Locate(ToolKind.YtDlp, new ToolSettings()));
        Assert.Null(Linux("").Locate(ToolKind.YtDlp, new ToolSettings()));
    }

    [Fact]
    public void Relative_path_entries_are_ignored()
    {
        Assert.Null(Linux("bin:.", "bin/yt-dlp", "./yt-dlp").Locate(ToolKind.YtDlp, new ToolSettings()));
    }

    [Fact]
    public void Ffprobe_is_taken_from_next_to_configured_ffmpeg()
    {
        var loc = Linux("/usr/bin", "/usr/bin/ffprobe", "/opt/ff/ffmpeg", "/opt/ff/ffprobe")
            .Locate(ToolKind.Ffprobe, new ToolSettings { FfmpegPath = "/opt/ff/ffmpeg" });

        Assert.Equal("/opt/ff/ffprobe", loc?.Path);
        Assert.Equal(ToolSource.Configured, loc?.Source);
    }

    [Fact]
    public void Ffprobe_falls_back_to_search_when_not_next_to_ffmpeg()
    {
        var loc = Linux("/usr/bin", "/usr/bin/ffprobe", "/opt/ff/ffmpeg")
            .Locate(ToolKind.Ffprobe, new ToolSettings { FfmpegPath = "/opt/ff/ffmpeg" });

        Assert.Equal("/usr/bin/ffprobe", loc?.Path);
        Assert.Equal(ToolSource.SystemPath, loc?.Source);
    }

    [Fact]
    public void Js_runtime_prefers_deno_and_reports_its_name()
    {
        var both = Linux("/usr/bin:/home/u/.deno/bin", "/usr/bin/node", "/home/u/.deno/bin/deno")
            .Locate(ToolKind.JsRuntime, new ToolSettings());
        Assert.Equal("deno", both?.Name);
        Assert.Equal("/home/u/.deno/bin/deno", both?.Path);

        var nodeOnly = Linux("/usr/bin", "/usr/bin/node").Locate(ToolKind.JsRuntime, new ToolSettings());
        Assert.Equal("node", nodeOnly?.Name);
    }

    [Fact]
    public void Configured_js_runtime_name_comes_from_file_name()
    {
        var loc = Linux("", "/opt/node/bin/node").Locate(ToolKind.JsRuntime,
            new ToolSettings { JsRuntimePath = "/opt/node/bin/node" });
        Assert.Equal("node", loc?.Name);
    }

    [Fact]
    public void Windows_looks_for_exe_with_semicolon_path()
    {
        var winBin = @"C:\data\bin";
        var dir = @"C:\tools";
        var exe = Path.Combine(dir, "yt-dlp.exe");
        var set = new HashSet<string> { exe, Path.Combine(dir, "yt-dlp") };
        var locator = new ToolLocator(winBin, isWindows: true,
            k => k == "PATH" ? $"\"{dir}\";C:\\other" : null, set.Contains);

        var loc = locator.Locate(ToolKind.YtDlp, new ToolSettings());
        // Path.IsPathRooted("C:\\tools") is false on Linux, so only assert on Windows.
        if (OperatingSystem.IsWindows())
            Assert.Equal(exe, loc?.Path);

        var managed = Path.Combine(winBin, "ffmpeg.exe");
        set.Add(managed);
        Assert.Equal(managed, locator.Locate(ToolKind.Ffmpeg, new ToolSettings())?.Path);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Real_locator_requires_execute_bit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions only");
        using var tmp = new TempDir();
        var file = tmp.File("yt-dlp");
        File.WriteAllText(file, "#!/bin/sh\n");
        var locator = new ToolLocator(new AppPaths(tmp.Path, tmp.Path));

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.False(locator.Locate(ToolKind.YtDlp, new ToolSettings { YtDlpPath = file })!.Exists);

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.True(locator.Locate(ToolKind.YtDlp, new ToolSettings { YtDlpPath = file })!.Exists);
    }
}
