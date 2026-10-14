using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SpotiSync.State;

/// <summary>Where a Spotify track stands relative to the library.</summary>
public enum TrackStatus
{
    /// <summary>Not matched yet (new track or override cleared).</summary>
    Pending = 0,

    /// <summary>In Jellyfin: matched automatically or via override.</summary>
    Matched = 1,

    /// <summary>Needs a look: a suggestion exists but is not confident.</summary>
    Review = 2,

    /// <summary>Not found in the library.</summary>
    Missing = 3,

    /// <summary>Ignored by the user.</summary>
    Ignored = 4,
}

/// <summary>Playlist sync mode.</summary>
public static class SyncModes
{
    /// <summary>Same order as Spotify, removes items that left the Spotify playlist.</summary>
    public const string Mirror = "mirror";

    /// <summary>Only appends new matches, never removes or reorders.</summary>
    public const string AddOnly = "add";

    /// <summary>Normalizes user input.</summary>
    /// <param name="mode">Mode.</param>
    /// <returns>A valid mode.</returns>
    public static string Normalize(string? mode) => string.Equals(mode, AddOnly, StringComparison.OrdinalIgnoreCase) ? AddOnly : Mirror;
}

/// <summary>One Spotify track inside a playlist and its match.</summary>
public sealed class TrackState
{
    /// <summary>Gets or sets the stable key: Spotify track id, or the uri for local files.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the Spotify track id (null for local files).</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the artists.</summary>
    public string[] Artists { get; set; } = [];

    /// <summary>Gets or sets the album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the duration in ms.</summary>
    public int Ms { get; set; }

    /// <summary>Gets or sets the ISRC.</summary>
    public string? Isrc { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public TrackStatus St { get; set; }

    /// <summary>Gets or sets the matched item (Matched) or the suggestion (Review).</summary>
    public Guid? Item { get; set; }

    /// <summary>Gets or sets the score of <see cref="Item"/>.</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets a display text for <see cref="Item"/>: "Title — Artist · Album".</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets a value indicating whether the status comes from a manual override.</summary>
    public bool Ov { get; set; }

    /// <summary>Gets or sets the override revision applied to this track (0 = none).</summary>
    public int OvRev { get; set; }
}

/// <summary>Cached tracks and matches of one playlist. One file per playlist.</summary>
public sealed class PlaylistState
{
    /// <summary>Gets or sets the Spotify playlist id ("liked" for Liked Songs).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the snapshot id the tracks were fetched at (Liked Songs: "total:latestAddedAt").</summary>
    public string? Snapshot { get; set; }

    /// <summary>Gets or sets the time tracks were fetched.</summary>
    public DateTime? FetchedAt { get; set; }

    /// <summary>Gets or sets the library generation the matches were computed against.</summary>
    public int LibGen { get; set; }

    /// <summary>Gets or sets the matcher version the matches were computed with.</summary>
    public int MatcherVersion { get; set; }

    /// <summary>Gets or sets the tracks in Spotify order.</summary>
    public List<TrackState> Tracks { get; set; } = [];
}

/// <summary>One Spotify playlist in the catalog, plus its sync settings and summary.</summary>
public sealed class CatalogEntry
{
    /// <summary>Id used for Liked Songs.</summary>
    public const string LikedId = "liked";

    /// <summary>Source value for playlists read from their public Spotify page (no account needed).</summary>
    public const string SourcePublic = "public";

    /// <summary>Source value for playlists imported from a Spotify data export file.</summary>
    public const string SourceFile = "file";

    /// <summary>Gets or sets the Spotify playlist id ("pub-..." / "file-..." for the sources above).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets where the playlist comes from: null = Spotify account, or <see cref="SourcePublic"/> / <see cref="SourceFile"/>.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the Spotify playlist id behind a public link.</summary>
    public string? SpotifyId { get; set; }

    /// <summary>Gets or sets a value indicating whether Spotify only returned part of the playlist (public pages stop at 100 songs).</summary>
    public bool Capped { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the owner display name.</summary>
    public string? OwnerName { get; set; }

    /// <summary>Gets or sets a value indicating whether the playlist is collaborative.</summary>
    public bool Collab { get; set; }

    /// <summary>Gets or sets a value indicating whether Spotify returns its items to us (owned or collaborative).</summary>
    public bool Readable { get; set; }

    /// <summary>Gets or sets the current snapshot id on Spotify.</summary>
    public string? Snapshot { get; set; }

    /// <summary>Gets or sets the number of tracks on Spotify.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets a small cover image url.</summary>
    public string? Image { get; set; }

    /// <summary>Gets or sets a value indicating whether the playlist is synced.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the mode (<see cref="SyncModes"/>).</summary>
    public string Mode { get; set; } = SyncModes.Mirror;

    /// <summary>Gets or sets the Jellyfin playlist id this syncs into.</summary>
    public Guid? JfId { get; set; }

    /// <summary>Gets or sets the hash of the item list last written to Jellyfin.</summary>
    public string? Written { get; set; }

    /// <summary>Gets or sets the matched count.</summary>
    public int Matched { get; set; }

    /// <summary>Gets or sets the need-a-look count.</summary>
    public int Review { get; set; }

    /// <summary>Gets or sets the not-found count.</summary>
    public int Missing { get; set; }

    /// <summary>Gets or sets the ignored count.</summary>
    public int Ignored { get; set; }

    /// <summary>Gets or sets the last time this playlist was synced.</summary>
    public DateTime? LastSync { get; set; }

    /// <summary>Gets or sets the last error for this playlist.</summary>
    public string? Error { get; set; }
}

/// <summary>The list of Spotify playlists and global sync bookkeeping.</summary>
public sealed class Catalog
{
    /// <summary>Gets or sets the Spotify user id.</summary>
    public string? MeId { get; set; }

    /// <summary>Gets or sets the Spotify display name.</summary>
    public string? MeName { get; set; }

    /// <summary>Gets or sets the library fingerprint at the last check.</summary>
    public string? LibFp { get; set; }

    /// <summary>Gets or sets the library generation (bumped whenever the fingerprint changes).</summary>
    public int LibGen { get; set; } = 1;

    /// <summary>Gets or sets when the library was last checked.</summary>
    public DateTime? LibCheckedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether Spotify rejected the "fields" parameter on /items.</summary>
    public bool NoFields { get; set; }

    /// <summary>Gets or sets the playlists (Liked Songs first).</summary>
    public List<CatalogEntry> Playlists { get; set; } = [];
}

/// <summary>A manual decision for one Spotify track, applied in every playlist on every sync.</summary>
public sealed class OverrideEntry
{
    /// <summary>Gets or sets the chosen library item.</summary>
    public Guid? Item { get; set; }

    /// <summary>Gets or sets a value indicating whether the track is ignored.</summary>
    public bool Ignore { get; set; }

    /// <summary>Gets or sets the revision at which this entry last changed.</summary>
    public int Rev { get; set; }

    /// <summary>Gets or sets the display text of <see cref="Item"/>.</summary>
    public string? Text { get; set; }
}

/// <summary>All overrides, keyed by track key.</summary>
public sealed class Overrides
{
    /// <summary>Gets or sets the revision counter (bumped on every change).</summary>
    public int Rev { get; set; }

    /// <summary>Gets or sets the entries.</summary>
    public Dictionary<string, OverrideEntry> Items { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Spotify tokens.</summary>
public sealed class AuthState
{
    /// <summary>Gets or sets the access token.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Gets or sets when the access token expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Gets or sets the refresh token (rotated by Spotify on refresh).</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Gets or sets the granted scopes.</summary>
    public string? Scope { get; set; }

    /// <summary>Gets or sets the deadline of a long 429 back-off, if any.</summary>
    public DateTimeOffset? RateLimitedUntil { get; set; }
}
