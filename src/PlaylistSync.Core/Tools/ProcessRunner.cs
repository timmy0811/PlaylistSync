using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace PlaylistSync.Core.Tools;

/// <summary>A process invocation. Arguments are passed as a list, never as a shell string.</summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Called for every stdout line as it arrives (on a background thread).</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>Called for every stderr line as it arrives (on a background thread).</summary>
    public Action<string>? OnStderrLine { get; init; }

    /// <summary>Collect stdout into <see cref="ProcessResult.StandardOutput"/>. Turn off for chatty processes.</summary>
    public bool CaptureStdout { get; init; } = true;
}

/// <param name="StandardError">The tail of stderr (at most <see cref="ProcessRunner.MaxCapturedStderrChars"/> chars).</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>The executable could not be started (missing, not executable, ...).</summary>
public sealed class ProcessStartException(string message, Exception inner) : Exception(message, inner);

public interface IProcessRunner
{
    /// <summary>
    /// Runs the process to completion and streams its output line by line.
    /// Cancelling kills the whole process tree and throws <see cref="OperationCanceledException"/>.
    /// A non-zero exit code is not an exception; check <see cref="ProcessResult.ExitCode"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public const int MaxCapturedStderrChars = 64 * 1024;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in request.Arguments) psi.ArgumentList.Add(arg);
        if (request.WorkingDirectory is not null) psi.WorkingDirectory = request.WorkingDirectory;
        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                if (value is null) psi.Environment.Remove(key);
                else psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ProcessStartException($"Could not start '{request.FileName}': {ex.Message}", ex);
        }

        // Nothing we run is interactive; a closed stdin makes any prompt fail fast instead of hanging.
        process.StandardInput.Close();

        var stdout = request.CaptureStdout ? new StringBuilder() : null;
        var stderr = new StringBuilder();
        var stdoutTask = PumpAsync(process.StandardOutput, request.OnStdoutLine, stdout, int.MaxValue);
        var stderrTask = PumpAsync(process.StandardError, request.OnStderrLine, stderr, MaxCapturedStderrChars);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        // WaitForExitAsync also waits for EOF on redirected streams, but our own pumps may still be finishing.
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        stopwatch.Stop();

        return new ProcessResult(process.ExitCode, stdout?.ToString() ?? "", stderr.ToString(), stopwatch.Elapsed);
    }

    private static async Task PumpAsync(StreamReader reader, Action<string>? onLine, StringBuilder? capture, int maxChars)
    {
        ExceptionDispatchInfo? callbackError = null;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (capture is not null)
            {
                capture.Append(line).Append('\n');
                if (capture.Length > maxChars) capture.Remove(0, capture.Length - maxChars);
            }

            if (onLine is null || callbackError is not null) continue;
            try
            {
                onLine(line);
            }
            catch (Exception ex)
            {
                // Keep reading: if we stopped, the pipe would fill up and the process would block forever.
                callbackError = ExceptionDispatchInfo.Capture(ex);
            }
        }
        callbackError?.Throw();
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exited between the check and the kill, or not permitted; nothing more we can do.
        }
    }

    private static async Task DrainAsync(params Task[] pumps)
    {
        try
        {
            // Bounded: a detached grandchild could keep the pipe open after the tree kill.
            await Task.WhenAll(pumps).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Output of a cancelled process is irrelevant; a throwing line callback must not mask the cancellation.
        }
    }
}
