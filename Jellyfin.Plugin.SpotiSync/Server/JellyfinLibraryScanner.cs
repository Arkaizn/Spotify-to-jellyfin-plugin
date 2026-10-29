using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SpotiSync.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SpotiSync.Server;

/// <summary>
/// Has Jellyfin refresh only the folders that changed (what its real-time monitor does), instead of
/// scanning whole libraries.
/// </summary>
public sealed class JellyfinLibraryScanner : ILibraryScanner
{
    /// <summary>Key of Jellyfin's "Scan Media Library" task.</summary>
    private const string FullScanTaskKey = "RefreshLibrary";

    private readonly ILibraryManager _library;
    private readonly IFileSystem _fileSystem;
    private readonly Func<ITaskManager?> _tasks;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLibraryScanner"/> class.</summary>
    /// <param name="library">Library manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="tasks">Task manager (looked up when needed).</param>
    public JellyfinLibraryScanner(ILibraryManager library, IFileSystem fileSystem, Func<ITaskManager?> tasks)
    {
        _library = library;
        _fileSystem = fileSystem;
        _tasks = tasks;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScanLibrary> Libraries()
    {
        var list = new List<ScanLibrary>();
        foreach (var vf in _library.GetVirtualFolders())
        {
            if (!string.IsNullOrEmpty(vf.ItemId) && vf.Locations is { Length: > 0 })
            {
                list.Add(new ScanLibrary(vf.ItemId, vf.Name, vf.CollectionType == CollectionTypeOptions.music, vf.Locations));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public DateTime? LastFullScanUtc()
    {
        var last = _tasks()?.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == FullScanTaskKey)?.LastExecutionResult;
        return last is { Status: TaskCompletionStatus.Completed } ? last.StartTimeUtc : null;
    }

    /// <inheritdoc />
    public async Task<int> RefreshAsync(ScanLibrary library, IReadOnlyList<string> dirs, IProgress<double> progress, CancellationToken ct)
    {
        if (!Guid.TryParse(library.Id, out var libId) || _library.GetItemById(libId) is not Folder libFolder)
        {
            return 0;
        }

        var roots = library.Locations.Select(Path.TrimEndingDirectorySeparator).ToHashSet(StringComparer.Ordinal);
        var done = new List<string>(); // refreshed with everything below
        var topDone = new HashSet<string>(StringComparer.Ordinal);
        int refreshed = 0;
        for (int i = 0; i < dirs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var dir = dirs[i];
            if (done.Exists(d => ChangeFinder.IsUnder(dir, d)))
            {
                continue;
            }

            int index = i;
            var part = new InlineProgress(p => progress.Report(100.0 * (index + (Math.Clamp(p, 0, 100) / 100)) / dirs.Count));

            // The closest folder Jellyfin already knows, below the library's own top folders.
            var (known, knownPath) = Known(dir, roots);
            if (known is not null)
            {
                await known.ValidateChildren(part, Options(), recursive: true, allowRemoveRoot: false, ct).ConfigureAwait(false);
                done.Add(knownPath!);
                refreshed++;
                continue;
            }

            // Something changed right at the top of the library (e.g. a new artist folder). Add and remove
            // top-level entries without walking the whole library, then fully refresh only the new ones.
            // Jellyfin keeps a folder item for each library path below the library itself; that one holds
            // the top-level entries.
            var root = roots.FirstOrDefault(r => ChangeFinder.IsUnder(dir, r));
            if (root is not null && topDone.Add(root))
            {
                var top = _library.FindByPath(root, true) as Folder ?? libFolder;
                var before = top.Children.Select(c => c.Id).ToHashSet();
                await top.ValidateChildren(part, Options(), recursive: false, allowRemoveRoot: false, ct).ConfigureAwait(false);
                refreshed++;
                foreach (var added in top.Children.OfType<Folder>().Where(c => !before.Contains(c.Id)).ToList())
                {
                    await added.ValidateChildren(part, Options(), recursive: true, allowRemoveRoot: false, ct).ConfigureAwait(false);
                    refreshed++;
                    if (!string.IsNullOrEmpty(added.Path))
                    {
                        done.Add(added.Path);
                    }
                }
            }

            // A folder that only became known through the step above.
            if (!roots.Contains(dir) && !done.Exists(d => ChangeFinder.IsUnder(dir, d)) && Known(dir, roots) is ({ } now, { } nowPath))
            {
                await now.ValidateChildren(part, Options(), recursive: true, allowRemoveRoot: false, ct).ConfigureAwait(false);
                done.Add(nowPath);
                refreshed++;
            }
        }

        progress.Report(100);
        return refreshed;
    }

    private (Folder? Folder, string? Path) Known(string dir, HashSet<string> roots)
    {
        for (string? p = dir; p is not null && !roots.Contains(p); p = Path.GetDirectoryName(p))
        {
            if (_library.FindByPath(p, true) is Folder f and not CollectionFolder)
            {
                return (f, p);
            }
        }

        return (null, null);
    }

    private MetadataRefreshOptions Options() => new(new DirectoryService(_fileSystem));
}
