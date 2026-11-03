using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class ToolCheckerTests
{
    [Theory]
    [InlineData(ToolKind.YtDlp, "2026.10.26\n", "2026.10.26")]
    [InlineData(ToolKind.Ffmpeg, "ffmpeg version n9.0.2 Copyright (c) 2000-2026 the FFmpeg developers\nbuilt with gcc", "9.0.2")]
    [InlineData(ToolKind.Ffmpeg, "ffmpeg version 7.1.1-1ubuntu1 Copyright (c) 2000-2025", "7.1.1-1ubuntu1")]
    [InlineData(ToolKind.Ffmpeg, "ffmpeg version N-118000-gabcdef0123-20250101 Copyright", "N-118000-gabcdef0123-20250101")]
    [InlineData(ToolKind.Ffprobe, "ffprobe version n9.0.2 Copyright (c) 2007-2026", "9.0.2")]
    [InlineData(ToolKind.JsRuntime, "deno 2.7.13 (stable, release, x86_64-unknown-linux-gnu)\nv8 14.0\n", "2.7.13")]
    [InlineData(ToolKind.JsRuntime, "v22.23.4\n", "22.23.4")]
    [InlineData(ToolKind.JsRuntime, "1.2.21", "1.2.21")]
    public void Parses_versions(ToolKind kind, string output, string expected) =>
        Assert.Equal(expected, ToolChecker.ParseVersion(kind, output));

    [Theory]
    [InlineData(ToolKind.YtDlp, "")]
    [InlineData(ToolKind.YtDlp, "Traceback (most recent call last):")]
    [InlineData(ToolKind.Ffmpeg, "something else")]
    public void Unparseable_output_gives_null(ToolKind kind, string output) =>
        Assert.Null(ToolChecker.ParseVersion(kind, output));

    [Fact]
    public async Task Reports_version_of_found_tool()
    {
        var runner = new FakeRunner(_ => new ProcessResult(0, "2026.10.26\n", "", TimeSpan.Zero));
        var status = await Checker(runner, "/usr/bin/yt-dlp").CheckAsync(ToolKind.YtDlp, new ToolSettings(),
            TestContext.Current.CancellationToken);

        Assert.True(status.IsUsable);
        Assert.Equal("2026.10.26", status.Version);
        Assert.Equal(["--version"], runner.Requests.Single().Arguments);
        Assert.Equal("/usr/bin/yt-dlp", runner.Requests.Single().FileName);
    }

    [Fact]
    public async Task Ffmpeg_is_probed_with_single_dash_version()
    {
        var runner = new FakeRunner(_ => new ProcessResult(0, "ffmpeg version 7.1 Copyright", "", TimeSpan.Zero));
        var status = await Checker(runner, "/usr/bin/ffmpeg").CheckAsync(ToolKind.Ffmpeg, new ToolSettings(),
            TestContext.Current.CancellationToken);

        Assert.Equal("7.1", status.Version);
        Assert.Equal(["-version"], runner.Requests.Single().Arguments);
    }

    [Fact]
    public async Task Missing_tool_is_reported_without_running_anything()
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("must not run"));
        var status = await Checker(runner).CheckAsync(ToolKind.YtDlp, new ToolSettings(),
            TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.Null(status.Location);
        Assert.Contains("not found", status.Error);
    }

    [Fact]
    public async Task Missing_configured_path_is_an_error()
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("must not run"));
        var status = await Checker(runner).CheckAsync(ToolKind.YtDlp, new ToolSettings { YtDlpPath = "/nope" },
            TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.Contains("/nope", status.Error);
    }

    [Fact]
    public async Task Failing_probe_is_an_error_with_stderr_detail()
    {
        var runner = new FakeRunner(_ => new ProcessResult(1, "", "ImportError: no module named yt_dlp\n", TimeSpan.Zero));
        var status = await Checker(runner, "/usr/bin/yt-dlp").CheckAsync(ToolKind.YtDlp, new ToolSettings(),
            TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.Contains("ImportError", status.Error);
    }

    [Fact]
    public async Task Start_failure_is_an_error()
    {
        var runner = new FakeRunner(r => throw new ProcessStartException($"Could not start '{r.FileName}'", new Exception()));
        var status = await Checker(runner, "/usr/bin/yt-dlp").CheckAsync(ToolKind.YtDlp, new ToolSettings(),
            TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.Contains("Could not start", status.Error);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var runner = new FakeRunner(_ => throw new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Checker(runner, "/usr/bin/yt-dlp").CheckAsync(ToolKind.YtDlp, new ToolSettings(), cts.Token));
    }

    [Fact]
    public async Task Check_all_covers_every_kind()
    {
        var runner = new FakeRunner(r => new ProcessResult(0,
            r.FileName.EndsWith("ffmpeg") ? "ffmpeg version 7.1 Copyright" : "2026.10.26", "", TimeSpan.Zero));
        var all = await Checker(runner, "/usr/bin/yt-dlp", "/usr/bin/ffmpeg").CheckAllAsync(new ToolSettings(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolChecker.AllKinds, all.Select(s => s.Kind));
        Assert.True(all[0].IsUsable);
        Assert.True(all[1].IsUsable);
        Assert.False(all[2].IsUsable);
        Assert.False(all[3].IsUsable);
    }

    private static ToolChecker Checker(IProcessRunner runner, params string[] executables)
    {
        var set = new HashSet<string>(executables);
        var locator = new ToolLocator("/data/bin", isWindows: false, k => k == "PATH" ? "/usr/bin" : null, set.Contains);
        return new ToolChecker(locator, runner, NullLogger<ToolChecker>.Instance);
    }

    private sealed class FakeRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
