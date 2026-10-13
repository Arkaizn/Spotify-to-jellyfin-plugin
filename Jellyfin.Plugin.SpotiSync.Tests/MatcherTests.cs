using Jellyfin.Plugin.SpotiSync.Matching;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

public class TitleCleanerTests
{
    [Theory]
    [InlineData("Don't Stop Me Now - Remastered 2011", "dont stop me now", TrackTags.None)]
    [InlineData("Hey Jude (Remastered 2015)", "hey jude", TrackTags.None)]
    [InlineData("Bohemian Rhapsody - Live Aid", "bohemian rhapsody", TrackTags.Live)]
    [InlineData("Wonderwall [Live at Knebworth]", "wonderwall", TrackTags.Live)]
    [InlineData("Titanium (feat. Sia)", "titanium", TrackTags.None)]
    [InlineData("Titanium feat. Sia", "titanium", TrackTags.None)]
    [InlineData("Levels - Skrillex Remix", "levels", TrackTags.Remix)]
    [InlineData("Strobe - Original Mix", "strobe", TrackTags.None)]
    [InlineData("Strobe (Radio Edit)", "strobe", TrackTags.None)]
    [InlineData("Layla - Acoustic; Live at MTV Unplugged", "layla", TrackTags.Live | TrackTags.Acoustic)]
    [InlineData("Song - Instrumental", "song", TrackTags.Instrumental)]
    [InlineData("Blinding Lights - Sped Up", "blinding lights", TrackTags.SpedUp)]
    [InlineData("(I Can't Get No) Satisfaction - Mono Version", "i cant get no satisfaction", TrackTags.None)]
    [InlineData("Part 2 - Live", "part 2", TrackTags.Live)]
    [InlineData("Lose Yourself - From \"8 Mile\" Soundtrack", "lose yourself", TrackTags.None)]
    [InlineData("Under Pressure - Remastered 2011", "under pressure", TrackTags.None)]
    [InlineData("Mr. Brightside", "mr brightside", TrackTags.None)]
    [InlineData("Paint It, Black", "paint it black", TrackTags.None)]
    public void Clean(string raw, string expectedBase, TrackTags expectedTags)
    {
        var c = TitleCleaner.Clean(raw);
        Assert.Equal(expectedBase, c.Base);
        Assert.Equal(expectedTags, c.Tags);
    }

    [Fact]
    public void Clean_KeepsTitleWithDashThatIsNotAVersion()
        => Assert.Equal("guns n roses sweet child", TitleCleaner.Clean("Guns N' Roses - Sweet Child").Base);

    [Fact]
    public void Clean_ExtractsFeaturedArtists()
        => Assert.Equal(["Sia"], TitleCleaner.Clean("Titanium (feat. Sia)").Featured);

    [Fact]
    public void Clean_ExtractsNumbers()
    {
        Assert.Equal("2", TitleCleaner.Clean("Part 2").Numbers);
        Assert.Null(TitleCleaner.Clean("Hello").Numbers);
        Assert.Equal("5", TitleCleaner.Clean("Symphony No. 05").Numbers);
    }

    [Fact]
    public void AlbumTags_DetectLiveAlbums()
    {
        Assert.Equal(TrackTags.Live, TitleCleaner.AlbumTags("Live at Wembley '86"));
        Assert.Equal(TrackTags.Live | TrackTags.Acoustic, TitleCleaner.AlbumTags("MTV Unplugged in New York"));
        Assert.Equal(TrackTags.None, TitleCleaner.AlbumTags("A Night at the Opera"));
    }
}

public class MatcherTests
{
    private static readonly Guid QueenDsmn = Guid.NewGuid();
    private static readonly Guid QueenBohemian = Guid.NewGuid();
    private static readonly Guid QueenBohemianLive = Guid.NewGuid();
    private static readonly Guid BeatlesHeyJude = Guid.NewGuid();
    private static readonly Guid DaftPunk = Guid.NewGuid();
    private static readonly Guid Part1 = Guid.NewGuid();
    private static readonly Guid Cover = Guid.NewGuid();
    private static readonly Guid WithIsrc = Guid.NewGuid();
    private static readonly Guid Beyonce = Guid.NewGuid();

    private static LibraryIndex Library() => LibraryIndexBuilder.Build(
    [
        new LibraryRecord(QueenDsmn, "Don't Stop Me Now", ["Queen"], ["Queen"], "Jazz", Ticks(209), null),
        new LibraryRecord(QueenBohemian, "Bohemian Rhapsody", ["Queen"], ["Queen"], "A Night at the Opera", Ticks(355), null),
        new LibraryRecord(QueenBohemianLive, "Bohemian Rhapsody", ["Queen"], ["Queen"], "Live at Wembley '86", Ticks(345), null),
        new LibraryRecord(BeatlesHeyJude, "Hey Jude", ["The Beatles"], ["The Beatles"], "1", Ticks(431), null),
        new LibraryRecord(DaftPunk, "One More Time", ["Daft Punk"], ["Daft Punk"], "Discovery", Ticks(320), null),
        new LibraryRecord(Part1, "Another Brick in the Wall, Pt. 1", ["Pink Floyd"], ["Pink Floyd"], "The Wall", Ticks(191), null),
        new LibraryRecord(Cover, "Hallelujah", ["Jeff Buckley"], ["Jeff Buckley"], "Grace", Ticks(414), null),
        new LibraryRecord(WithIsrc, "Totally Different Name", ["Somebody"], null, null, Ticks(100), "USUM71703861"),
        new LibraryRecord(Beyonce, "Halo", ["Beyonce"], ["Beyonce"], "I Am... Sasha Fierce", Ticks(261), null),
        new LibraryRecord(Guid.NewGuid(), "Halo", ["Depeche Mode"], ["Depeche Mode"], "Violator", Ticks(270), null),
    ], 16);

    private static long Ticks(int seconds) => seconds * TimeSpan.TicksPerSecond;

    private static (Guid? Id, MatchKind Kind, double Score) Run(string title, string[] artists, string album, int seconds, string? isrc = null)
    {
        var idx = Library();
        var r = new Matcher(idx).Match(new MatchQuery(title, artists, album, seconds * 1000, isrc));
        return (r.Index >= 0 ? idx.Ids[r.Index] : null, r.Kind, r.Score);
    }

    [Fact]
    public void RemasterSuffix_Matches()
    {
        var r = Run("Don't Stop Me Now - Remastered 2011", ["Queen"], "Jazz (2011 Remaster)", 210);
        Assert.Equal(QueenDsmn, r.Id);
        Assert.Equal(MatchKind.Matched, r.Kind);
    }

    [Fact]
    public void StudioVersion_PrefersStudioOverLive()
    {
        var r = Run("Bohemian Rhapsody - Remastered 2011", ["Queen"], "A Night At The Opera", 354);
        Assert.Equal(QueenBohemian, r.Id);
        Assert.Equal(MatchKind.Matched, r.Kind);
    }

    [Fact]
    public void LiveVersion_PrefersLiveItem()
    {
        var r = Run("Bohemian Rhapsody - Live at Wembley Stadium", ["Queen"], "Live at Wembley Stadium", 346);
        Assert.Equal(QueenBohemianLive, r.Id);
        Assert.Equal(MatchKind.Matched, r.Kind);
    }

    [Fact]
    public void ArtistWithThe_AndAccents_Match()
    {
        Assert.Equal(BeatlesHeyJude, Run("Hey Jude - Remastered 2015", ["The Beatles"], "1 (Remastered)", 431).Id);
        var halo = Run("Halo", ["Beyoncé"], "I AM...SASHA FIERCE", 261);
        Assert.Equal(Beyonce, halo.Id);
        Assert.Equal(MatchKind.Matched, halo.Kind);
    }

    [Fact]
    public void NumberMismatch_IsNotAutoMatched()
    {
        var r = Run("Another Brick in the Wall, Pt. 2", ["Pink Floyd"], "The Wall", 239);
        Assert.NotEqual(MatchKind.Matched, r.Kind);
    }

    [Fact]
    public void DifferentArtist_IsOnlyASuggestion()
    {
        var r = Run("Hallelujah", ["Leonard Cohen"], "Various Positions", 279);
        Assert.NotEqual(MatchKind.Matched, r.Kind);
    }

    [Fact]
    public void RemixVsOriginal_IsNotAutoMatched()
    {
        var r = Run("One More Time - Romanthony Remix", ["Daft Punk"], "One More Time (Remixes)", 400);
        Assert.NotEqual(MatchKind.Matched, r.Kind);
    }

    [Fact]
    public void UnknownSong_IsNotFound()
    {
        var r = Run("Smells Like Teen Spirit", ["Nirvana"], "Nevermind", 301);
        Assert.Equal(MatchKind.NotFound, r.Kind);
        Assert.Null(r.Id);
    }

    [Fact]
    public void Isrc_ShortcutsScoring()
    {
        var r = Run("Whatever", ["Nobody"], "x", 1, "usum71703861");
        Assert.Equal(WithIsrc, r.Id);
        Assert.Equal(1.0, r.Score);
    }

    [Fact]
    public void FeaturedArtistInTitle_CountsAsArtist()
    {
        var idx = LibraryIndexBuilder.Build([new LibraryRecord(Guid.NewGuid(), "Titanium (feat. Sia)", ["David Guetta"], null, "Nothing but the Beat", Ticks(245), null)], 1);
        var r = new Matcher(idx).Match(new MatchQuery("Titanium", ["David Guetta", "Sia"], "Nothing But The Beat 2.0", 245_000, null));
        Assert.Equal(MatchKind.Matched, r.Kind);
    }

    [Theory]
    [InlineData(200, 200, 1.0)]
    [InlineData(200, 204, 0.9)]
    [InlineData(200, 210, 0.7)]
    [InlineData(200, 215, 0.4)]
    [InlineData(200, 260, 0.0)]
    [InlineData(0, 200, 0.5)]
    public void DurationScore(int a, int b, double expected) => Assert.Equal(expected, Matcher.DurationScore(a, b));

    [Fact]
    public void Penalties_TagAndNumberMismatch()
    {
        Assert.Equal(0.8, Matcher.ApplyPenalties(1.0, 1, 1, TrackTags.Live, TrackTags.None, null, null), 6);
        Assert.Equal(0.6, Matcher.ApplyPenalties(1.0, 1, 1, TrackTags.Live | TrackTags.Remix | TrackTags.Demo, TrackTags.None, null, null), 6);
        Assert.Equal(0.75, Matcher.ApplyPenalties(1.0, 1, 1, TrackTags.None, TrackTags.None, "1", "2"), 6);
        Assert.Equal(0.55, Matcher.ApplyPenalties(1.0, 0.5, 1, TrackTags.None, TrackTags.None, null, null), 6);
        Assert.Equal(0.75, Matcher.ApplyPenalties(1.0, 1, 0.3, TrackTags.None, TrackTags.None, null, null), 6);
    }

    [Fact]
    public void Search_FindsByTitleArtistAndPrefix()
    {
        var idx = Library();
        var hits = idx.Search("queen bohem", 15);
        Assert.NotEmpty(hits);
        Assert.Contains(idx.Ids[hits[0].Index], new[] { QueenBohemian, QueenBohemianLive });
        Assert.Empty(idx.Search("   ", 15));
        Assert.True(idx.Search("halo", 1).Count == 1);
    }
}
