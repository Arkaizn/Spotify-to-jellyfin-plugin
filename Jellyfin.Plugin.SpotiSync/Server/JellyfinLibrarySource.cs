using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SpotiSync.Matching;
using Jellyfin.Plugin.SpotiSync.Sync;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SpotiSync.Server;

/// <summary>Reads audio items through <see cref="ILibraryManager"/> (no HTTP, no user context).</summary>
public sealed class JellyfinLibrarySource : ILibrarySource
{
    private const int BatchSize = 1000;
    private readonly ILibraryManager _library;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLibrarySource"/> class.</summary>
    /// <param name="library">Library manager.</param>
    public JellyfinLibrarySource(ILibraryManager library)
    {
        _library = library;
    }

    /// <inheritdoc />
    public LibraryFingerprint GetFingerprint()
    {
        int count = _library.GetCount(AudioQuery());
        var q = AudioQuery();
        q.Limit = 1;
        q.OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)];
        var newest = _library.GetItemList(q);
        return new LibraryFingerprint(count, newest.Count > 0 ? newest[0].DateCreated.Ticks : 0);
    }

    /// <inheritdoc />
    public int CountChangedSince(DateTime sinceUtc)
    {
        var q = AudioQuery();
        q.MinDateLastSaved = sinceUtc;
        return _library.GetCount(q);
    }

    /// <inheritdoc />
    public IEnumerable<LibraryRecord> ReadAll(CancellationToken ct)
    {
        // Ids first (16 bytes each), then items in batches so only BatchSize full BaseItems are
        // alive at a time. Batching by id is stable even if the library changes meanwhile.
        var ids = _library.GetItemIds(AudioQuery());
        for (int start = 0; start < ids.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, ids.Count - start);
            var batch = new Guid[n];
            for (int i = 0; i < n; i++)
            {
                batch[i] = ids[start + i];
            }

            var q = AudioQuery();
            q.ItemIds = batch;
            q.SkipDeserialization = true; // only scalar columns are needed: ~17% faster in 12.2
            foreach (var item in _library.GetItemList(q))
            {
                if (item is Audio a)
                {
                    yield return new LibraryRecord(a.Id, a.Name, a.Artists, a.AlbumArtists, a.Album, a.RunTimeTicks, Isrc(a));
                }
            }
        }
    }

    /// <summary>Display text for an item, or null when it no longer exists.</summary>
    /// <param name="id">Item id.</param>
    /// <returns>"Title — Artist · Album".</returns>
    public string? Describe(Guid id)
    {
        if (_library.GetItemById(id) is not Audio a)
        {
            return null;
        }

        var s = a.Name ?? string.Empty;
        if (a.Artists is { Count: > 0 })
        {
            s += " — " + string.Join(", ", a.Artists);
        }

        if (!string.IsNullOrEmpty(a.Album))
        {
            s += " · " + a.Album;
        }

        return s;
    }

    private static string? Isrc(BaseItem item)
    {
        // Jellyfin 12.2 has no ISRC field; use one if a metadata plugin stored it as a provider id.
        var ids = item.ProviderIds;
        if (ids is null || ids.Count == 0)
        {
            return null;
        }

        return ids.TryGetValue("ISRC", out var v) || ids.TryGetValue("Isrc", out v) || ids.TryGetValue("isrc", out v) ? v : null;
    }

    private static InternalItemsQuery AudioQuery() => new()
    {
        IncludeItemTypes = [BaseItemKind.Audio],
        Recursive = true,
        IsVirtualItem = false,
        DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
    };
}
