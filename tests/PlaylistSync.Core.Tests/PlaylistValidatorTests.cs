using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;

namespace PlaylistSync.Core.Tests;

public sealed class PlaylistValidatorTests
{
    private static readonly string Root = Path.GetPathRoot(Path.GetTempPath())!;
    private static readonly string Home = Path.Combine(Root, "home", "me");
    private const string Url = "https://www.youtube.com/playlist?list=PL123";

    private static PlaylistEntry Existing(string name, string dir) =>
        new() { Name = name, Url = Url, Directory = dir };

    private static PlaylistValidation Validate(string? name = "Mix", string? url = Url, string? dir = null,
        IEnumerable<PlaylistEntry>? others = null, Guid? editing = null, StringComparer? comparer = null) =>
        PlaylistValidator.Validate(name, url, dir ?? Path.Combine(Home, "Music", "Mix"), others ?? [], editing, Home,
            comparer ?? StringComparer.Ordinal);

    [Fact]
    public void Valid_entry_has_no_errors_and_a_normalized_path()
    {
        var v = Validate(dir: Path.Combine(Home, "Music", "Mix") + Path.DirectorySeparatorChar);
        Assert.True(v.IsValid);
        Assert.Equal(Path.Combine(Home, "Music", "Mix"), v.NormalizedDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string? name) => Assert.NotNull(Validate(name: name).NameError);

    [Fact]
    public void Overlong_name_is_rejected() => Assert.NotNull(Validate(name: new string('x', 201)).NameError);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://open.spotify.com/playlist/abc")]
    [InlineData("ftp://www.youtube.com/playlist?list=PL1")]
    public void Url_must_be_youtube(string? url) => Assert.NotNull(Validate(url: url).UrlError);

    [Theory]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_x")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PL1")]
    public void Youtube_music_and_watch_urls_are_accepted(string url) => Assert.Null(Validate(url: url).UrlError);

    [Theory]
    [InlineData("")]
    [InlineData("relative/folder")]
    public void Directory_must_be_full_path(string dir) => Assert.NotNull(Validate(dir: dir).DirectoryError);

    [Fact]
    public void Tilde_expands_to_home()
    {
        var v = Validate(dir: "~/Music/Mix");
        Assert.Null(v.DirectoryError);
        Assert.Equal(Path.Combine(Home, "Music", "Mix"), v.NormalizedDirectory);
    }

    [Fact]
    public void Root_and_home_are_rejected()
    {
        Assert.NotNull(Validate(dir: Root).DirectoryError);
        Assert.NotNull(Validate(dir: Home).DirectoryError);
        Assert.NotNull(Validate(dir: "~").DirectoryError);
    }

    [Fact]
    public void Same_folder_as_another_playlist_is_rejected()
    {
        var other = Existing("Other", Path.Combine(Home, "Music", "Mix"));
        var v = Validate(others: [other]);
        Assert.Contains("already used by 'Other'", v.DirectoryError);
    }

    [Fact]
    public void Nested_folders_are_rejected_both_ways()
    {
        var parent = Existing("Parent", Path.Combine(Home, "Music"));
        Assert.Contains("overlaps", Validate(others: [parent]).DirectoryError);

        var child = Existing("Child", Path.Combine(Home, "Music", "Mix", "Sub"));
        Assert.Contains("overlaps", Validate(others: [child]).DirectoryError);
    }

    [Fact]
    public void Sibling_with_common_prefix_is_fine()
    {
        var other = Existing("Other", Path.Combine(Home, "Music", "Mix2"));
        Assert.Null(Validate(others: [other]).DirectoryError);
    }

    [Fact]
    public void Editing_entry_does_not_conflict_with_itself()
    {
        var self = Existing("Mix", Path.Combine(Home, "Music", "Mix"));
        Assert.Null(Validate(others: [self], editing: self.Id).DirectoryError);
    }

    [Fact]
    public void Case_rules_follow_the_comparer()
    {
        var other = Existing("Other", Path.Combine(Home, "music", "mix"));
        Assert.Null(Validate(others: [other], comparer: StringComparer.Ordinal).DirectoryError);
        Assert.NotNull(Validate(others: [other], comparer: StringComparer.OrdinalIgnoreCase).DirectoryError);
    }

    [Fact]
    public void Overlaps_handles_root_parent()
    {
        Assert.True(PathUtil.Overlaps(Path.Combine(Root, "a"), Root, StringComparer.Ordinal));
        Assert.False(PathUtil.Overlaps(Path.Combine(Root, "a"), Path.Combine(Root, "ab"), StringComparer.Ordinal));
    }

    [Fact]
    public void Dot_segments_are_resolved()
    {
        var v = Validate(dir: Path.Combine(Home, "Music", "x", "..", "Mix"));
        Assert.Equal(Path.Combine(Home, "Music", "Mix"), v.NormalizedDirectory);
    }
}

public sealed class SafeFolderNameTests
{
    [Theory]
    [InlineData("Chill Mix", "Chill Mix")]
    [InlineData("  AC/DC: Best of?  ", "AC_DC_ Best of_")]
    [InlineData("Ends with dots...", "Ends with dots")]
    [InlineData("con", "con_")]
    [InlineData("COM1.mix", "COM1_.mix")]
    [InlineData("Computer", "Computer")]
    [InlineData("///", "Playlist")]
    [InlineData("", "Playlist")]
    [InlineData(null, "Playlist")]
    public void Makes_portable_names(string? input, string expected) => Assert.Equal(expected, PathUtil.SafeFolderName(input));

    [Fact]
    public void Long_names_are_truncated() => Assert.Equal(100, PathUtil.SafeFolderName(new string('a', 300)).Length);
}
