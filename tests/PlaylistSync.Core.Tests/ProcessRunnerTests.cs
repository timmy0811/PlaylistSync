using System.Diagnostics;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

/// <summary>Runs real processes through <c>/bin/sh</c>, so these only run on Unix.</summary>
public class ProcessRunnerTests
{
    private static readonly ProcessRunner Runner = new();

    private static ProcessRequest Sh(string script) => new("/bin/sh", ["-c", script]);

    private static void UnixOnly() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/sh");

    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        UnixOnly();
        var result = await Runner.RunAsync(Sh("echo out; echo err >&2; exit 3"), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("out\n", result.StandardOutput);
        Assert.Equal("err\n", result.StandardError);
    }

    [Fact]
    public async Task Streams_lines_as_they_arrive()
    {
        UnixOnly();
        var lines = new List<string>();
        var request = Sh("for i in 1 2 3; do echo line$i; done") with
        {
            OnStdoutLine = lines.Add,
            CaptureStdout = false,
        };

        var result = await Runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(["line1", "line2", "line3"], lines);
        Assert.Equal("", result.StandardOutput);
    }

    [Fact]
    public async Task Arguments_are_not_interpreted_by_a_shell()
    {
        UnixOnly();
        var tricky = "a b; echo pwned $HOME 'q' \"dq\"";
        var result = await Runner.RunAsync(new ProcessRequest("/bin/sh", ["-c", "printf '%s' \"$1\"", "sh", tricky]),
            TestContext.Current.CancellationToken);

        Assert.Equal(tricky, result.StandardOutput.TrimEnd('\n'));
    }

    [Fact]
    public async Task Passes_environment_and_utf8()
    {
        UnixOnly();
        var request = Sh("printf '%s\\n' \"$PS_TEST\"") with
        {
            Environment = new Dictionary<string, string?> { ["PS_TEST"] = "Björk – Jóga 🎵" },
        };
        var result = await Runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Björk – Jóga 🎵\n", result.StandardOutput);
    }

    [Fact]
    public async Task Stdin_is_closed_so_reads_do_not_hang()
    {
        UnixOnly();
        var result = await Runner.RunAsync(Sh("read x; echo done"), TestContext.Current.CancellationToken);
        Assert.Equal("done\n", result.StandardOutput);
    }

    [Fact]
    public async Task Stderr_capture_is_bounded()
    {
        UnixOnly();
        var result = await Runner.RunAsync(Sh("i=0; while [ $i -lt 3000 ]; do echo 0123456789012345678901234567890123456789 >&2; i=$((i+1)); done; echo last >&2"),
            TestContext.Current.CancellationToken);

        Assert.True(result.StandardError.Length <= ProcessRunner.MaxCapturedStderrChars);
        Assert.EndsWith("last\n", result.StandardError);
    }

    [Fact]
    public async Task Missing_executable_throws_start_exception()
    {
        await Assert.ThrowsAsync<ProcessStartException>(() =>
            Runner.RunAsync(new ProcessRequest(Path.Combine(Path.GetTempPath(), "definitely-not-here-42"), []),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throwing_line_callback_does_not_deadlock_and_is_rethrown()
    {
        UnixOnly();
        // Enough output to fill the pipe buffer if we stopped reading after the first line.
        var request = Sh("i=0; while [ $i -lt 20000 ]; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; i=$((i+1)); done") with
        {
            OnStdoutLine = _ => throw new InvalidOperationException("boom"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner.RunAsync(request, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Cancellation_kills_the_whole_process_tree()
    {
        UnixOnly();
        var childPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var request = Sh("sleep 60 & echo $!; wait") with
        {
            OnStdoutLine = line => childPid.TrySetResult(int.Parse(line)),
        };

        var run = Runner.RunAsync(request, cts.Token);
        var pid = await childPid.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var started = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "cancellation should be prompt");
        Assert.True(await ExitedAsync(pid), $"grandchild sleep (pid {pid}) is still running");
    }

    [Fact]
    public async Task Already_cancelled_token_does_not_start_process()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.RunAsync(Sh("exit 0"), cts.Token));
    }

    private static async Task<bool> ExitedAsync(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true; // no such process
            }
            await Task.Delay(100);
        }
        return false;
    }
}
