using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Tools;

/// <param name="Location">Null when the tool was not found anywhere.</param>
/// <param name="Version">Parsed version, or null if the probe failed.</param>
/// <param name="Error">Why the tool is unusable (not found, configured path missing, probe failed).</param>
public sealed record ToolStatus(ToolKind Kind, ToolLocation? Location, string? Version, string? Error)
{
    public bool IsUsable => Location is { Exists: true } && Error is null;
}

/// <summary>Locates each tool and probes its version.</summary>
public sealed partial class ToolChecker(ToolLocator locator, IProcessRunner runner, ILogger<ToolChecker> log)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    public static readonly IReadOnlyList<ToolKind> AllKinds =
        [ToolKind.YtDlp, ToolKind.Ffmpeg, ToolKind.Ffprobe, ToolKind.JsRuntime];

    public async Task<IReadOnlyList<ToolStatus>> CheckAllAsync(ToolSettings settings, CancellationToken ct = default) =>
        await Task.WhenAll(AllKinds.Select(k => CheckAsync(k, settings, ct))).ConfigureAwait(false);

    public async Task<ToolStatus> CheckAsync(ToolKind kind, ToolSettings settings, CancellationToken ct = default)
    {
        var location = locator.Locate(kind, settings);
        if (location is null)
            return new ToolStatus(kind, null, null, $"{DisplayName(kind)} was not found.");
        if (!location.Exists)
            return new ToolStatus(kind, location, null, $"The configured path '{location.Path}' does not exist or is not executable.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var result = await runner.RunAsync(
                new ProcessRequest(location.Path, [VersionArgument(kind)]), timeout.Token).ConfigureAwait(false);

            var output = result.StandardOutput.Length > 0 ? result.StandardOutput : result.StandardError;
            var version = ParseVersion(kind, output);
            if (!result.Succeeded || version is null)
            {
                var detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? "no output";
                log.LogWarning("{Tool} at {Path} failed the version probe (exit {Exit}): {Detail}",
                    kind, location.Path, result.ExitCode, detail);
                return new ToolStatus(kind, location, version,
                    $"{DisplayName(kind)} did not run correctly (exit code {result.ExitCode}): {detail}");
            }

            log.LogInformation("{Tool}: {Path} ({Source}), version {Version}", kind, location.Path, location.Source, version);
            return new ToolStatus(kind, location, version, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolStatus(kind, location, null,
                $"{DisplayName(kind)} did not answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
        catch (ProcessStartException ex)
        {
            log.LogWarning(ex, "Could not start {Tool} at {Path}", kind, location.Path);
            return new ToolStatus(kind, location, null, ex.Message);
        }
    }

    public static string DisplayName(ToolKind kind) => kind switch
    {
        ToolKind.YtDlp => "yt-dlp",
        ToolKind.Ffmpeg => "ffmpeg",
        ToolKind.Ffprobe => "ffprobe",
        ToolKind.JsRuntime => "JavaScript runtime (deno)",
        _ => kind.ToString(),
    };

    internal static string VersionArgument(ToolKind kind) =>
        kind is ToolKind.Ffmpeg or ToolKind.Ffprobe ? "-version" : "--version";

    /// <summary>
    /// Extracts the version from the probe output:
    /// <c>2026.10.26</c> (yt-dlp), <c>ffmpeg version n9.0.2 Copyright…</c>,
    /// <c>deno 2.7.13 (stable…)</c>, <c>v22.23.4</c> (node), <c>1.2.3</c> (bun).
    /// </summary>
    internal static string? ParseVersion(ToolKind kind, string output)
    {
        var line = FirstLine(output);
        if (line is null) return null;

        if (kind is ToolKind.Ffmpeg or ToolKind.Ffprobe)
        {
            // Release builds say "7.1.1" or "n7.1.1"; git builds say "N-118000-gabcdef-20250101".
            var m = FfmpegVersionRegex().Match(line);
            return m.Success ? m.Groups[1].Value : null;
        }

        var v = DottedVersionRegex().Match(line);
        return v.Success ? v.Groups[1].Value : null;
    }

    private static string? FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }

    [GeneratedRegex(@"\bversion\s+(?:n(?=\d))?(\S+)")]
    private static partial Regex FfmpegVersionRegex();

    [GeneratedRegex(@"(?<![\w.])v?(\d+(?:\.\d+)+)")]
    private static partial Regex DottedVersionRegex();
}
