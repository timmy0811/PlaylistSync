using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Sources;

namespace PlaylistSync.Core.Tests;

public sealed class FolderCoverTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
    private static readonly byte[] Webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    private readonly TempDir _dir = new();
    private readonly TempDir _cache = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _dir.Dispose();
        _cache.Dispose();
    }

    private async Task<string> Cached(byte[] bytes)
    {
        var path = _cache.File("c.img");
        await File.WriteAllBytesAsync(path, bytes, Ct);
        return path;
    }

    [Fact]
    public void Detects_jpeg_and_png_only()
    {
        Assert.Equal(".jpg", FolderCover.DetectExtension(Jpeg));
        Assert.Equal(".png", FolderCover.DetectExtension(Png));
        Assert.Null(FolderCover.DetectExtension(Webp));
        Assert.Null(FolderCover.DetectExtension([0xFF]));
        Assert.Null(FolderCover.DetectExtension([]));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "cover.jpg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "cover.png")]
    public async Task Writes_cover_with_the_right_extension(byte[] bytes, string name)
    {
        Assert.Equal(FolderCoverResult.Written, await FolderCover.SaveAsync(_dir.Path, await Cached(bytes), Ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_dir.File(name), Ct));
        Assert.Single(Directory.GetFiles(_dir.Path)); // no temp file left
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("COVER.PNG")]
    [InlineData("folder.jpg")]
    [InlineData("Front.jpeg")]
    public async Task Never_replaces_an_existing_cover(string existing)
    {
        await File.WriteAllTextAsync(_dir.File(existing), "mine", Ct);
        Assert.Equal(existing, Path.GetFileName(FolderCover.FindExisting(_dir.Path)));

        Assert.Equal(FolderCoverResult.AlreadyPresent, await FolderCover.SaveAsync(_dir.Path, await Cached(Jpeg), Ct));
        Assert.Equal("mine", await File.ReadAllTextAsync(_dir.File(existing), Ct));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Other_images_do_not_count_as_a_cover()
    {
        await File.WriteAllTextAsync(_dir.File("artwork-scan.jpg"), "x", Ct);
        Assert.Null(FolderCover.FindExisting(_dir.Path));
        Assert.Equal(FolderCoverResult.Written, await FolderCover.SaveAsync(_dir.Path, await Cached(Jpeg), Ct));
    }

    [Fact]
    public async Task Unsupported_formats_are_skipped()
    {
        Assert.Equal(FolderCoverResult.UnsupportedFormat, await FolderCover.SaveAsync(_dir.Path, await Cached(Webp), Ct));
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void Missing_folder_has_no_cover() => Assert.Null(FolderCover.FindExisting(_dir.File("nope")));

    [Theory]
    [InlineData("https://i.ytimg.com/vi/abc/hqdefault.jpg", true)]
    [InlineData("https://i.ytimg.com/vi_webp/abc/maxresdefault.webp", true)]
    [InlineData("https://i.ytimg.com/pl_c/PL1/studio_square_thumbnail.jpg", false)]
    [InlineData("https://lh3.googleusercontent.com/abc=w544-h544", false)]
    [InlineData("not a url", false)]
    public void Video_thumbnails_are_recognised(string url, bool expected) =>
        Assert.Equal(expected, PlaylistCover.IsVideoThumbnail(url));
}
