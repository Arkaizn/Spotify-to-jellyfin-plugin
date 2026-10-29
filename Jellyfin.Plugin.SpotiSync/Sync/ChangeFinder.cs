using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;

namespace Jellyfin.Plugin.SpotiSync.Sync;

/// <summary>What <see cref="ChangeFinder.Find"/> found.</summary>
/// <param name="Dirs">Folders with something new, changed or removed inside, shallowest first.</param>
/// <param name="DirsChecked">Folders looked at.</param>
/// <param name="FilesChecked">Files looked at.</param>
public sealed record ChangeScan(List<string> Dirs, int DirsChecked, int FilesChecked);

/// <summary>
/// Finds the folders of a library that changed since a point in time, by reading only file system
/// dates (no Jellyfin database work). A folder counts as changed when its own date is newer (an entry
/// was added, removed or renamed, which also catches files moved in with their old date) or when a file
/// directly inside it is newer (edited tags).
/// </summary>
public static class ChangeFinder
{
    /// <summary>Finds changed folders below <paramref name="roots"/>.</summary>
    /// <param name="roots">Library folders.</param>
    /// <param name="sinceUtc">Changes after this time count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The changed folders.</returns>
    public static ChangeScan Find(IReadOnlyList<string> roots, DateTime sinceUtc, CancellationToken ct)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        int dirs = 0, files = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        foreach (var root in roots)
        {
            var top = Path.TrimEndingDirectorySeparator(root);
            if (!Directory.Exists(top))
            {
                continue;
            }

            dirs++;
            if (Directory.GetLastWriteTimeUtc(top) > sinceUtc)
            {
                changed.Add(top);
            }

            // Only changed entries allocate a path string; unchanged ones cost one stat each.
            var entries = new FileSystemEnumerable<(bool IsDir, string? Changed)>(
                top,
                (ref FileSystemEntry e) => (e.IsDirectory, e.LastWriteTimeUtc.UtcDateTime > sinceUtc ? (e.IsDirectory ? e.ToFullPath() : e.Directory.ToString()) : null),
                options)
            {
                // Skip what Jellyfin skips as well: dot folders and NAS metadata folders.
                ShouldIncludePredicate = (ref FileSystemEntry e) => !Ignored(e.FileName),
                ShouldRecursePredicate = (ref FileSystemEntry e) => !Ignored(e.FileName),
            };

            foreach (var (isDir, path) in entries)
            {
                if (((dirs + files) & 4095) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (isDir)
                {
                    dirs++;
                }
                else
                {
                    files++;
                }

                if (path is not null)
                {
                    changed.Add(path);
                }
            }
        }

        var list = new List<string>(changed);
        list.Sort((a, b) => Depth(a) != Depth(b) ? Depth(a).CompareTo(Depth(b)) : string.CompareOrdinal(a, b));
        return new ChangeScan(list, dirs, files);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    /// <param name="path">Path.</param>
    /// <param name="folder">Folder.</param>
    /// <returns>True when inside.</returns>
    public static bool IsUnder(string path, string folder)
        => path.Length >= folder.Length
           && path.StartsWith(folder, StringComparison.Ordinal)
           && (path.Length == folder.Length || path[folder.Length] == Path.DirectorySeparatorChar || folder.EndsWith(Path.DirectorySeparatorChar));

    private static int Depth(string path)
    {
        int n = 0;
        foreach (var c in path)
        {
            if (c == Path.DirectorySeparatorChar)
            {
                n++;
            }
        }

        return n;
    }

    private static bool Ignored(ReadOnlySpan<char> name)
        => name.StartsWith(".") || name.Equals("@eaDir", StringComparison.OrdinalIgnoreCase) || name.Equals("#recycle", StringComparison.OrdinalIgnoreCase);
}
