using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SpotiSync.Sync;

namespace Jellyfin.Plugin.SpotiSync.Api;

#pragma warning disable CS1591 // plain transfer objects

public sealed class UserDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class SettingsDto
{
    public string ClientId { get; set; } = string.Empty;

    public Guid OwnerUserId { get; set; }

    public string PublicUrl { get; set; } = string.Empty;

    public string DefaultMode { get; set; } = "mirror";

    public string NamePrefix { get; set; } = string.Empty;

    // read-only fields below
    public string? RedirectUri { get; set; }

    public bool Connected { get; set; }

    public string? SpotifyUser { get; set; }

    public DateTimeOffset? RateLimitedUntil { get; set; }

    public List<UserDto>? Users { get; set; }
}

public sealed class StatusDto
{
    public bool Running { get; set; }

    public string? Mode { get; set; }

    public string? Current { get; set; }

    public double Progress { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? LastResult { get; set; }

    public long LogSeq { get; set; }

    public List<LogLine>? Log { get; set; }
}

public sealed class PlaylistPatch
{
    public bool? Enabled { get; set; }

    public string? Mode { get; set; }

    /// <summary>Gets or sets the Jellyfin playlist to merge into ("" = own playlist).</summary>
    public string? Target { get; set; }
}

/// <summary>Override actions: "pick" (with item), "ignore", "clear", "accept" (take the suggestion).</summary>
public sealed class OverrideRequest
{
    public string Key { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public Guid? Item { get; set; }

    /// <summary>Playlist the action came from (used by "accept" to find the suggestion).</summary>
    public string? Playlist { get; set; }
}

public sealed class AddPublicRequest
{
    public string? Url { get; set; }
}

public sealed class ImportResultDto
{
    public int Playlists { get; set; }

    public int Tracks { get; set; }

    public List<string> Names { get; set; } = [];
}

public sealed class SyncRequest
{
    public string? Mode { get; set; }
}

public sealed class LoginDto
{
    public string Url { get; set; } = string.Empty;
}

public sealed class SearchResultDto
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public int Sec { get; set; }

    public string Text { get; set; } = string.Empty;
}

#pragma warning restore CS1591
