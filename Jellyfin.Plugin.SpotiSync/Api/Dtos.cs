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

    public bool RemoveDuplicates { get; set; }

    /// <summary>Gets or sets the library ids the scan for new files covers.</summary>
    public List<string>? ScanLibraries { get; set; }

    /// <summary>Gets or sets how often the scan for new files runs by itself, in minutes (0 = only by hand).</summary>
    public int ScanEvery { get; set; }

    // read-only fields below
    public List<LibraryDto>? Libraries { get; set; }

    public string? RedirectUri { get; set; }

    public bool Connected { get; set; }

    public string? SpotifyUser { get; set; }

    public DateTimeOffset? RateLimitedUntil { get; set; }

    public List<UserDto>? Users { get; set; }
}

public sealed class LibraryDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Music { get; set; }

    public bool Selected { get; set; }
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

public sealed class SyncEdit
{
    public string? Name { get; set; }

    public string? Mode { get; set; }

    public bool? Enabled { get; set; }

    /// <summary>Gets or sets an existing Jellyfin playlist to write into (null = keep).</summary>
    public Guid? JfId { get; set; }

    /// <summary>Gets or sets a value indicating whether to write into a new Jellyfin playlist called <see cref="Name"/> instead.</summary>
    public bool NewPlaylist { get; set; }
}

/// <summary>A Jellyfin playlist for the picker, and the sync already writing into it.</summary>
public sealed class JfPlaylistDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }

    public string? UsedBy { get; set; }

    public string? UsedByName { get; set; }
}

/// <summary>Adds a source to a sync: a public link, or an existing catalog entry.</summary>
public sealed class SourceAdd
{
    public string? Url { get; set; }

    public string? EntryId { get; set; }
}

public sealed class SourceRefDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Source { get; set; }

    public int Total { get; set; }

    public bool Capped { get; set; }

    public string? Error { get; set; }
}

/// <summary>One row of the combined song list of a sync.</summary>
public sealed class SyncTrackDto
{
    public string Key { get; set; } = string.Empty;

    public string? Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string[] Artists { get; set; } = [];

    public string? Album { get; set; }

    public State.TrackStatus St { get; set; }

    public Guid? Item { get; set; }

    public double Score { get; set; }

    public string? Text { get; set; }

    public bool Ov { get; set; }

    /// <summary>Index into <see cref="SyncTracksDto.Sources"/>.</summary>
    public int Src { get; set; }

    /// <summary>1-based position of the first row with the same song (0 = not a duplicate).</summary>
    public int DupOf { get; set; }
}

public sealed class SyncTracksDto
{
    public List<SourceRefDto> Sources { get; set; } = [];

    public List<SyncTrackDto> Tracks { get; set; } = [];
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
