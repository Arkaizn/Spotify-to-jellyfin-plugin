using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SpotiSync.Spotify;

#pragma warning disable CS1591 // DTOs mirror the Spotify Web API; names are self-explanatory.

public sealed class SpUser
{
    public string? Id { get; set; }

    public string? DisplayName { get; set; }
}

public sealed class SpImage
{
    public string? Url { get; set; }

    public int? Width { get; set; }
}

public sealed class SpTotal
{
    public int Total { get; set; }
}

/// <summary>Simplified playlist from /me/playlists. Spotify renamed "tracks" to "items" in 2026; both are read.</summary>
public sealed class SpPlaylist
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public SpUser? Owner { get; set; }

    public bool Collaborative { get; set; }

    public string? SnapshotId { get; set; }

    public SpTotal? Items { get; set; }

    public SpTotal? Tracks { get; set; }

    public List<SpImage>? Images { get; set; }

    [JsonIgnore]
    public int Total => Items?.Total ?? Tracks?.Total ?? 0;
}

public sealed class SpArtist
{
    public string? Name { get; set; }
}

public sealed class SpAlbum
{
    public string? Name { get; set; }
}

public sealed class SpExternalIds
{
    public string? Isrc { get; set; }
}

public sealed class SpTrack
{
    public string? Id { get; set; }

    public string? Uri { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public int DurationMs { get; set; }

    public bool IsLocal { get; set; }

    public List<SpArtist>? Artists { get; set; }

    public SpAlbum? Album { get; set; }

    public SpExternalIds? ExternalIds { get; set; }
}

/// <summary>Entry of /playlists/{id}/items. The track sits in "item" (2026) or "track" (before).</summary>
public sealed class SpPlaylistEntry
{
    public SpTrack? Item { get; set; }

    public SpTrack? Track { get; set; }

    public bool IsLocal { get; set; }

    [JsonIgnore]
    public SpTrack? Value => Item ?? Track;
}

public sealed class SpSavedTrack
{
    public string? AddedAt { get; set; }

    public SpTrack? Track { get; set; }

    public SpTrack? Item { get; set; }
}

public sealed class SpPlaylistPage
{
    public List<SpPlaylist?>? Items { get; set; }

    public string? Next { get; set; }

    public int Total { get; set; }
}

public sealed class SpEntryPage
{
    public List<SpPlaylistEntry?>? Items { get; set; }

    public string? Next { get; set; }

    public int Total { get; set; }
}

public sealed class SpSavedPage
{
    public List<SpSavedTrack?>? Items { get; set; }

    public string? Next { get; set; }

    public int Total { get; set; }
}

public sealed class SpToken
{
    public string? AccessToken { get; set; }

    public int ExpiresIn { get; set; }

    public string? RefreshToken { get; set; }

    public string? Scope { get; set; }

    public string? Error { get; set; }

    public string? ErrorDescription { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SpUser))]
[JsonSerializable(typeof(SpPlaylistPage))]
[JsonSerializable(typeof(SpEntryPage))]
[JsonSerializable(typeof(SpSavedPage))]
[JsonSerializable(typeof(SpToken))]
public sealed partial class SpotifyJsonContext : JsonSerializerContext
{
}

/// <summary>Spotify asked us to back off for longer than we are willing to wait.</summary>
public sealed class SpotifyRateLimitedException : Exception
{
    public SpotifyRateLimitedException(DateTimeOffset until)
        : base("Spotify rate limit until " + until.ToString("u", System.Globalization.CultureInfo.InvariantCulture))
    {
        Until = until;
    }

    public DateTimeOffset Until { get; }
}

/// <summary>Not connected, or the refresh token was revoked.</summary>
public sealed class SpotifyAuthException : Exception
{
    public SpotifyAuthException(string message)
        : base(message)
    {
    }
}

/// <summary>Any other Spotify API error.</summary>
public sealed class SpotifyApiException : Exception
{
    public SpotifyApiException(int status, string message)
        : base(message)
    {
        Status = status;
    }

    public int Status { get; }
}

#pragma warning restore CS1591
