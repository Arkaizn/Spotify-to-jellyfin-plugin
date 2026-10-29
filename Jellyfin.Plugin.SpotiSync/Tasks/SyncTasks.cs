using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SpotiSync.Server;
using Jellyfin.Plugin.SpotiSync.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SpotiSync.Tasks;

/// <summary>Scheduled task: full sync (default every 6 hours).</summary>
public class SyncPlaylistsTask : IScheduledTask
{
    /// <summary>Task key.</summary>
    public const string TaskKey = "SpotiSyncSyncPlaylists";

    private readonly SpotiSyncService _service;

    /// <summary>Initializes a new instance of the <see cref="SyncPlaylistsTask"/> class.</summary>
    /// <param name="service">Service.</param>
    public SyncPlaylistsTask(SpotiSyncService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "SpotiSync: sync playlists";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Fetches changed Spotify playlists, matches new tracks and updates the Jellyfin playlists.";

    /// <inheritdoc />
    public string Category => "SpotiSync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.Engine.RunAsync(SyncMode.Full, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks,
        };
    }
}

/// <summary>Scheduled task: rematch cached tracks against the library without calling Spotify.</summary>
public class RematchTask : IScheduledTask
{
    /// <summary>Task key.</summary>
    public const string TaskKey = "SpotiSyncRematch";

    private readonly SpotiSyncService _service;

    /// <summary>Initializes a new instance of the <see cref="RematchTask"/> class.</summary>
    /// <param name="service">Service.</param>
    public RematchTask(SpotiSyncService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "SpotiSync: rematch only";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Rematches the cached Spotify tracks against the library and updates playlists. Does not contact Spotify.";

    /// <inheritdoc />
    public string Category => "SpotiSync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.Engine.RunAsync(SyncMode.RematchOnly, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

/// <summary>Quick scan for new files in the chosen libraries, then rematch (no Spotify calls).</summary>
public class ScanLibraryTask : IScheduledTask
{
    /// <summary>Task key.</summary>
    public const string TaskKey = "SpotiSyncScanLibrary";

    private readonly SpotiSyncService _service;

    /// <summary>Initializes a new instance of the <see cref="ScanLibraryTask"/> class.</summary>
    /// <param name="service">Service.</param>
    public ScanLibraryTask(SpotiSyncService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "SpotiSync: scan for new files and rematch";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Looks for new, changed and removed files in the libraries chosen in SpotiSync's settings (by file date), lets Jellyfin update only those folders, then matches the Spotify tracks again.";

    /// <inheritdoc />
    public string Category => "SpotiSync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.Engine.RunAsync(SyncMode.ScanLibrary, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
