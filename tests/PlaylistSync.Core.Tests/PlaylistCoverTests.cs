using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Sources;

namespace PlaylistSync.Core.Tests;

public sealed class PlaylistCoverTests
{
    private const string Custom = "https://i.ytimg.com/pl_c/PLx/studio_square_thumbnail.jpg?sqp=";
    private const string Video = "https://i.ytimg.com/vi/abc/hqdefault.jpg?sqp=";

    // Real output for a YouTube Music playlist with an uploaded cover (yt-dlp 2026.10.26, trimmed to 2 entries).
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flat-playlist-custom-cover.json"));

    [Fact]
    public void Parser_reads_playlist_thumbnails()
    {
        var p = FlatPlaylistParser.Parse(Fixture());

        Assert.Equal(3, p.Thumbnails.Count);
        Assert.All(p.Thumbnails, t => Assert.True(PlaylistCover.IsCustom(t.Url)));
        Assert.Equal([240, 480, 720], p.Thumbnails.Select(t => t.Width));
        var picked = PlaylistCover.Pick(p.Thumbnails);
        Assert.Equal(p.Thumbnails[2].Url, picked);
    }

    [Fact]
    public void Parser_without_thumbnails_gives_an_empty_list()
    {
        var p = FlatPlaylistParser.Parse("""{"_type":"playlist","id":"PL1","entries":[]}""");
        Assert.Empty(p.Thumbnails);
    }

    [Fact]
    public void Prefers_the_custom_cover_over_a_bigger_video_thumbnail()
    {
        var picked = PlaylistCover.Pick([new(Video + "1", 1280, 720), new(Custom + "1", 240, 240), new(Custom + "2", 720, 720)]);
        Assert.Equal(Custom + "2", picked);
    }

    [Fact]
    public void Falls_back_to_the_largest_video_thumbnail()
    {
        var picked = PlaylistCover.Pick([new(Video + "1", 168, 94), new(Video + "2", 336, 188), new(Video + "3", null, null)]);
        Assert.Equal(Video + "2", picked);
    }

    [Fact]
    public void Ignores_foreign_and_insecure_urls()
    {
        Assert.Null(PlaylistCover.Pick([new("https://evil.example/x.jpg", 720, 720), new("http://i.ytimg.com/vi/a/x.jpg", 720, 720)]));
        Assert.Null(PlaylistCover.Pick([]));
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/a/x.jpg", true)]
    [InlineData("https://yt3.ggpht.com/abc", true)]
    [InlineData("https://lh3.googleusercontent.com/abc", true)]
    [InlineData("https://ytimg.com.evil.example/a.jpg", false)]
    [InlineData("https://notytimg.com/a.jpg", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public void Allowed_hosts(string url, bool expected) => Assert.Equal(expected, PlaylistCover.IsAllowedHost(url));
}

public sealed class CoverCacheTests : IDisposable
{
    private const string Url = "https://i.ytimg.com/pl_c/PLx/studio_square_thumbnail.jpg?sqp=a";
    private readonly TempDir _dir = new();
    private readonly FakeHandler _handler = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private CoverCache Cache() =>
        new(new AppPaths(_dir.File("cfg"), _dir.File("data")), new HttpClient(_handler), NullLogger<CoverCache>.Instance);

    [Fact]
    public async Task Downloads_once_and_then_uses_the_cache()
    {
        var id = Guid.NewGuid();
        var cache = Cache();

        var path = await cache.GetAsync(id, Url, Ct);
        Assert.NotNull(path);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path, Ct));

        Assert.Equal(path, await cache.GetAsync(id, Url, Ct));
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task A_new_url_replaces_the_old_file()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var cache = Cache();
        var old = await cache.GetAsync(id, Url, Ct);
        var keep = await cache.GetAsync(other, Url, Ct);

        var updated = await cache.GetAsync(id, Url + "b", Ct);

        Assert.NotEqual(old, updated);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(updated));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Remove_deletes_the_playlists_cover()
    {
        var id = Guid.NewGuid();
        var cache = Cache();
        var path = await cache.GetAsync(id, Url, Ct);

        cache.Remove(id);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Failures_return_null_and_leave_nothing_behind()
    {
        var cache = Cache();
        _handler.Status = HttpStatusCode.NotFound;
        Assert.Null(await cache.GetAsync(Guid.NewGuid(), Url, Ct));

        _handler.Status = HttpStatusCode.OK;
        _handler.ContentType = "text/html";
        Assert.Null(await cache.GetAsync(Guid.NewGuid(), Url, Ct));

        _handler.ContentType = "image/jpeg";
        _handler.Throw = true;
        Assert.Null(await cache.GetAsync(Guid.NewGuid(), Url, Ct));

        Assert.False(Directory.Exists(cache.Dir) && Directory.EnumerateFileSystemEntries(cache.Dir).Any());
    }

    [Fact]
    public async Task Refuses_oversized_images()
    {
        _handler.Body = new byte[CoverCache.MaxBytes + 1];
        Assert.Null(await Cache().GetAsync(Guid.NewGuid(), Url, Ct));
    }

    [Fact]
    public async Task Never_fetches_urls_outside_youtube_image_hosts()
    {
        Assert.Null(await Cache().GetAsync(Guid.NewGuid(), "https://evil.example/a.jpg", Ct));
        Assert.Equal(0, _handler.Calls);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string ContentType = "image/jpeg";
        public byte[] Body = [1, 2, 3];
        public bool Throw;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Throw) throw new HttpRequestException("offline");
            var content = new ByteArrayContent(Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
        }
    }
}
