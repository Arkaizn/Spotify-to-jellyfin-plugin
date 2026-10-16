using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SpotiSync.Matching;
using Jellyfin.Plugin.SpotiSync.Spotify;

namespace Jellyfin.Plugin.SpotiSync.Sync;

/// <summary>Tracks of a playlist and whether Spotify rejected the "fields" filter.</summary>
/// <param name="Tracks">Tracks in playlist order.</param>
/// <param name="FieldsRejected">True when the request had to be repeated without "fields".</param>
public sealed record PlaylistItemsResult(List<SpTrack> Tracks, bool FieldsRejected);

/// <summary>Read access to Spotify (implemented by <see cref="SpotifyClient"/>, faked in tests).</summary>
public interface ISpotifySource
{
    /// <summary>Whether a refresh token is stored.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when connected.</returns>
    Task<bool> IsConnectedAsync(CancellationToken ct);

    /// <summary>Deadline of a remembered long rate limit, if still in the future.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deadline or null.</returns>
    Task<DateTimeOffset?> GetRateLimitDeadlineAsync(CancellationToken ct);

    /// <summary>GET /me.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user.</returns>
    Task<SpUser> GetMeAsync(CancellationToken ct);

    /// <summary>GET /me/playlists (all pages).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Playlists.</returns>
    Task<List<SpPlaylist>> GetPlaylistsAsync(CancellationToken ct);

    /// <summary>GET /playlists/{id}/items (all pages).</summary>
    /// <param name="playlistId">Playlist id.</param>
    /// <param name="useFields">Whether to send the "fields" filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tracks.</returns>
    Task<PlaylistItemsResult> GetPlaylistItemsAsync(string playlistId, bool useFields, CancellationToken ct);

    /// <summary>A cheap change marker for Liked Songs (total + newest entry), one request.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Snapshot string.</returns>
    Task<string> GetLikedSnapshotAsync(CancellationToken ct);

    /// <summary>GET /me/tracks (all pages).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tracks, newest first.</returns>
    Task<List<SpTrack>> GetLikedTracksAsync(CancellationToken ct);

    /// <summary>Drops cached tokens.</summary>
    void ReleaseCache();
}

/// <summary>Cheap library change marker.</summary>
/// <param name="Count">Number of audio items.</param>
/// <param name="LatestCreatedTicks">DateCreated ticks of the newest audio item.</param>
public readonly record struct LibraryFingerprint(int Count, long LatestCreatedTicks)
{
    /// <inheritdoc />
    public override string ToString() => Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + LatestCreatedTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Read access to the audio library.</summary>
public interface ILibrarySource
{
    /// <summary>Count and newest DateCreated of audio items (a couple of cheap queries).</summary>
    /// <returns>The fingerprint.</returns>
    LibraryFingerprint GetFingerprint();

    /// <summary>Number of audio items saved (added or modified) after <paramref name="sinceUtc"/>.</summary>
    /// <param name="sinceUtc">Point in time.</param>
    /// <returns>Count.</returns>
    int CountChangedSince(DateTime sinceUtc);

    /// <summary>Streams all audio items (paged internally so peak memory stays small).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Records.</returns>
    IEnumerable<LibraryRecord> ReadAll(CancellationToken ct);
}

/// <summary>Write access to Jellyfin playlists.</summary>
public interface IPlaylistTarget
{
    /// <summary>Finds the playlist to sync into: <paramref name="knownId"/> if it still exists, else one named <paramref name="name"/>.</summary>
    /// <param name="knownId">Previously used playlist id.</param>
    /// <param name="name">Playlist name.</param>
    /// <param name="ownerId">Owner user.</param>
    /// <returns>Playlist id or null.</returns>
    Guid? Resolve(Guid? knownId, string name, Guid ownerId);

    /// <summary>Current item ids in playlist order.</summary>
    /// <param name="playlistId">Playlist id.</param>
    /// <returns>Item ids.</returns>
    IReadOnlyList<Guid> GetItems(Guid playlistId);

    /// <summary>Song key (see <c>SongKey</c>) of a library item, or null when unknown.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The key.</returns>
    string? GetSongKey(Guid itemId);

    /// <summary>Renames a playlist.</summary>
    /// <param name="playlistId">Playlist id.</param>
    /// <param name="name">New name.</param>
    /// <param name="ownerId">Owner.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RenameAsync(Guid playlistId, string name, Guid ownerId, CancellationToken ct);

    /// <summary>Creates a playlist.</summary>
    /// <param name="name">Name.</param>
    /// <param name="ownerId">Owner.</param>
    /// <param name="items">Initial items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New playlist id.</returns>
    Task<Guid> CreateAsync(string name, Guid ownerId, IReadOnlyList<Guid> items, CancellationToken ct);

    /// <summary>Appends items at the end.</summary>
    /// <param name="playlistId">Playlist id.</param>
    /// <param name="items">Items.</param>
    /// <param name="ownerId">Owner.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task AppendAsync(Guid playlistId, IReadOnlyList<Guid> items, Guid ownerId, CancellationToken ct);

    /// <summary>Removes items.</summary>
    /// <param name="playlistId">Playlist id.</param>
    /// <param name="items">Items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RemoveAsync(Guid playlistId, IReadOnlyList<Guid> items, CancellationToken ct);

    /// <summary>Moves one item so it ends up at <paramref name="newIndex"/>.</summary>
    /// <param name="playlistId">Playlist id.</param>
    /// <param name="item">Item.</param>
    /// <param name="newIndex">Final index.</param>
    /// <param name="ownerId">Owner.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task MoveAsync(Guid playlistId, Guid item, int newIndex, Guid ownerId, CancellationToken ct);

    /// <summary>Replaces the whole content.</summary>
    /// <param name="playlistId">Playlist id.</param>
    /// <param name="items">Items.</param>
    /// <param name="ownerId">Owner.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReplaceAsync(Guid playlistId, IReadOnlyList<Guid> items, Guid ownerId, CancellationToken ct);
}
