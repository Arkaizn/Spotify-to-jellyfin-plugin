using Jellyfin.Plugin.SpotiSync.Matching;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SpotiSync.Tests;

internal sealed class FakeSpotify : ISpotifySource
{
    public List<SpPlaylist> Playlists { get; } = [];

    public Dictionary<string, List<SpTrack>> Items { get; } = [];

    public List<SpTrack> Liked { get; } = [];

    public int ListCalls;
    public int ItemCalls;
    public int LikedCalls;
    public DateTimeOffset? RateLimit;
    public HashSet<string> RateLimitOn = [];
    public bool RejectFields;
    public TaskCompletionSource? Block;

    public Task<bool> IsConnectedAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<DateTimeOffset?> GetRateLimitDeadlineAsync(CancellationToken ct) => Task.FromResult(RateLimit);

    public Task<SpUser> GetMeAsync(CancellationToken ct) => Task.FromResult(new SpUser { Id = "me", DisplayName = "Me" });

    public async Task<List<SpPlaylist>> GetPlaylistsAsync(CancellationToken ct)
    {
        ListCalls++;
        if (Block is not null)
        {
            await Block.Task;
        }

        return Playlists.ToList();
    }

    public Task<PlaylistItemsResult> GetPlaylistItemsAsync(string playlistId, bool useFields, CancellationToken ct)
    {
        ItemCalls++;
        if (RateLimitOn.Contains(playlistId))
        {
            throw new SpotifyRateLimitedException(DateTimeOffset.UtcNow.AddHours(1));
        }

        return Task.FromResult(new PlaylistItemsResult(Items[playlistId].ToList(), useFields && RejectFields));
    }

    public Task<string> GetLikedSnapshotAsync(CancellationToken ct) => Task.FromResult(Liked.Count + ":" + (Liked.Count > 0 ? Liked[0].Id : string.Empty));

    public Task<List<SpTrack>> GetLikedTracksAsync(CancellationToken ct)
    {
        LikedCalls++;
        return Task.FromResult(Liked.ToList());
    }

    public void ReleaseCache()
    {
    }

    public SpPlaylist AddPlaylist(string id, string name, IEnumerable<SpTrack> tracks, string owner = "me", string snapshot = "s1")
    {
        var p = new SpPlaylist { Id = id, Name = name, Owner = new SpUser { Id = owner }, SnapshotId = snapshot, Items = new SpTotal() };
        Items[id] = tracks.ToList();
        p.Items.Total = Items[id].Count;
        Playlists.Add(p);
        return p;
    }

    public static SpTrack Track(string id, string name, string artist, int seconds, string album = "Album")
        => new() { Id = id, Uri = "spotify:track:" + id, Name = name, Type = "track", DurationMs = seconds * 1000, Artists = [new SpArtist { Name = artist }], Album = new SpAlbum { Name = album } };
}

internal sealed class FakeLibrary : ILibrarySource
{
    public List<LibraryRecord> Items { get; } = [];

    public int ReadAllCalls;
    public DateTime? LastChange { get; set; }

    public LibraryFingerprint GetFingerprint() => new(Items.Count, Items.Count == 0 ? 0 : Items.Count * 1000L);

    public int CountChangedSince(DateTime sinceUtc) => LastChange is DateTime d && d > sinceUtc ? 1 : 0;

    public IEnumerable<LibraryRecord> ReadAll(CancellationToken ct)
    {
        ReadAllCalls++;
        return Items;
    }

    public Guid Add(string title, string artist, int seconds, string album = "Album")
    {
        var id = Guid.NewGuid();
        Items.Add(new LibraryRecord(id, title, [artist], [artist], album, seconds * TimeSpan.TicksPerSecond, null));
        return id;
    }
}

internal sealed class FakeTarget : IPlaylistTarget
{
    public Dictionary<Guid, (string Name, List<Guid> Items)> Playlists { get; } = [];

    public int Writes;
    public int Moves;
    public int Replaces;

    public Guid? Resolve(Guid? knownId, string name, Guid ownerId)
    {
        if (knownId is Guid k && Playlists.ContainsKey(k))
        {
            return k;
        }

        foreach (var kv in Playlists)
        {
            if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Key;
            }
        }

        return null;
    }

    public IReadOnlyList<Guid> GetItems(Guid playlistId) => Playlists[playlistId].Items.ToList();

    public Dictionary<Guid, string> Keys { get; } = [];

    public string? GetSongKey(Guid itemId) => Keys.TryGetValue(itemId, out var k) ? k : null;

    public Task RenameAsync(Guid playlistId, string name, Guid ownerId, CancellationToken ct)
    {
        Writes++;
        Playlists[playlistId] = (name, Playlists[playlistId].Items);
        return Task.CompletedTask;
    }

    public Task<Guid> CreateAsync(string name, Guid ownerId, IReadOnlyList<Guid> items, CancellationToken ct)
    {
        Writes++;
        var id = Guid.NewGuid();
        Playlists[id] = (name, items.ToList());
        return Task.FromResult(id);
    }

    public Task AppendAsync(Guid playlistId, IReadOnlyList<Guid> items, Guid ownerId, CancellationToken ct)
    {
        Writes++;
        Playlists[playlistId].Items.AddRange(items);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid playlistId, IReadOnlyList<Guid> items, CancellationToken ct)
    {
        Writes++;
        Playlists[playlistId].Items.RemoveAll(items.Contains);
        return Task.CompletedTask;
    }

    // Same semantics as Jellyfin 12.2 PlaylistManager.MoveItemAsync: the item ends up at newIndex.
    public Task MoveAsync(Guid playlistId, Guid item, int newIndex, Guid ownerId, CancellationToken ct)
    {
        Writes++;
        Moves++;
        var list = Playlists[playlistId].Items;
        list.Remove(item);
        if (newIndex >= list.Count)
        {
            list.Add(item);
        }
        else
        {
            list.Insert(newIndex, item);
        }

        return Task.CompletedTask;
    }

    public Task ReplaceAsync(Guid playlistId, IReadOnlyList<Guid> items, Guid ownerId, CancellationToken ct)
    {
        Writes++;
        Replaces++;
        var list = Playlists[playlistId].Items;
        list.Clear();
        list.AddRange(items);
        return Task.CompletedTask;
    }
}

internal sealed class Harness : IDisposable
{
    public static readonly Guid Owner = Guid.NewGuid();

    public Harness()
    {
        Dir = Directory.CreateTempSubdirectory("spotisync-test-").FullName;
        Store = new StateStore(Dir);
        Engine = new SyncEngine(Spotify, Library, Target, Store, Cache, () => Settings, NullLogger.Instance, Public);
    }

    public string Dir { get; }

    public FakeSpotify Spotify { get; } = new();

    public FakePublic Public { get; } = new();

    public FakeLibrary Library { get; } = new();

    public FakeTarget Target { get; } = new();

    public IndexCache Cache { get; } = new();

    public StateStore Store { get; }

    public SyncEngine Engine { get; }

    public SyncSettings Settings { get; set; } = new(Owner, string.Empty, SyncModes.Mirror);

    public Task<SyncOutcome> Sync(SyncMode mode = SyncMode.Full) => Engine.RunAsync(mode, null, CancellationToken.None);

    public async Task Enable(string id, string mode = SyncModes.Mirror)
    {
        var c = await Store.LoadCatalogAsync();
        var e = c.Playlists.Single(p => p.Id == id);
        e.Enabled = true;
        e.Mode = mode;
        await Store.SaveCatalogAsync(c);
    }

    public async Task SetOverride(string key, OverrideEntry? entry)
    {
        var o = await Store.LoadOverridesAsync();
        o.Rev++;
        if (entry is null)
        {
            o.Items.Remove(key);
        }
        else
        {
            entry.Rev = o.Rev;
            o.Items[key] = entry;
        }

        await Store.SaveOverridesAsync(o);
    }

    public List<Guid> JellyfinItems(string name) => Target.Playlists.Values.Single(p => p.Name == name).Items;

    public void Dispose()
    {
        Engine.Dispose();
        Cache.Dispose();
        Store.Dispose();
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakePublic : IPublicPlaylistSource
{
    public Dictionary<string, PublicPlaylist> Lists { get; } = [];

    public int Calls;

    public Task<PublicPlaylist> GetAsync(string spotifyId, CancellationToken ct)
    {
        Calls++;
        return Lists.TryGetValue(spotifyId, out var p) ? Task.FromResult(p) : throw new SpotifyApiException(404, "Playlist not found. Is it public?");
    }
}
