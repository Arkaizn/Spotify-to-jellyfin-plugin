using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SpotiSync.Configuration;

/// <summary>
/// Settings stored by Jellyfin as XML. Tokens and sync state live in separate files under the
/// plugin data folder so a token refresh never rewrites this file.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the Spotify app client id (PKCE, no secret).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user that owns the synced playlists.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// Gets or sets the public URL of this Jellyfin server (e.g. https://jellyfin.example.com).
    /// Empty = derive it from the browser request.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the mode new playlists start with: "mirror" or "add".</summary>
    public string DefaultMode { get; set; } = "mirror";

    /// <summary>Gets or sets a value indicating whether each song is written only once per Jellyfin playlist.</summary>
    public bool RemoveDuplicates { get; set; } = true;

    /// <summary>Gets or sets a prefix put in front of Jellyfin playlist names (empty = same name).</summary>
    public string NamePrefix { get; set; } = string.Empty;
}
