using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

public class SyncDefTests
{
    private static SpTrack T(string id, string title) => FakeSpotify.Track(id, title, "Band " + title, 200, "Album " + title);

    private static async Task<Harness> Setup(params string[] songs)
    {
        var h = new Harness();
        foreach (var s in songs)
        {
            h.Library.Add(s, "Band " + s, 200, "Album " + s);
        }

        return await Task.FromResult(h);
    }

    private static async Task Source(Harness h, string id, string name, params SpTrack[] tracks)
    {
        h.Public.Lists[id] = new PublicPlaylist(name, null, tracks.ToList(), false);
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "pub-" + id, SpotifyId = id, Source = CatalogEntry.SourcePublic, Name = name, Readable = true });
        await h.Store.SaveCatalogAsync(c);
    }

    private static async Task<SyncDef> NewSync(Harness h, string name, string mode, params string[] sources)
    {
        var c = await h.Store.LoadCatalogAsync();
        var s = new SyncDef { Id = SyncDefs.NewId(), Name = name, Mode = mode, Sources = sources.Select(x => "pub-" + x).ToList() };
        c.Syncs.Add(s);
        await h.Store.SaveCatalogAsync(c);
        return s;
    }

    private Guid Id(Harness h, string title) => h.Library.Items.Single(r => r.Name == title).Id;

    [Fact]
    public async Task Sync_BuildsOnePlaylistFromSourcesInTheirOrder_WithCounts()
    {
        using var h = await Setup("Alpha", "Bravo", "Charlie", "Delta");
        await Source(h, "B", "Zeta part", T("b1", "Charlie"), T("b2", "Alpha"), T("x", "Nothing Here"));
        await Source(h, "A", "Alpha part", T("a1", "Alpha"), T("a2", "Bravo"));
        var sync = await NewSync(h, "Road", SyncModes.Mirror, "B", "A"); // manual order: B first

        await h.Sync();
        Assert.Equal([Id(h, "Charlie"), Id(h, "Alpha"), Id(h, "Bravo")], h.JellyfinItems("Road"));
        var s = (await h.Store.LoadCatalogAsync()).Syncs.Single();
        Assert.Equal(5, s.Total);
        Assert.Equal(3, s.InJellyfin);
        Assert.Equal(1, s.Duplicates);   // Alpha is in both parts
        Assert.Equal(1, s.Missing);
        Assert.NotNull(s.JfId);

        var again = await h.Sync();
        Assert.Equal(0, again.Writes);
        Assert.Equal(0, again.StateWrites);
    }

    [Fact]
    public async Task DisabledSync_IsNotWritten_AndUnusedSourcesAreNotFetched()
    {
        using var h = await Setup("Alpha");
        await Source(h, "A", "A", T("a1", "Alpha"));
        await Source(h, "U", "Unused", T("u1", "Alpha"));
        var c = await h.Store.LoadCatalogAsync();
        c.Syncs.Add(new SyncDef { Id = "s1", Name = "Off", Paused = true, Sources = ["pub-A"] });
        await h.Store.SaveCatalogAsync(c);
        await h.Sync();
        Assert.Empty(h.Target.Playlists);
        Assert.Equal(0, h.Public.Calls);
    }

    [Fact]
    public async Task Renaming_RenamesTheJellyfinPlaylist()
    {
        using var h = await Setup("Alpha");
        await Source(h, "A", "A", T("a1", "Alpha"));
        await NewSync(h, "Old Name", SyncModes.Mirror, "A");
        await h.Sync();
        var c = await h.Store.LoadCatalogAsync();
        c.Syncs[0].Name = "New Name";
        await h.Store.SaveCatalogAsync(c);
        await h.Sync();
        Assert.Single(h.Target.Playlists);
        Assert.Equal([Id(h, "Alpha")], h.JellyfinItems("New Name"));
    }

    [Fact]
    public async Task SameSourceInTwoSyncs_IsFetchedOnce()
    {
        using var h = await Setup("Alpha");
        await Source(h, "A", "A", T("a1", "Alpha"));
        await NewSync(h, "One", SyncModes.Mirror, "A");
        await NewSync(h, "Two", SyncModes.AddOnly, "A");
        await h.Sync();
        Assert.Equal(1, h.Public.Calls);
        Assert.Equal(2, h.Target.Playlists.Count);
    }

    [Fact]
    public void Ensure_ConvertsOldSetups()
    {
        var c = new Catalog
        {
            Playlists =
            [
                new CatalogEntry { Id = "a", Name = "Rock 10", Target = "Rock", Enabled = true, Readable = true },
                new CatalogEntry { Id = "b", Name = "Rock 2", Target = "Rock", Enabled = true, Readable = true, Mode = SyncModes.AddOnly },
                new CatalogEntry { Id = "c", Name = "Chill", Enabled = true, Readable = true, JfId = Guid.NewGuid() },
                new CatalogEntry { Id = "d", Name = "Off", Readable = true },
            ],
        };
        Assert.True(SyncDefs.Ensure(c));
        Assert.Equal(2, c.Syncs.Count);
        var rock = c.Syncs.Single(s => s.Name == "Rock");
        Assert.Equal(SyncModes.AddOnly, rock.Mode);
        Assert.Equal(["b", "a"], SyncDefs.OrderedSources(c, rock).Select(e => e.Id)); // natural order
        var chill = c.Syncs.Single(s => s.Name == "Chill");
        Assert.Equal(c.Playlists[2].JfId, chill.JfId); // keeps writing into the same Jellyfin playlist
        Assert.All(c.Playlists, e => Assert.False(e.Enabled));
        Assert.False(SyncDefs.Ensure(c));
    }
}
