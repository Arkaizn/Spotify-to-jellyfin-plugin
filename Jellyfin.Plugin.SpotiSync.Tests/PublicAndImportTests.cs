using System.Text.Json;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

public class PublicPlaylistParsingTests
{
    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc123", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/intl-de/playlist/37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("  37i9dQZF1DXcBWIGoYBM5M ", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC", null)]
    [InlineData("hello", null)]
    [InlineData("", null)]
    public void ParsePlaylistId(string input, string? expected) => Assert.Equal(expected, SpotifyPublicClient.ParsePlaylistId(input));

    private static string Embed(string entityJson)
        => "<html><body><div id=\"__next\"></div><script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":"
           + entityJson + "}}}}}</script></body></html>";

    [Fact]
    public void ParseEmbed_ReadsNameCoverAndTracks()
    {
        var html = Embed("""
        {"type":"playlist","name":"Road Trip","coverArt":{"sources":[{"url":"https://i.scdn.co/big","width":640},{"url":"https://i.scdn.co/small","width":60}]},
         "trackList":[
          {"uri":"spotify:track:aaa","title":"Titanium (feat. Sia)","subtitle":"David Guetta, Sia","duration":245000},
          {"uri":"spotify:episode:podcast","title":"A podcast","subtitle":"Show","duration":1},
          {"uri":"spotify:local:Artist:Album:Song:200","title":"Local Song","subtitle":"Local Artist","duration":200000},
          {"uri":"spotify:track:bbb","title":"Hey Jude","artists":[{"name":"The Beatles"}],"duration":431000}
         ]}
        """);
        var p = SpotifyPublicClient.ParseEmbed(html);
        Assert.Equal("Road Trip", p.Name);
        Assert.Equal("https://i.scdn.co/small", p.Image);
        Assert.False(p.Capped);
        Assert.Equal(3, p.Tracks.Count);
        Assert.Equal("aaa", p.Tracks[0].Id);
        Assert.Equal(["David Guetta", "Sia"], p.Tracks[0].Artists!.Select(a => a.Name));
        Assert.Equal(245000, p.Tracks[0].DurationMs);
        Assert.True(p.Tracks[1].IsLocal);
        Assert.Null(p.Tracks[1].Id);
        Assert.Equal("The Beatles", p.Tracks[2].Artists![0].Name);
    }

    [Fact]
    public void ParseEmbed_FlagsTheHundredSongLimit()
    {
        var items = string.Join(",", Enumerable.Range(0, 100).Select(i => "{\"uri\":\"spotify:track:t" + i + "\",\"title\":\"Song " + i + "\",\"subtitle\":\"A\",\"duration\":1000}"));
        var p = SpotifyPublicClient.ParseEmbed(Embed("{\"name\":\"Big\",\"trackList\":[" + items + "]}"));
        Assert.Equal(100, p.Tracks.Count);
        Assert.True(p.Capped);
    }

    [Fact]
    public void ParseEmbed_RejectsPagesWithoutData()
    {
        Assert.Throws<SpotifyApiException>(() => SpotifyPublicClient.ParseEmbed("<html>nothing</html>"));
        Assert.Throws<SpotifyApiException>(() => SpotifyPublicClient.ParseEmbed(Embed("{\"name\":\"x\"}")));
    }
}

public class SpotifyExportTests
{
    [Fact]
    public void Parse_PlaylistFile()
    {
        using var doc = JsonDocument.Parse("""
        {"playlists":[
          {"name":"Gym","lastModifiedDate":"2026-01-01","items":[
            {"track":{"trackName":"Eye of the Tiger","artistName":"Survivor","albumName":"Eye of the Tiger","trackUri":"spotify:track:t1"},"episode":null,"localTrack":null,"addedDate":"2026-01-01"},
            {"track":null,"episode":{"episodeName":"Pod"},"localTrack":null},
            {"track":null,"episode":null,"localTrack":{"uri":"spotify:local:x","trackName":"My Demo","artistName":"Me","albumName":"Tapes"}}
          ]},
          {"name":"Empty","items":[]}
        ]}
        """);
        var lists = SpotifyExport.Parse(doc.RootElement);
        Assert.Equal(2, lists.Count);
        var gym = lists[0];
        Assert.Equal("Gym", gym.Name);
        Assert.Equal(2, gym.Tracks.Count);
        Assert.Equal("t1", gym.Tracks[0].Id);
        Assert.Equal("Survivor", gym.Tracks[0].Artists![0].Name);
        Assert.Equal("Eye of the Tiger", gym.Tracks[0].Album!.Name);
        Assert.True(gym.Tracks[1].IsLocal);
        Assert.Empty(lists[1].Tracks);
    }

    [Fact]
    public void Parse_LibraryFile()
    {
        using var doc = JsonDocument.Parse("""{"tracks":[{"artist":"Queen","album":"Jazz","track":"Don't Stop Me Now","uri":"spotify:track:q1"}],"albums":[]}""");
        var lists = SpotifyExport.Parse(doc.RootElement);
        Assert.Single(lists);
        Assert.Equal(SpotifyExport.LikedName, lists[0].Name);
        Assert.Equal("q1", lists[0].Tracks[0].Id);
    }

    [Fact]
    public void Parse_OtherFiles_GiveNothing()
    {
        using var doc = JsonDocument.Parse("""{"username":"x","country":"CH"}""");
        Assert.Empty(SpotifyExport.Parse(doc.RootElement));
    }
}

public class PublicPlaylistSyncTests
{
    private static async Task AddPublic(Harness h, string spotifyId)
    {
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "pub-" + spotifyId, SpotifyId = spotifyId, Source = CatalogEntry.SourcePublic, Name = "Public " + spotifyId, Readable = true, Enabled = true });
        await h.Store.SaveCatalogAsync(c);
    }

    private static PublicPlaylist Pl(string name, params Spotify.SpTrack[] tracks) => new(name, null, tracks.ToList(), false);

    [Fact]
    public async Task PublicPlaylist_SyncsWithoutAccount_AndOnlyRewritesOnChange()
    {
        using var h = new Harness();
        var a = h.Library.Add("Alpha Song", "Band One", 200);
        var b = h.Library.Add("Beta Tune", "Band Two", 210);
        h.Public.Lists["P1"] = Pl("Mine", FakeSpotify.Track("t1", "Alpha Song", "Band One", 200), FakeSpotify.Track("t2", "Beta Tune", "Band Two", 210));
        await AddPublic(h, "P1");

        var first = await h.Sync();
        Assert.Equal(1, first.Fetched);
        Assert.Equal([a, b], h.JellyfinItems("Public P1"));

        var second = await h.Sync();
        Assert.Equal(0, second.Fetched);
        Assert.Equal(0, second.Writes);
        Assert.Equal(0, second.StateWrites);
        Assert.Equal(2, h.Public.Calls);

        h.Public.Lists["P1"] = Pl("Mine", FakeSpotify.Track("t2", "Beta Tune", "Band Two", 210), FakeSpotify.Track("t1", "Alpha Song", "Band One", 200));
        await h.Sync();
        Assert.Equal([b, a], h.JellyfinItems("Public P1"));

        // Rematch-only never fetches.
        await h.Sync(SyncMode.RematchOnly);
        Assert.Equal(3, h.Public.Calls);
    }

    [Fact]
    public async Task PublicPlaylist_SurvivesAccountListRefresh_AndErrorsStayPerPlaylist()
    {
        using var h = new Harness();
        h.Library.Add("Alpha Song", "Band One", 200);
        h.Spotify.AddPlaylist("acc1", "Account List", [FakeSpotify.Track("t1", "Alpha Song", "Band One", 200)]);
        await AddPublic(h, "GONE");
        await h.Sync();
        var c = await h.Store.LoadCatalogAsync();
        var pub = c.Playlists.Single(p => p.Id == "pub-GONE");
        Assert.NotNull(pub.Error);
        Assert.Contains(c.Playlists, p => p.Id == "acc1");
    }

    [Fact]
    public async Task ImportedPlaylist_IsMatchedWithoutAnyFetch()
    {
        using var h = new Harness();
        var a = h.Library.Add("Alpha Song", "Band One", 200);
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "file-gym", Source = CatalogEntry.SourceFile, Name = "Gym", Readable = true, Enabled = true });
        await h.Store.SaveCatalogAsync(c);
        var state = new PlaylistState { Id = "file-gym", FetchedAt = DateTime.UtcNow };
        SyncEngine.MergeTracks(state, [new Spotify.SpTrack { Id = "t1", Uri = "spotify:track:t1", Name = "Alpha Song", Artists = [new Spotify.SpArtist { Name = "Band One" }] }]);
        await h.Store.SavePlaylistAsync(state);

        var o = await h.Sync(SyncMode.RematchOnly);
        Assert.Equal([a], h.JellyfinItems("Gym"));
        Assert.Equal(0, h.Public.Calls);
        Assert.Equal(0, h.Spotify.ItemCalls);
        Assert.Equal(1, o.Matched); // no duration in exports: still matched
    }
}
