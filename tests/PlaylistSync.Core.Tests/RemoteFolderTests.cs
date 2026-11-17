using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public sealed class RemoteAddressTests
{
    [Fact]
    public void Parses_an_smb_address_with_user_and_spaces()
    {
        var a = RemoteAddress.Parse("smb://Timm@192.168.2.201/personal_folder/Archiv/Audio/Streaming_Synced/Camellia like/");
        Assert.NotNull(a);
        Assert.Equal("smb", a.Scheme);
        Assert.Equal("Timm", a.User);
        Assert.Equal("192.168.2.201", a.Host);
        Assert.Equal(["personal_folder", "Archiv", "Audio", "Streaming_Synced", "Camellia like"], a.Segments);
        Assert.Equal("smb://Timm@192.168.2.201/personal_folder/Archiv/Audio/Streaming_Synced/Camellia like", a.ToString());
        Assert.Equal("smb://Timm@192.168.2.201/personal_folder/Archiv/Audio/Streaming_Synced/Camellia%20like", a.ToUriString());
        Assert.False(a.IsRoot);
    }

    [Fact]
    public void Decodes_percent_escapes_and_normalises_scheme_and_host()
    {
        var a = RemoteAddress.Parse("  SMB://NAS.local//Music/Mix%20%232/  ");
        Assert.Equal("smb://nas.local/Music/Mix #2", a?.ToString());
    }

    [Fact]
    public void Mount_root_is_the_share_for_smb_and_the_server_otherwise()
    {
        var smb = RemoteAddress.Parse("smb://u@nas/share/a/b")!;
        Assert.Equal("smb://u@nas/share", smb.MountRoot.ToString());
        Assert.Equal(["a", "b"], smb.SubPath);

        var sftp = RemoteAddress.Parse("sftp://u@host:2222/home/u/music")!;
        Assert.Equal("sftp://u@host:2222", sftp.MountRoot.ToString());
        Assert.Equal(["home", "u", "music"], sftp.SubPath);
    }

    [Theory]
    [InlineData("smb://nas")]                 // no share
    [InlineData("smb://nas/")]
    [InlineData("smb:///share")]              // no host
    [InlineData("http://nas/share")]          // unsupported scheme
    [InlineData("smb://nas/share/../etc")]
    [InlineData("smb://nas:99999/share")]
    [InlineData("/home/me/Music")]
    public void Rejects_invalid_addresses(string input) => Assert.Null(RemoteAddress.Parse(input));

    [Fact]
    public void Share_only_is_a_root()
    {
        Assert.True(RemoteAddress.Parse("smb://nas/share")!.IsRoot);
        Assert.True(RemoteAddress.Parse("sftp://host")!.IsRoot);
    }

    [Fact]
    public void Converts_smb_to_unc() =>
        Assert.Equal(@"\\nas\share\Music\Mix", RemoteAddress.Parse("smb://me@nas/share/Music/Mix")!.ToUnc());

    [Fact]
    public void Kio_fuse_mount_path_turns_back_into_the_address() =>
        Assert.Equal("smb://Timm@192.168.2.201/personal_folder/Archiv/Camellia like",
            RemoteAddress.FromMountPath("/run/user/1000/kio-fuse-gQLDBE/smb/Timm@192.168.2.201/personal_folder/Archiv/Camellia like")?.ToString());

    [Fact]
    public void Gvfs_mount_path_turns_back_into_the_address()
    {
        Assert.Equal("smb://timm@nas/music/Mix",
            RemoteAddress.FromMountPath("/run/user/1000/gvfs/smb-share:server=nas,share=music,user=timm/Mix")?.ToString());
        Assert.Equal("sftp://bob@host/home/bob",
            RemoteAddress.FromMountPath("/run/user/1000/gvfs/sftp:host=host,user=bob/home/bob")?.ToString());
    }

    [Theory]
    [InlineData("/home/me/Music")]
    [InlineData("/run/user/1000/doc/abc")]
    [InlineData("/run/user/1000/gvfs/mtp:host=phone/Music")]
    public void Other_paths_are_not_mount_paths(string path) => Assert.Null(RemoteAddress.FromMountPath(path));
}

public sealed class RemoteFolderValidationTests
{
    private static readonly string Home = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "home", "me");
    private const string Url = "https://www.youtube.com/playlist?list=PL123";

    private static PlaylistValidation Validate(string dir, params PlaylistEntry[] others) =>
        PlaylistValidator.Validate("Mix", Url, dir, others, null, Home, StringComparer.Ordinal);

    [Fact]
    public void Network_folder_is_valid_and_kept_as_address()
    {
        var v = Validate("smb://Timm@192.168.2.201/personal_folder/Audio/Camellia like/");
        Assert.True(v.IsValid);
        Assert.Equal(OperatingSystem.IsWindows()
            ? @"\\192.168.2.201\personal_folder\Audio\Camellia like"
            : "smb://Timm@192.168.2.201/personal_folder/Audio/Camellia like", v.NormalizedDirectory);
    }

    [Fact]
    public void Share_itself_is_rejected() => Assert.NotNull(Validate("smb://nas/share").DirectoryError);

    [Fact]
    public void Garbled_address_gets_a_hint() => Assert.Contains("smb://", Validate("smb://").DirectoryError);

    [Fact]
    public void Overlapping_network_folders_are_rejected()
    {
        var other = new PlaylistEntry { Name = "Other", Url = Url, Directory = "smb://nas/share/Music" };
        Assert.Contains("overlaps", Validate("smb://nas/share/Music/Mix", other).DirectoryError);
    }

    [Fact]
    public void Mount_path_is_stored_as_address()
    {
        if (OperatingSystem.IsWindows()) return;
        var v = Validate("/run/user/1000/kio-fuse-AbCdEf/smb/me@nas/share/Mix");
        Assert.True(v.IsValid);
        Assert.Equal("smb://me@nas/share/Mix", v.NormalizedDirectory);
    }

    [Fact]
    public void File_uri_becomes_a_path()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("/data/My Music", PathUtil.NormalizeUserPath("file:///data/My%20Music/", Home));
    }

    [Fact]
    public void Network_base_folder_is_accepted() =>
        Assert.Equal(OperatingSystem.IsWindows() ? @"\\nas\share" : "smb://nas/share",
            PathUtil.BaseDirectory("smb://nas/share/", Home, "/music"));
}

public sealed class FolderResolverTests : IDisposable
{
    private readonly TempDir _mount = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _mount.Dispose();

    private static FolderResolver Resolver(FakeProcessRunner runner) =>
        new(runner, NullLogger<FolderResolver>.Instance, "/tmp/plsync-downloads");

    private string KioReply => $$"""{"type":"s","data":["{{_mount.Path.Replace("\\", "\\\\")}}"]}""";

    [Fact]
    public async Task Local_folders_pass_through_without_running_anything()
    {
        var runner = new FakeProcessRunner(_ => throw new InvalidOperationException("no process expected"));
        var resolved = await Resolver(runner).ResolveAsync("/home/me/Music/Mix", Ct);
        Assert.Equal(new ResolvedFolder("/home/me/Music/Mix", false), resolved);
    }

    [Fact]
    public async Task Mounts_the_share_with_kio_fuse_and_appends_the_rest()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok(KioReply));
        var resolver = Resolver(runner);

        var resolved = await resolver.ResolveAsync("smb://Timm@nas/share/Audio/Camellia like", Ct);

        Assert.Equal(new ResolvedFolder(Path.Combine(_mount.Path, "Audio", "Camellia like"), true), resolved);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("busctl", request.FileName);
        Assert.Equal("smb://Timm@nas/share", request.Arguments[^1]);

        // Cached: a second folder on the same share needs no new mount.
        await resolver.ResolveAsync("smb://Timm@nas/share/Other", Ct);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Falls_back_to_gio_without_kio_fuse()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new FakeProcessRunner(r => r switch
        {
            { FileName: "busctl" } => FakeProcessRunner.Fail("Call failed: The name org.kde.KIOFuse was not provided by any .service files"),
            { FileName: "gio", Arguments: ["mount", ..] } => FakeProcessRunner.Ok(""),
            _ => FakeProcessRunner.Ok($"display name: share\nlocal path: {_mount.Path}\nuri: smb://nas/share/\n"),
        });

        var resolved = await Resolver(runner).ResolveAsync("smb://nas/share/Mix", Ct);

        Assert.Equal(Path.Combine(_mount.Path, "Mix"), resolved.Path);
        Assert.Equal(["busctl", "gio", "gio"], runner.Requests.Select(r => r.FileName));
    }

    [Fact]
    public async Task Unreachable_server_is_a_clear_error()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail("Call failed: Could not connect to host nas."));
        var ex = await Assert.ThrowsAsync<FolderUnavailableException>(() => Resolver(runner).ResolveAsync("smb://nas/share/Mix", Ct));
        Assert.Equal("Could not connect to smb://nas/share: Could not connect to host nas.", ex.Message);
    }

    [Fact]
    public async Task No_mount_helper_installed()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new FakeProcessRunner(r => throw new ProcessStartException($"{r.FileName} not found", new FileNotFoundException()));
        var ex = await Assert.ThrowsAsync<FolderUnavailableException>(() => Resolver(runner).ResolveAsync("smb://nas/share/Mix", Ct));
        Assert.Contains("kio-fuse", ex.Message);
    }

    [Fact]
    public void Temp_root_is_local_and_stable_per_address()
    {
        var resolver = Resolver(new FakeProcessRunner(_ => FakeProcessRunner.Ok("")));
        Assert.Equal(resolver.TempRootFor("smb://nas/share/Mix"), resolver.TempRootFor("smb://nas/share/Mix"));
        Assert.NotEqual(resolver.TempRootFor("smb://nas/share/Mix"), resolver.TempRootFor("smb://nas/share/Other"));
        Assert.StartsWith("/tmp/plsync-downloads", resolver.TempRootFor("smb://nas/share/Mix"));
    }

    [Fact]
    public async Task Sync_reports_an_unreachable_network_folder_as_a_folder_error()
    {
        var service = new Sync.SyncService([], new Storage.ManifestStore(),
            null!, null!, TimeProvider.System, NullLogger<Sync.SyncService>.Instance);
        var entry = new PlaylistEntry { Name = "Mix", Url = "https://www.youtube.com/playlist?list=PL1", Directory = "smb://nas/share/Mix" };
        var ex = await Assert.ThrowsAsync<Sync.SyncException>(() => service.SyncAsync(entry, new AppSettings(), ct: Ct));
        Assert.Equal(Sync.SyncErrorKind.Directory, ex.Kind);
    }
}

public sealed class FilePlacerAcrossTests
{
    [Fact]
    public void Temp_dir_outside_the_target_is_copied_without_leftovers()
    {
        using var temp = new TempDir();
        using var target = new TempDir();
        var source = temp.File("Song.mp3");
        File.WriteAllText(source, "new");
        File.WriteAllText(target.File("Song.mp3"), "user file");

        var placed = Download.FilePlacer.MoveIntoPlace(source, temp.Path, target.Path, "abc", restrictFilenames: false);

        Assert.Equal("Song [abc].mp3", placed);
        Assert.Equal("new", File.ReadAllText(target.File("Song [abc].mp3")));
        Assert.Equal("user file", File.ReadAllText(target.File("Song.mp3")));
        Assert.False(File.Exists(source));
        Assert.Equal(["Song [abc].mp3", "Song.mp3"], Directory.GetFiles(target.Path).Select(Path.GetFileName).Order());
    }
}
