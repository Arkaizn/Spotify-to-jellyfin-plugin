using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.SpotiSync.Configuration;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpotiSync.Server;

/// <summary>
/// Composition root for the plugin (singleton). Constructing it allocates a handful of small
/// objects; nothing runs until Jellyfin's scheduler or the dashboard calls in.
/// </summary>
public sealed class SpotiSyncService : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="SpotiSyncService"/> class.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="httpFactory">HTTP client factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="playlistManager">Playlist manager.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public SpotiSyncService(
        IApplicationPaths paths,
        IHttpClientFactory httpFactory,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        ILoggerFactory loggerFactory)
    {
        // Under plugins/configurations so the data survives plugin updates.
        Store = new StateStore(Path.Combine(paths.PluginConfigurationsPath, "SpotiSync"));
        Spotify = new SpotifyClient(httpFactory, NamedClient.Default, Store, () => Config.ClientId.Trim(), loggerFactory.CreateLogger<SpotifyClient>());
        Library = new JellyfinLibrarySource(libraryManager);
        Cache = new IndexCache();
        Target = new JellyfinPlaylistTarget(playlistManager, libraryManager);
        Engine = new SyncEngine(
            Spotify,
            Library,
            Target,
            Store,
            Cache,
            () => new SyncSettings(Config.OwnerUserId, Config.NamePrefix ?? string.Empty, Config.DefaultMode, Config.RemoveDuplicates),
            loggerFactory.CreateLogger<SyncEngine>(),
            Public = new SpotifyPublicClient(httpFactory, NamedClient.Default));
    }

    /// <summary>Gets the state store.</summary>
    public StateStore Store { get; }

    /// <summary>Gets the Spotify client.</summary>
    public SpotifyClient Spotify { get; }

    /// <summary>Gets the public playlist reader.</summary>
    public SpotifyPublicClient Public { get; }

    /// <summary>Gets the library source.</summary>
    public JellyfinLibrarySource Library { get; }

    /// <summary>Gets the Jellyfin playlist writer.</summary>
    public JellyfinPlaylistTarget Target { get; }

    /// <summary>Gets the index cache.</summary>
    public IndexCache Cache { get; }

    /// <summary>Gets the sync engine.</summary>
    public SyncEngine Engine { get; }

    /// <summary>Gets the current configuration.</summary>
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Dispose()
    {
        Engine.Dispose();
        Cache.Dispose();
        Spotify.Dispose();
        Store.Dispose();
    }
}
