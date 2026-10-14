using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;
using static Jellyfin.Plugin.SpotiSync.Tests.FakeSpotify;

namespace Jellyfin.Plugin.SpotiSync.Tests;

public class SyncEngineTests
{
    private static (Harness H, Guid[] Lib) Setup(int n = 6)
    {
        var h = new Harness();
        var lib = new Guid[n];
        for (int i = 0; i < n; i++)
        {
            lib[i] = h.Library.Add("Song Number" + Word(i), "Artist" + Word(i), 200 + i);
        }

        return (h, lib);
    }

    private static string Word(int i) => " " + (char)('a' + i) + (char)('k' + i) + (char)('q' + i) + "x";

    private static Spotify.SpTrack T(int i) => Track("t" + i, "Song Number" + Word(i), "Artist" + Word(i), 200 + i);

    [Fact]
    public async Task FirstSync_CreatesPlaylist_SecondSyncDoesNothing()
    {
        var (h, lib) = Setup();
        using var _ = h;
        h.Spotify.AddPlaylist("p1", "Mix", [T(0), T(1), T(2), Track("x", "Not In Library At All", "Nobody", 100)]);
        await h.Sync(); // discovers playlists (disabled by default)
        Assert.Empty(h.Target.Playlists);
        await h.Enable("p1");

        var first = await h.Sync();
        Assert.Equal(1, first.Fetched);
        Assert.Equal([lib[0], lib[1], lib[2]], h.JellyfinItems("Mix"));
        var catalog = await h.Store.LoadCatalogAsync();
        var entry = catalog.Playlists.Single(p => p.Id == "p1");
        Assert.Equal(3, entry.Matched);
        Assert.Equal(1, entry.Missing);

        var writes = h.Target.Writes;
        var stamp = File.GetLastWriteTimeUtc(h.Store.PlaylistPath("p1"));
        var second = await h.Sync();
        Assert.Equal(0, second.Fetched);
        Assert.Equal(0, second.Matched);
        Assert.Equal(0, second.Writes);
        Assert.Equal(0, second.StateWrites);
        Assert.False(second.IndexUsed);
        Assert.Equal(writes, h.Target.Writes);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(h.Store.PlaylistPath("p1")));
        Assert.Equal(1, h.Library.ReadAllCalls);
        Assert.Equal(3, h.Spotify.ListCalls);
    }

    [Fact]
    public async Task Mirror_ReordersAndRemoves_WithMinimalMoves()
    {
        var (h, lib) = Setup();
        using var _ = h;
        var p = h.Spotify.AddPlaylist("p1", "Mix", [T(0), T(1), T(2), T(3), T(4)]);
        await h.Sync();
        await h.Enable("p1");
        await h.Sync();

        // Move T(4) to the front, drop T(1), add T(5).
        h.Spotify.Items["p1"] = [T(4), T(0), T(2), T(3), T(5)];
        p.SnapshotId = "s2";
        var o = await h.Sync();
        Assert.Equal(1, o.Fetched);
        Assert.Equal(1, o.Matched); // only the new track was scored
        Assert.Equal([lib[4], lib[0], lib[2], lib[3], lib[5]], h.JellyfinItems("Mix"));
        Assert.Equal(1, h.Target.Moves);
        Assert.Equal(0, h.Target.Replaces);
    }

    [Fact]
    public async Task Mirror_RestoresManualEdits()
    {
        var (h, lib) = Setup();
        using var _ = h;
        h.Spotify.AddPlaylist("p1", "Mix", [T(0), T(1), T(2)]);
        await h.Sync();
        await h.Enable("p1");
        await h.Sync();
        var items = h.JellyfinItems("Mix");
        items.Reverse();
        items.Add(lib[5]);

        await h.Sync();
        Assert.Equal([lib[0], lib[1], lib[2]], h.JellyfinItems("Mix"));
    }

    [Fact]
    public async Task AddOnly_NeverRemovesOrReorders()
    {
        var (h, lib) = Setup();
        using var _ = h;
        var p = h.Spotify.AddPlaylist("p1", "Mix", [T(0), T(1), T(2)]);
        await h.Sync();
        await h.Enable("p1", SyncModes.AddOnly);
        await h.Sync();
        h.Spotify.Items["p1"] = [T(3), T(2)];
        p.SnapshotId = "s2";
        await h.Sync();
        Assert.Equal([lib[0], lib[1], lib[2], lib[3]], h.JellyfinItems("Mix"));
    }

    [Fact]
    public async Task TakesOverExistingPlaylistWithSameName()
    {
        var (h, lib) = Setup();
        using var _ = h;
        var existing = Guid.NewGuid();
        h.Target.Playlists[existing] = ("Road Trip", [lib[5]]);
        h.Spotify.AddPlaylist("p1", "Road Trip", [T(0), T(1)]);
        await h.Sync();
        await h.Enable("p1");
        await h.Sync();
        Assert.Single(h.Target.Playlists);
        Assert.Equal([lib[0], lib[1]], h.Target.Playlists[existing].Items);
    }

    [Fact]
    public async Task Overrides_PickAndIgnore_AreAppliedAndStick()
    {
        var (h, lib) = Setup();
        using var _ = h;
        var p = h.Spotify.AddPlaylist("p1", "Mix", [T(0), Track("x", "Unknown Track", "Nobody", 100), T(2)]);
        await h.Sync();
        await h.Enable("p1");
        await h.Sync();
        Assert.Equal([lib[0], lib[2]], h.JellyfinItems("Mix"));

        await h.SetOverride("x", new OverrideEntry { Item = lib[5], Text = "picked" });
        await h.SetOverride("t2", new OverrideEntry { Ignore = true });
        var o = await h.Sync(SyncMode.RematchOnly);
        Assert.False(o.IndexUsed);
        Assert.Equal([lib[0], lib[5]], h.JellyfinItems("Mix"));

        // A new Spotify snapshot keeps the overrides.
        h.Spotify.Items["p1"] = [T(0), Track("x", "Unknown Track", "Nobody", 100), T(2), T(3)];
        p.SnapshotId = "s2";
        await h.Sync();
        Assert.Equal([lib[0], lib[5], lib[3]], h.JellyfinItems("Mix"));
        var state = await h.Store.LoadPlaylistAsync("p1");
        Assert.Equal(TrackStatus.Ignored, state!.Tracks.Single(t => t.Key == "t2").St);

        // Clearing the override rematches the track.
        await h.SetOverride("t2", null);
        await h.Sync(SyncMode.RematchOnly);
        Assert.Equal([lib[0], lib[5], lib[2], lib[3]], h.JellyfinItems("Mix"));
    }

    [Fact]
    public async Task LibraryChange_RematchesOnlyUnmatchedTracks()
    {
        var (h, lib) = Setup();
        using var _ = h;
        h.Spotify.AddPlaylist("p1", "Mix", [T(0), T(1), Track("new", "Brand New Tune", "Fresh Band", 180)]);
        await h.Sync();
        await h.Enable("p1");
        var first = await h.Sync();
        Assert.Equal(3, first.Matched);

        var added = h.Library.Add("Brand New Tune", "Fresh Band", 181);
        var o = await h.Sync();
        Assert.Equal(1, o.Matched);
        Assert.Equal([lib[0], lib[1], added], h.JellyfinItems("Mix"));
    }

    [Fact]
    public async Task RemovedLibraryItem_IsRematched()
    {
        var (h, lib) = Setup();
        using var _ = h;
        h.Spotify.AddPlaylist("p1", "Mix", [T(0), T(1)]);
        await h.Sync();
        await h.Enable("p1");
        await h.Sync();
        h.Library.Items.RemoveAll(r => r.Id == lib[1]);
        var o = await h.Sync();
        Assert.Equal(1, o.Matched);
        Assert.Equal([lib[0]], h.JellyfinItems("Mix"));
    }

    [Fact]
    public async Task RateLimit_StopsFetchingAndUsesCache()
    {
        var (h, lib) = Setup();
        using var _ = h;
        var p1 = h.Spotify.AddPlaylist("p1", "A", [T(0)]);
        var p2 = h.Spotify.AddPlaylist("p2", "B", [T(1)]);
        await h.Sync();
        await h.Enable("p1");
        await h.Enable("p2");
        await h.Sync();

        p1.SnapshotId = "s2";
        h.Spotify.Items["p1"] = [T(0), T(2)];
        h.Spotify.RateLimitOn.Add("p1");
        p2.SnapshotId = "s2";
        h.Spotify.Items["p2"] = [T(1), T(3)];
        var calls = h.Spotify.ItemCalls;
        var o = await h.Sync();
        Assert.NotNull(o.SpotifyError);
        Assert.Equal(calls + 1, h.Spotify.ItemCalls); // p2 was not fetched after the 429
        Assert.Equal([lib[0]], h.JellyfinItems("A"));
        Assert.Equal([lib[1]], h.JellyfinItems("B"));

        // Remembered deadline: no Spotify calls at all.
        h.Spotify.RateLimit = DateTimeOffset.UtcNow.AddHours(1);
        var lists = h.Spotify.ListCalls;
        await h.Sync();
        Assert.Equal(lists, h.Spotify.ListCalls);
    }

    [Fact]
    public async Task NotOwnedPlaylists_AreNotReadable()
    {
        var (h, _lib) = Setup();
        using var _ = h;
        h.Spotify.AddPlaylist("p1", "Theirs", [T(0)], owner: "someone-else");
        var collab = h.Spotify.AddPlaylist("p2", "Shared", [T(1)], owner: "someone-else");
        collab.Collaborative = true;
        await h.Sync();
        var c = await h.Store.LoadCatalogAsync();
        Assert.False(c.Playlists.Single(p => p.Id == "p1").Readable);
        Assert.True(c.Playlists.Single(p => p.Id == "p2").Readable);
        Assert.True(c.Playlists.Single(p => p.Id == CatalogEntry.LikedId).Readable);
    }

    [Fact]
    public async Task LikedSongs_AreSynced()
    {
        var (h, lib) = Setup();
        using var _ = h;
        h.Spotify.Liked.AddRange([T(3), T(4)]);
        await h.Sync();
        await h.Enable(CatalogEntry.LikedId);
        await h.Sync();
        Assert.Equal([lib[3], lib[4]], h.JellyfinItems("Liked Songs"));
        Assert.Equal(2, (await h.Store.LoadCatalogAsync()).Playlists.Single(p => p.Id == CatalogEntry.LikedId).Total);
        await h.Sync();
        Assert.Equal(1, h.Spotify.LikedCalls);
    }

    [Fact]
    public async Task FieldsRejected_IsRemembered()
    {
        var (h, _lib) = Setup();
        using var _ = h;
        h.Spotify.AddPlaylist("p1", "Mix", [T(0)]);
        h.Spotify.RejectFields = true;
        await h.Sync();
        await h.Enable("p1");
        await h.Sync();
        Assert.True((await h.Store.LoadCatalogAsync()).NoFields);
    }

    [Fact]
    public async Task OnlyOneSyncAtATime()
    {
        var (h, _lib) = Setup();
        using var _ = h;
        h.Spotify.Block = new TaskCompletionSource();
        var a = h.Sync();
        var b = await h.Sync();
        Assert.True(b.AlreadyRunning);
        Assert.True(h.Engine.IsRunning);
        h.Spotify.Block.SetResult();
        Assert.False((await a).AlreadyRunning);
        Assert.False(h.Engine.IsRunning);
    }
}

public class PlaylistDiffTests
{
    [Fact]
    public void RandomPermutations_ProduceDesiredOrder_WithMinimalMoves()
    {
        var rng = new Random(42);
        for (int round = 0; round < 500; round++)
        {
            int n = rng.Next(0, 30);
            var pool = Enumerable.Range(0, n + 10).Select(_ => Guid.NewGuid()).ToList();
            var current = pool.OrderBy(_ => rng.Next()).Take(n).ToList();
            var desired = pool.OrderBy(_ => rng.Next()).Take(rng.Next(0, n + 5)).ToList();
            var plan = PlaylistDiff.Compute(current, desired, mirror: true);
            var list = current.ToList();
            PlaylistDiff.ApplyTo(list, plan, desired);
            Assert.Equal(desired, list);
            if (!plan.Replace)
            {
                Assert.True(plan.Moves.Count <= PlaylistDiff.MaxMoves);
            }
        }
    }

    [Fact]
    public void SingleMove_ForRotation()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var plan = PlaylistDiff.Compute([c, a, b], [a, b, c], true);
        Assert.Single(plan.Moves);
        Assert.Empty(plan.Remove);
        Assert.Empty(plan.Append);
    }

    [Fact]
    public void Identical_IsEmpty()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        Assert.True(PlaylistDiff.Compute(ids, ids, true).IsEmpty);
        Assert.True(PlaylistDiff.Compute(ids, ids, false).IsEmpty);
    }

    [Fact]
    public void Duplicates_ForceReplace()
    {
        var a = Guid.NewGuid();
        var plan = PlaylistDiff.Compute([a, a], [a], true);
        Assert.True(plan.Replace);
    }
}

public class SyncEngineNameClashTests
{
    [Fact]
    public async Task TwoSpotifyPlaylistsWithSameName_GetSeparateJellyfinPlaylists()
    {
        using var h = new Harness();
        var a = h.Library.Add("Alpha Song", "Band One", 200);
        var b = h.Library.Add("Beta Tune", "Band Two", 210);
        h.Spotify.AddPlaylist("p1", "Mix", [FakeSpotify.Track("t1", "Alpha Song", "Band One", 200)]);
        h.Spotify.AddPlaylist("p2", "Mix", [FakeSpotify.Track("t2", "Beta Tune", "Band Two", 210)]);
        await h.Sync();
        await h.Enable("p1");
        await h.Enable("p2");
        await h.Sync();
        await h.Sync();
        Assert.Equal(2, h.Target.Playlists.Count);
        Assert.Contains(h.Target.Playlists.Values, p => p.Items.SequenceEqual([a]));
        Assert.Contains(h.Target.Playlists.Values, p => p.Items.SequenceEqual([b]));
    }
}
