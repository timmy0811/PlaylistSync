using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class ReplacementMatcherTests
{
    private static readonly ReplacementQuery PishPosh = new("NDQvKlcNXqY", "Pish Posh (feat. Of the Trees)", "Mr. Bill", 246);

    private static ReplacementCandidate Candidate(string? track = "Pish Posh", double? duration = 246, string? availability = "public",
        params string[] artists) =>
        new("6bbOVO8kppI", "Pish Posh", track, artists.Length > 0 ? artists : ["Mr. Bill", "Of The Trees"], duration, availability);

    [Fact]
    public void Re_release_of_the_same_song_matches()
    {
        // Real data: the playlist's video was pulled, YouTube Music plays 6bbOVO8kppI instead.
        Assert.Null(ReplacementMatcher.Matches(PishPosh, Candidate()));
    }

    [Theory]
    [InlineData(249.0, true)]
    [InlineData(243.0, true)]
    [InlineData(250.0, false)]
    [InlineData(200.0, false)]
    public void Length_must_be_within_three_seconds(double duration, bool matches)
    {
        Assert.Equal(matches, ReplacementMatcher.Matches(PishPosh, Candidate(duration: duration)) is null);
    }

    [Fact]
    public void Candidate_without_length_is_rejected()
    {
        Assert.NotNull(ReplacementMatcher.Matches(PishPosh, Candidate(duration: null)));
    }

    [Theory]
    [InlineData("private")]
    [InlineData("premium_only")]
    [InlineData("needs_auth")]
    public void Restricted_candidates_are_rejected(string availability)
    {
        Assert.NotNull(ReplacementMatcher.Matches(PishPosh, Candidate(availability: availability)));
    }

    [Fact]
    public void Other_artist_is_rejected()
    {
        Assert.NotNull(ReplacementMatcher.Matches(PishPosh, Candidate(artists: ["Someone Else"])));
    }

    [Fact]
    public void Different_version_of_the_title_is_rejected()
    {
        Assert.NotNull(ReplacementMatcher.Matches(PishPosh, Candidate(track: "Pish Posh (Live)") with { Title = "Pish Posh (Live)" }));
    }

    [Fact]
    public void The_original_video_itself_is_never_a_replacement()
    {
        Assert.NotNull(ReplacementMatcher.Matches(PishPosh, Candidate() with { Id = PishPosh.OriginalId }));
    }

    [Theory]
    [InlineData("Pish Posh (feat. Of the Trees)", "Pish Posh")]
    [InlineData("Pish Posh [ft. Of the Trees]", "Pish Posh")]
    [InlineData("Pish Posh feat. Of the Trees", "pish posh")]
    [InlineData("Mr. Bill - Pish Posh (Official Audio)", "Pish Posh")]
    [InlineData("Pish Posh (HQ)", "PISH POSH")]
    [InlineData("Café del Mar", "Cafe Del Mar")]
    public void Titles_match_despite_credits_and_noise(string original, string candidate)
    {
        Assert.True(ReplacementMatcher.TitleMatches(original, "Mr. Bill", candidate));
    }

    [Theory]
    [InlineData("Pish Posh", "Pish Posh (Live)")]
    [InlineData("Pish Posh", "Pish Posh (VIP Remix)")]
    [InlineData("Pish Posh", "Oh Pish Posh")]
    [InlineData("Pish Posh", "")]
    [InlineData("[DEEMO II] Blue and Amor", "Blue and Amor")]
    public void Different_titles_do_not_match(string original, string candidate)
    {
        Assert.False(ReplacementMatcher.TitleMatches(original, "Mr. Bill", candidate));
    }

    [Theory]
    [InlineData("THz - Topic", new[] { "THz" })]
    [InlineData("Mr. Bill", new[] { "Mr. Bill, Of The Trees" })]
    [InlineData("Mr. Bill & Of The Trees", new[] { "Mr. Bill" })]
    [InlineData("xaev", new[] { "xaev - Topic" })]
    [InlineData("Beyoncé", new[] { "Beyonce" })]
    public void Artists_match_across_topic_channels_and_credit_lists(string original, string[] candidate)
    {
        Assert.True(ReplacementMatcher.ArtistMatches(original, candidate));
    }

    [Theory]
    [InlineData("Zelda Universe", new[] { "Nintendo" })]
    [InlineData("C418", new[] { "C4" })]
    [InlineData("", new[] { "Anyone" })]
    public void Unrelated_artists_do_not_match(string original, string[] candidate)
    {
        Assert.False(ReplacementMatcher.ArtistMatches(original, candidate));
    }
}

public class ReplacementFinderTests
{
    private static readonly ToolLocation YtDlp = new(ToolKind.YtDlp, "/usr/bin/yt-dlp", "yt-dlp", ToolSource.SystemPath);
    private static readonly DownloadTools Tools = new(YtDlp, null, null);
    private static readonly ReplacementQuery Query = new("NDQvKlcNXqY", "Pish Posh (feat. Of the Trees)", "Mr. Bill", 246);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Search = """
        {"_type":"playlist","entries":[
          {"_type":"url","ie_key":"Youtube","id":"NDQvKlcNXqY","title":"Pish Posh"},
          {"_type":"url","ie_key":"Youtube","id":"live1","title":"Oh Pish Posh (Live)"},
          {"_type":"url","ie_key":"Youtube","id":"zMY5xYneIOs","title":"Pish Posh"},
          {"_type":"url","ie_key":"Youtube","id":"6bbOVO8kppI","title":"Pish Posh"}
        ]}
        """;

    private static string Probe(string id, double duration, string artist = "Mr. Bill") => $$"""
        {"id":"{{id}}","title":"Pish Posh","track":"Pish Posh","artists":["{{artist}}","Of The Trees"],
         "channel":"{{artist}}","duration":{{duration}},"availability":"public"}
        """;

    private static bool IsSearch(ProcessRequest r) => r.Arguments.Contains("--flat-playlist");
    private static string Target(ProcessRequest r) => r.Arguments[^1];

    private static YtDlpReplacementFinder Finder(FakeProcessRunner runner) => new(runner, NullLogger<YtDlpReplacementFinder>.Instance);

    [Fact]
    public async Task Probes_title_matches_in_order_and_takes_the_first_that_fits()
    {
        var runner = new FakeProcessRunner(r =>
            IsSearch(r) ? FakeProcessRunner.Ok(Search)
            : Target(r).EndsWith("zMY5xYneIOs") ? FakeProcessRunner.Ok(Probe("zMY5xYneIOs", 180)) // another song with that name
            : FakeProcessRunner.Ok(Probe("6bbOVO8kppI", 246)));

        var result = await Finder(runner).FindAsync(Query, new DownloadOptions(), Tools, Ct);

        Assert.Equal(ReplacementOutcome.Found, result.Outcome);
        Assert.Equal("6bbOVO8kppI", result.Replacement!.VideoId);
        Assert.Equal("Mr. Bill, Of The Trees", result.Replacement.Artists);
        // The search text drops nothing the user would type: artist + full title.
        Assert.Contains("q=Mr.%20Bill%20Pish%20Posh%20%28feat.%20Of%20the%20Trees%29", Target(runner.Requests[0]));
        // The original and the live version were never probed.
        var probed = runner.Requests.Skip(1).Select(Target).ToList();
        Assert.Equal(2, probed.Count);
        Assert.DoesNotContain(probed, t => t.Contains("NDQvKlcNXqY") || t.Contains("live1"));
    }

    [Fact]
    public async Task Topic_suffix_is_dropped_from_the_search_text()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok("""{"_type":"playlist","entries":[]}"""));

        await Finder(runner).FindAsync(Query with { Artist = "Mr. Bill - Topic" }, new DownloadOptions(), Tools, Ct);

        Assert.StartsWith("https://music.youtube.com/search?q=Mr.%20Bill%20Pish", Target(runner.Requests.Single()));
    }

    [Fact]
    public async Task Nothing_close_enough_is_not_found()
    {
        var runner = new FakeProcessRunner(r =>
            IsSearch(r) ? FakeProcessRunner.Ok(Search) : FakeProcessRunner.Ok(Probe("x", 180)));

        var result = await Finder(runner).FindAsync(Query, new DownloadOptions(), Tools, Ct);

        Assert.Equal(ReplacementOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Unreadable_candidates_are_skipped()
    {
        var runner = new FakeProcessRunner(r =>
            IsSearch(r) ? FakeProcessRunner.Ok(Search)
            : Target(r).EndsWith("zMY5xYneIOs") ? FakeProcessRunner.Fail("ERROR: [youtube] zMY5xYneIOs: Video unavailable")
            : FakeProcessRunner.Ok(Probe("6bbOVO8kppI", 246)));

        var result = await Finder(runner).FindAsync(Query, new DownloadOptions(), Tools, Ct);

        Assert.Equal("6bbOVO8kppI", result.Replacement?.VideoId);
    }

    [Fact]
    public async Task Failed_search_is_an_error_not_a_miss()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail("ERROR: Sign in to confirm you're not a bot"));

        var result = await Finder(runner).FindAsync(Query, new DownloadOptions(), Tools, Ct);

        Assert.Equal(ReplacementOutcome.Error, result.Outcome);
        Assert.Contains("not a bot", result.Error);
    }

    [Fact]
    public void Probe_collects_every_artist_name()
    {
        var c = YtDlpReplacementFinder.ParseProbe("""
            {"id":"v","title":"T","track":"Tr","artists":["A","B"],"creators":["A"],"artist":"A, B","channel":"A - Topic","duration":12}
            """)!;

        Assert.Equal(["A", "B", "A, B", "A - Topic"], c.Artists);
        Assert.Equal("A, B", c.Credit);
        Assert.Equal("Tr", c.Track);
        Assert.Equal(12, c.DurationSec);
        Assert.Null(YtDlpReplacementFinder.ParseProbe("not json"));
    }
}
