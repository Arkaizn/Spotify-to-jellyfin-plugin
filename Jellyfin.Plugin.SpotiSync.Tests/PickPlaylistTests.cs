using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

/// <summary>Syncing into a Jellyfin playlist that was picked from the list (by id) instead of typed by name.</summary>
public class PickPlaylistTests
{
    private static SpTrack T(string id, string title) => FakeSpotify.Track(id, title, "Band " + title, 200, "Album " + title);

    private static async Task<Harness> Setup()
    {
        var h = new Harness();
        foreach (var s in new[] { "Alpha", "Bravo", "Charlie", "Delta" })
        {
            h.Library.Add(s, "Band " + s, 200, "Album " + s);
        }

        h.Public.Lists["A"] = new PublicPlaylist("Spotify A", null, [T("a1", "Alpha"), T("a2", "Bravo")], false);
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "pub-A", SpotifyId = "A", Source = CatalogEntry.SourcePublic, Name = "Spotify A", Readable = true });
        await h.Store.SaveCatalogAsync(c);
        return h;
    }

    private static Guid Id(Harness h, string title) => h.Library.Items.Single(r => r.Name == title).Id;

    private static async Task<SyncDef> AddSync(Harness h, Action<SyncDef> setup, string mode = SyncModes.Mirror)
    {
        var c = await h.Store.LoadCatalogAsync();
        var s = new SyncDef { Id = SyncDefs.NewId(), Name = "typed", Mode = mode, Sources = ["pub-A"] };
        setup(s);
        c.Syncs.Add(s);
        await h.Store.SaveCatalogAsync(c);
        return s;
    }

    [Fact]
    public void List_ShowsExistingPlaylists()
    {
        using var h = new Harness();
        var id = Guid.NewGuid();
        h.Target.Playlists[id] = ("Rock", [Guid.NewGuid(), Guid.NewGuid()]);
        var l = h.Target.List(Harness.Owner);
        Assert.Equal(new JfPlaylistInfo(id, "Rock", 2), Assert.Single(l));
    }

    [Fact]
    public async Task PickedPlaylist_IsUsedById_EvenWhenAnotherHasTheTypedName()
    {
        using var h = await Setup();
        var picked = Guid.NewGuid();
        var decoy = Guid.NewGuid();
        h.Target.Playlists[decoy] = ("Rock", []);
        h.Target.Playlists[picked] = ("Rock", [Id(h, "Delta")]); // same name: only the id tells them apart
        await AddSync(h, s => SyncDefs.LinkTo(s, new JfPlaylistInfo(picked, "Rock", 1), string.Empty));

        await h.Sync();
        Assert.Equal([Id(h, "Alpha"), Id(h, "Bravo")], h.Target.Playlists[picked].Items); // mirror replaced Delta
        Assert.Empty(h.Target.Playlists[decoy].Items);
        Assert.Equal(2, h.Target.Playlists.Count); // nothing created
        Assert.Equal("Rock", h.Target.Playlists[picked].Name);
    }

    [Fact]
    public async Task PickedPlaylist_AddOnly_KeepsItsSongs()
    {
        using var h = await Setup();
        var picked = Guid.NewGuid();
        h.Target.Playlists[picked] = ("Mine", [Id(h, "Delta"), Id(h, "Bravo")]);
        await AddSync(h, s => SyncDefs.LinkTo(s, new JfPlaylistInfo(picked, "Mine", 2), string.Empty), SyncModes.AddOnly);

        await h.Sync();
        Assert.Equal([Id(h, "Delta"), Id(h, "Bravo"), Id(h, "Alpha")], h.Target.Playlists[picked].Items);
    }

    [Fact]
    public async Task PickedPlaylist_IsNotRenamed_ByTheNamePrefix()
    {
        using var h = await Setup();
        h.Settings = h.Settings with { NamePrefix = "Spotify - " };
        var plain = Guid.NewGuid();
        var prefixed = Guid.NewGuid();
        h.Target.Playlists[plain] = ("Chill", []);
        h.Target.Playlists[prefixed] = ("Spotify - Gym", []);
        var a = await AddSync(h, s => SyncDefs.LinkTo(s, new JfPlaylistInfo(plain, "Chill", 0), "Spotify - "));
        var b = await AddSync(h, s => SyncDefs.LinkTo(s, new JfPlaylistInfo(prefixed, "Spotify - Gym", 0), "Spotify - "));
        Assert.Equal("Chill", a.Name);
        Assert.True(a.NoPrefix);
        Assert.Equal("Gym", b.Name); // shown without the prefix, written with it
        Assert.False(b.NoPrefix);

        await h.Sync();
        Assert.Equal("Chill", h.Target.Playlists[plain].Name);
        Assert.Equal("Spotify - Gym", h.Target.Playlists[prefixed].Name);
        Assert.Equal(2, h.Target.Playlists.Count);
    }

    [Fact]
    public async Task Unlink_WritesIntoANewPlaylist_AndLeavesTheOldOne()
    {
        using var h = await Setup();
        var old = Guid.NewGuid();
        h.Target.Playlists[old] = ("Old", []);
        var sync = await AddSync(h, s => SyncDefs.LinkTo(s, new JfPlaylistInfo(old, "Old", 0), string.Empty));
        await h.Sync();
        Assert.Equal(2, h.Target.Playlists[old].Items.Count);

        var c = await h.Store.LoadCatalogAsync();
        SyncDefs.Unlink(c.Syncs.Single(), "Fresh");
        await h.Store.SaveCatalogAsync(c);
        await h.Sync();

        Assert.Equal(2, h.Target.Playlists[old].Items.Count); // untouched
        Assert.Equal("Old", h.Target.Playlists[old].Name);
        Assert.Equal([Id(h, "Alpha"), Id(h, "Bravo")], h.JellyfinItems("Fresh"));
        Assert.NotEqual(old, (await h.Store.LoadCatalogAsync()).Syncs.Single().JfId);
    }
}
