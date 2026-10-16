using Jellyfin.Plugin.SpotiSync.Matching;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

public class DuplicateTests
{
    [Theory]
    [InlineData("Hey Jude", "The Beatles", "Hey Jude - Remastered 2015", "Beatles", true)]
    [InlineData("Titanium (feat. Sia)", "David Guetta", "Titanium", "David Guetta, Sia", true)]
    [InlineData("Bohemian Rhapsody", "Queen", "Bohemian Rhapsody - Live", "Queen", false)]
    [InlineData("Hey Jude", "The Beatles", "Hey Jude", "Wilson Pickett", false)]
    public void SongKey_SameSongDifferentFiles(string t1, string a1, string t2, string a2, bool same)
        => Assert.Equal(same, SongKey.Of(t1, a1) == SongKey.Of(t2, a2));

    [Fact]
    public void SongKey_FromDisplayText()
        => Assert.Equal(SongKey.Of("Hey Jude", "The Beatles"), SongKey.FromText("Hey Jude — The Beatles, Paul McCartney · 1"));

    private static async Task Add(Harness h, string id, string name, string mode, string? target, params SpTrack[] tracks)
    {
        h.Public.Lists[id] = new PublicPlaylist(name, null, tracks.ToList(), false);
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "pub-" + id, SpotifyId = id, Source = CatalogEntry.SourcePublic, Name = name, Mode = mode, Target = target, Readable = true, Enabled = true });
        await h.Store.SaveCatalogAsync(c);
    }

    private static SpTrack T(string id, string title, string artist, string album, int sec) => FakeSpotify.Track(id, title, artist, sec, album);

    [Fact]
    public async Task Mirror_WritesSameSongOnce_AndMarksTheDuplicate()
    {
        using var h = new Harness();
        var album = h.Library.Add("Shiny Song", "The Band", 200, "Album");
        var single = h.Library.Add("Shiny Song", "The Band", 200, "Single");
        var other = h.Library.Add("Other Tune", "The Band", 180, "Album");
        await Add(h, "P", "Mix", SyncModes.Mirror, null,
            T("a", "Shiny Song", "The Band", "Album", 200),
            T("b", "Other Tune", "The Band", "Album", 180),
            T("c", "Shiny Song - Single Version", "The Band", "Single", 200));

        await h.Sync();
        Assert.Equal([album, other], h.JellyfinItems("Mix"));
        var state = await h.Store.LoadPlaylistAsync("pub-P");
        Assert.Equal(1, state!.Tracks[2].Dup);
        Assert.Equal(1, (await h.Store.LoadCatalogAsync()).Playlists.Single(p => p.Id == "pub-P").Duplicates);
        Assert.Equal(single, state.Tracks[2].Item); // still matched to its own file, just not written twice

        var again = await h.Sync();
        Assert.Equal(0, again.Writes);
    }

    [Fact]
    public async Task AddOnly_DoesNotAppendAnotherCopy_AndCleanupKeepsTheOriginalPosition()
    {
        using var h = new Harness();
        var first = h.Library.Add("Shiny Song", "The Band", 200, "Album");
        var other = h.Library.Add("Other Tune", "The Band", 180, "Album");
        var copy = h.Library.Add("Shiny Song", "The Band", 200, "Best Of");
        foreach (var r in h.Library.Items)
        {
            h.Target.Keys[r.Id] = SongKey.Of(r.Name, r.Artists![0])!;
        }

        await Add(h, "IN", "Inbox", SyncModes.AddOnly, null, T("a", "Shiny Song", "The Band", "Album", 200), T("b", "Other Tune", "The Band", "Album", 180));
        await h.Sync();
        Assert.Equal([first, other], h.JellyfinItems("Inbox"));

        // The inbox is cleared on Spotify and the song comes back as the "Best Of" version.
        h.Public.Lists["IN"] = h.Public.Lists["IN"] with { Tracks = [T("c", "Shiny Song", "The Band", "Best Of", 200)] };
        await h.Sync();
        Assert.Equal([first, other], h.JellyfinItems("Inbox"));

        // A copy that got in before (e.g. older version of the plugin) is removed by "Remove duplicates now".
        h.JellyfinItems("Inbox").Add(copy);
        h.Engine.RequestDedupe();
        await h.Sync(SyncMode.RematchOnly);
        Assert.Equal([first, other], h.JellyfinItems("Inbox"));
    }

    [Fact]
    public async Task CombinedParts_ShareASongOnlyOnce()
    {
        using var h = new Harness();
        var x = h.Library.Add("Shiny Song", "The Band", 200, "Album");
        var y = h.Library.Add("Other Tune", "The Band", 180, "Album");
        await Add(h, "P1", "Rock 1", SyncModes.Mirror, "Rock", T("a", "Shiny Song", "The Band", "Album", 200));
        await Add(h, "P2", "Rock 2", SyncModes.Mirror, "Rock", T("a2", "Shiny Song", "The Band", "Album", 200), T("b", "Other Tune", "The Band", "Album", 180));
        await h.Sync();
        Assert.Equal([x, y], h.JellyfinItems("Rock"));
    }

    [Fact]
    public async Task SettingOff_KeepsDifferentFilesOfTheSameSong()
    {
        using var h = new Harness();
        h.Settings = h.Settings with { RemoveDuplicates = false };
        var album = h.Library.Add("Shiny Song", "The Band", 200, "Album");
        var single = h.Library.Add("Shiny Song", "The Band", 200, "Single");
        await Add(h, "P", "Mix", SyncModes.Mirror, null, T("a", "Shiny Song", "The Band", "Album", 200), T("c", "Shiny Song", "The Band", "Single", 200));
        await h.Sync();
        Assert.Equal([album, single], h.JellyfinItems("Mix"));
    }
}
