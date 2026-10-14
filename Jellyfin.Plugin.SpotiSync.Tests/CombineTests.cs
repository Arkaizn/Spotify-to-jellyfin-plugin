using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

public class CombineTests
{
    private static SpTrack T(int i) => FakeSpotify.Track("t" + i, "Song " + Word(i), "Band " + Word(i), 200 + i);

    private static string Word(int i) => new string((char)('a' + (i % 26)), 3) + (char)('k' + (i / 26)) + "x";

    private static (Harness H, List<Guid> Lib) Setup(int n)
    {
        var h = new Harness();
        var lib = new List<Guid>();
        for (int i = 0; i < n; i++)
        {
            lib.Add(h.Library.Add("Song " + Word(i), "Band " + Word(i), 200 + i));
        }

        return (h, lib);
    }

    private static async Task AddPublic(Harness h, string spotifyId, string name, string? target, string mode, IEnumerable<int> songs)
    {
        h.Public.Lists[spotifyId] = new PublicPlaylist(name, null, songs.Select(T).ToList(), false);
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "pub-" + spotifyId, SpotifyId = spotifyId, Source = CatalogEntry.SourcePublic, Name = name, Target = target, Mode = mode, Readable = true, Enabled = true });
        await h.Store.SaveCatalogAsync(c);
    }

    private static void SetSongs(Harness h, string spotifyId, IEnumerable<int> songs)
        => h.Public.Lists[spotifyId] = h.Public.Lists[spotifyId] with { Tracks = songs.Select(T).ToList() };

    [Fact]
    public async Task Parts_AreCombinedInNaturalOrder_AndMirrored()
    {
        var (h, lib) = Setup(12);
        using var _ = h;
        await AddPublic(h, "P10", "Rock 10", "Rock", SyncModes.Mirror, [6, 7, 8]);
        await AddPublic(h, "P2", "Rock 2", "Rock", SyncModes.Mirror, [0, 1, 2]);

        await h.Sync();
        Assert.Single(h.Target.Playlists);
        Assert.Equal([lib[0], lib[1], lib[2], lib[6], lib[7], lib[8]], h.JellyfinItems("Rock"));

        var again = await h.Sync();
        Assert.Equal(0, again.Writes);
        Assert.Equal(0, again.StateWrites);

        SetSongs(h, "P2", [0, 2, 3]); // 1 removed, 3 added
        await h.Sync();
        Assert.Equal([lib[0], lib[2], lib[3], lib[6], lib[7], lib[8]], h.JellyfinItems("Rock"));
    }

    [Fact]
    public async Task AddOnlyInbox_KeepsSongsRemovedFromSpotify()
    {
        var (h, lib) = Setup(10);
        using var _ = h;
        await AddPublic(h, "IN", "Mix inbox", "Mix", SyncModes.AddOnly, [0, 1, 2]);
        await h.Sync();
        SetSongs(h, "IN", [3, 4]); // old ones archived away on Spotify
        await h.Sync();
        Assert.Equal([lib[0], lib[1], lib[2], lib[3], lib[4]], h.JellyfinItems("Mix"));
    }

    [Fact]
    public async Task FailingPart_NeverRemovesItsSongs()
    {
        var (h, lib) = Setup(10);
        using var _ = h;
        await AddPublic(h, "A", "Rock 1", "Rock", SyncModes.Mirror, [0, 1]);
        await AddPublic(h, "B", "Rock 2", "Rock", SyncModes.Mirror, [2, 3]);
        await h.Sync();
        Assert.Equal(4, h.JellyfinItems("Rock").Count);

        h.Public.Lists.Remove("B"); // Spotify page unavailable this time
        await h.Sync();
        Assert.Equal([lib[0], lib[1], lib[2], lib[3]], h.JellyfinItems("Rock"));
    }

    [Fact]
    public async Task ImportedFileAndPublicInbox_FeedOnePlaylist()
    {
        var (h, lib) = Setup(10);
        using var _ = h;
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "file-rock", Source = CatalogEntry.SourceFile, Name = "Rock", Target = "Rock", Mode = SyncModes.AddOnly, Readable = true, Enabled = true });
        await h.Store.SaveCatalogAsync(c);
        var state = new PlaylistState { Id = "file-rock", FetchedAt = DateTime.UtcNow };
        SyncEngine.MergeTracks(state, [T(0), T(1), T(2)]);
        await h.Store.SavePlaylistAsync(state);
        await AddPublic(h, "IN", "Rock inbox", "Rock", SyncModes.AddOnly, [5]);

        await h.Sync();
        Assert.Equal([lib[0], lib[1], lib[2], lib[5]], h.JellyfinItems("Rock"));
    }

    [Theory]
    [InlineData("Rock 2", "Rock 10", -1)]
    [InlineData("Rock 10", "Rock 9", 1)]
    [InlineData("rock a", "Rock B", -1)]
    [InlineData("Part 02", "Part 2", 0)]
    [InlineData("Mix", "Mix 1", -1)]
    public void NaturalCompare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SyncEngine.NaturalCompare(a, b)));
}
