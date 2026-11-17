using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playback;
using PlaylistSync.Core.Settings;

namespace PlaylistSync.Core.Tests;

public class PlayerCatalogTests
{
    private const string Haruna = """
        [Desktop Entry]
        Type=Application
        Name=Haruna
        Name[de]=Haruna Medienplayer
        Exec=haruna %u
        Icon=org.kde.haruna
        MimeType=audio/mpeg;audio/x-mpegurl;video/mp4;

        [Desktop Action New]
        Name=Ignored
        Exec=ignored
        """;

    [Fact]
    public void Parse_reads_main_group_only()
    {
        var e = DesktopEntry.Parse("org.kde.haruna.desktop", "/x/org.kde.haruna.desktop", Haruna)!;
        Assert.Equal("Haruna", e.Name);
        Assert.Equal("haruna %u", e.Exec);
        Assert.True(e.HandlesAudio);
    }

    [Theory]
    [InlineData("[Desktop Entry]\nType=Application\nName=X\nExec=x\nNoDisplay=true\nMimeType=audio/mpeg;")]
    [InlineData("[Desktop Entry]\nType=Application\nName=X\nExec=x\nHidden=true\nMimeType=audio/mpeg;")]
    [InlineData("[Desktop Entry]\nType=Link\nName=X\nURL=https://example.com\n")]
    [InlineData("[Desktop Entry]\nType=Application\nName=X\n")]
    public void Parse_skips_hidden_and_non_applications(string text) =>
        Assert.Null(DesktopEntry.Parse("x.desktop", "/x.desktop", text));

    [Fact]
    public void BuildCommand_expands_url_code_to_file_uri()
    {
        var e = DesktopEntry.Parse("h.desktop", "/h.desktop", Haruna)!;
        var (program, args) = e.BuildCommand("/music/a b.opus")!.Value;
        Assert.Equal("haruna", program);
        Assert.Equal(["file:///music/a%20b.opus"], args);
    }

    [Theory]
    [InlineData("vlc --started-from-file %U", new[] { "--started-from-file", "/m/a.m3u" }, "vlc", true)]
    [InlineData("mpv --player-operation-mode=pseudo-gui -- %F", new[] { "--player-operation-mode=pseudo-gui", "--", "/m/a.m3u" }, "mpv", false)]
    [InlineData("/usr/bin/flatpak run --file-forwarding org.x.Y @@ %f @@", new[] { "run", "--file-forwarding", "org.x.Y", "@@", "/m/a.m3u", "@@" }, "/usr/bin/flatpak", false)]
    [InlineData("player", new[] { "/m/a.m3u" }, "player", false)]
    [InlineData("\"/opt/My Player/run\" --name=%c %%x %f", new[] { "--name=P", "%x", "/m/a.m3u" }, "/opt/My Player/run", false)]
    public void BuildCommand_handles_field_codes_and_quotes(string exec, string[] expectedArgs, string expectedProgram, bool uri)
    {
        var e = new DesktopEntry("p.desktop", "/p.desktop", "P", exec, null, ["audio/mpeg"]);
        var (program, args) = e.BuildCommand("/m/a.m3u")!.Value;
        Assert.Equal(expectedProgram, program);
        Assert.Equal(uri ? expectedArgs.Select(a => a == "/m/a.m3u" ? "file:///m/a.m3u" : a) : expectedArgs, args);
    }

    [Fact]
    public void BuildCommand_handles_escapes_inside_quotes()
    {
        Assert.Equal(["sh", "-c", "echo \"$1\"", "/m/a"], DesktopEntry.SplitExec("sh -c \"echo \\\"\\$1\\\"\" /m/a"));
        Assert.Null(DesktopEntry.SplitExec("\"unclosed"));
    }

    [Fact]
    public void FindPlayers_prefers_user_folder_and_lists_audio_apps_only()
    {
        using var tmp = new TempDir();
        var user = Path.Combine(tmp.Path, "user");
        var system = Path.Combine(tmp.Path, "system");
        Directory.CreateDirectory(Path.Combine(system, "kde"));
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(system, "haruna.desktop"), Haruna);
        File.WriteAllText(Path.Combine(system, "kde", "edit.desktop"),
            "[Desktop Entry]\nType=Application\nName=Editor\nExec=edit %f\nMimeType=text/plain;");
        File.WriteAllText(Path.Combine(system, "kde", "amp.desktop"),
            "[Desktop Entry]\nType=Application\nName=Amp\nExec=amp %f\nMimeType=audio/flac;");
        // The user's copy hides the system one.
        File.WriteAllText(Path.Combine(user, "haruna.desktop"), "[Desktop Entry]\nType=Application\nName=Haruna\nExec=haruna\nHidden=true");

        var players = PlayerCatalog.FindPlayers([user, system]);

        Assert.Equal(["kde-amp.desktop"], players.Select(p => p.Id));
        Assert.NotNull(PlayerCatalog.Find([system], "haruna.desktop"));
    }

    [Fact]
    public void ApplicationDirs_follow_xdg()
    {
        var dirs = PlayerCatalog.ApplicationDirs(
            name => name switch { "XDG_DATA_DIRS" => "/usr/share:relative:/var/lib/flatpak/exports/share", _ => null },
            "/home/u");
        Assert.Equal(
        [
            Path.Combine("/home/u", ".local", "share", "applications"),
            Path.Combine("/usr/share", "applications"),
            Path.Combine("/var/lib/flatpak/exports/share", "applications"),
        ], dirs);
    }

    [Fact]
    public void Validator_requires_full_player_path()
    {
        var bad = new AppSettings { Playback = new PlaybackSettings { ExecutablePath = "vlc" } };
        Assert.NotNull(SettingsValidator.ValidateApp(bad).Error(nameof(PlaybackSettings.ExecutablePath)));
        var ok = new AppSettings { Playback = new PlaybackSettings { ExecutablePath = Path.Combine(Path.GetTempPath(), "vlc") } };
        Assert.Null(SettingsValidator.ValidateApp(ok).Error(nameof(PlaybackSettings.ExecutablePath)));
    }
}
