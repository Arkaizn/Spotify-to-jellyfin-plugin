using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpotiSync.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;

namespace Jellyfin.Plugin.SpotiSync.Server;

/// <summary>Writes playlists through <see cref="IPlaylistManager"/>.</summary>
public sealed class JellyfinPlaylistTarget : IPlaylistTarget
{
    private readonly IPlaylistManager _playlists;
    private readonly ILibraryManager _library;

    /// <summary>Initializes a new instance of the <see cref="JellyfinPlaylistTarget"/> class.</summary>
    /// <param name="playlists">Playlist manager.</param>
    /// <param name="library">Library manager.</param>
    public JellyfinPlaylistTarget(IPlaylistManager playlists, ILibraryManager library)
    {
        _playlists = playlists;
        _library = library;
    }

    /// <inheritdoc />
    public Guid? Resolve(Guid? knownId, string name, Guid ownerId)
    {
        if (knownId is Guid k && _library.GetItemById(k) is Playlist)
        {
            return k;
        }

        Guid? sameName = null;
        foreach (var p in _playlists.GetPlaylists(ownerId))
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (p.OwnerUserId == ownerId)
                {
                    return p.Id;
                }

                sameName ??= p.Id;
            }
        }

        return sameName;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetItems(Guid playlistId)
    {
        if (_library.GetItemById(playlistId) is not Playlist playlist)
        {
            return [];
        }

        // Resolved entries only, in the order PlaylistManager uses for move indices.
        var children = playlist.GetManageableItems();
        var list = new List<Guid>(children.Count);
        foreach (var c in children)
        {
            if (c.Item1.ItemId is Guid id)
            {
                list.Add(id);
            }
        }

        return list;
    }

    /// <inheritdoc />
    public string? GetSongKey(Guid itemId)
        => _library.GetItemById(itemId) is MediaBrowser.Controller.Entities.Audio.Audio a
            ? Matching.SongKey.Of(a.Name, a.Artists is { Count: > 0 } ? a.Artists[0] : (a.AlbumArtists is { Count: > 0 } ? a.AlbumArtists[0] : null))
            : null;

    /// <inheritdoc />
    public Task RenameAsync(Guid playlistId, string name, Guid ownerId, CancellationToken ct)
        => _playlists.UpdatePlaylist(new PlaylistUpdateRequest { Id = playlistId, UserId = ownerId, Name = name });

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(string name, Guid ownerId, IReadOnlyList<Guid> items, CancellationToken ct)
    {
        var result = await _playlists.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = name,
            UserId = ownerId,
            ItemIdList = items,
            MediaType = MediaType.Audio,
            Public = false,
        }).ConfigureAwait(false);
        return Guid.Parse(result.Id, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public Task AppendAsync(Guid playlistId, IReadOnlyList<Guid> items, Guid ownerId, CancellationToken ct)
        => _playlists.AddItemToPlaylistAsync(playlistId, ToCollection(items), null, ownerId);

    /// <inheritdoc />
    public Task RemoveAsync(Guid playlistId, IReadOnlyList<Guid> items, CancellationToken ct)
    {
        var entryIds = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            entryIds[i] = items[i].ToString("N", CultureInfo.InvariantCulture);
        }

        return _playlists.RemoveItemFromPlaylistAsync(playlistId.ToString("N", CultureInfo.InvariantCulture), entryIds);
    }

    /// <inheritdoc />
    public Task MoveAsync(Guid playlistId, Guid item, int newIndex, Guid ownerId, CancellationToken ct)
        => _playlists.MoveItemAsync(playlistId.ToString("N", CultureInfo.InvariantCulture), item.ToString("N", CultureInfo.InvariantCulture), newIndex, ownerId);

    /// <inheritdoc />
    public Task ReplaceAsync(Guid playlistId, IReadOnlyList<Guid> items, Guid ownerId, CancellationToken ct)
        => _playlists.UpdatePlaylist(new PlaylistUpdateRequest { Id = playlistId, UserId = ownerId, Ids = items });

    private static IReadOnlyCollection<Guid> ToCollection(IReadOnlyList<Guid> items)
        => items as IReadOnlyCollection<Guid> ?? new List<Guid>(items);
}
