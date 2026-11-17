using System.Text.Json;

namespace PlaylistSync.Core.Download;

public enum DownloadPhase
{
    /// <summary>yt-dlp is resolving the video (no bytes yet).</summary>
    Starting,
    /// <summary>Transferring the audio stream.</summary>
    Downloading,
    /// <summary>ffmpeg post-processing (converting, tagging, embedding the cover).</summary>
    Processing,
    /// <summary>The video is gone; searching YouTube Music for an equivalent upload.</summary>
    SearchingReplacement,
}

/// <param name="Percent">0–100 within the download phase; null when the size is unknown or while processing.</param>
/// <param name="Step">Processing step for display, e.g. "Converting".</param>
public sealed record DownloadProgress(
    DownloadPhase Phase,
    double? Percent = null,
    double? BytesPerSecond = null,
    double? EtaSeconds = null,
    string? Step = null);

/// <summary>
/// Parses the machine-readable lines yt-dlp prints with the templates from <see cref="YtDlpArgsBuilder.TrackDownload"/>.
/// Download progress arrives on stdout, post-processor progress on stderr; both are recognised by their marker.
/// </summary>
public static class DownloadOutputParser
{
    public const string DownloadMarker = "PSYNC-DL ";
    public const string PostProcessMarker = "PSYNC-PP ";
    public const string FileMarker = "PSYNC-FILE ";

    /// <summary>Parses a <c>PSYNC-DL {json}</c> line (the <c>%(progress)j</c> dict).</summary>
    public static DownloadProgress? ParseDownload(string line)
    {
        if (!line.StartsWith(DownloadMarker, StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line.AsMemory(DownloadMarker.Length));
            var p = doc.RootElement;
            if (p.ValueKind != JsonValueKind.Object) return null;

            var finished = String(p, "status") == "finished";
            var done = Number(p, "downloaded_bytes");
            var total = Number(p, "total_bytes") ?? Number(p, "total_bytes_estimate");
            double? percent = finished ? 100
                : done is { } d && total is > 0 ? Math.Clamp(d / total.Value * 100, 0, 100)
                : null;
            return new DownloadProgress(
                DownloadPhase.Downloading,
                percent,
                Number(p, "speed"),
                finished ? 0 : Number(p, "eta"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a <c>PSYNC-PP started ExtractAudio</c> line. Only "started" lines produce progress, and steps that
    /// are not worth showing (metadata parsing, moving files) are skipped.
    /// </summary>
    public static DownloadProgress? ParsePostProcess(string line)
    {
        if (!line.StartsWith(PostProcessMarker, StringComparison.Ordinal)) return null;
        var parts = line[PostProcessMarker.Length..].Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0] != "started") return null;
        var step = StepName(parts[1]);
        return step is null ? null : new DownloadProgress(DownloadPhase.Processing, Step: step);
    }

    /// <summary>The final file path from a <c>PSYNC-FILE /abs/path.mp3</c> line.</summary>
    public static string? ParseFile(string line) =>
        line.StartsWith(FileMarker, StringComparison.Ordinal) && line.Length > FileMarker.Length
            ? line[FileMarker.Length..]
            : null;

    private static string? StepName(string postprocessor) => postprocessor switch
    {
        "MetadataParser" or "MoveFiles" or "MoveFilesAfterDownload" => null,
        "ExtractAudio" => "Converting",
        "ThumbnailsConvertor" => "Preparing cover",
        "EmbedThumbnail" => "Embedding cover",
        "Metadata" or "FFmpegMetadata" => "Writing tags",
        "SponsorBlock" or "ModifyChapters" => "Removing non-music parts",
        _ => postprocessor,
    };

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
