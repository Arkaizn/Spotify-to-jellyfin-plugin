using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Jellyfin.Plugin.SpotiSync.State;

namespace Jellyfin.Plugin.SpotiSync.Sync;

/// <summary>Housekeeping of <see cref="SyncDef"/>s.</summary>
public static class SyncDefs
{
    /// <summary>Creates a new sync id.</summary>
    /// <returns>Id.</returns>
    public static string NewId() => "s-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

    /// <summary>
    /// Brings the catalog to the sync model: every playlist that was switched on without belonging to a
    /// sync gets one (playlists sharing a "Jellyfin playlist" name share one), and sources that no
    /// longer exist are dropped. Pure bookkeeping, no I/O.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <returns>True when something changed.</returns>
    public static bool Ensure(Catalog catalog)
    {
        bool changed = false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in catalog.Playlists)
        {
            ids.Add(e.Id);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in catalog.Syncs)
        {
            changed |= s.Sources.RemoveAll(id => !ids.Contains(id)) > 0;
            used.UnionWith(s.Sources);
        }

        foreach (var e in catalog.Playlists)
        {
            if (!e.Enabled || !e.Readable || used.Contains(e.Id))
            {
                continue;
            }

            var target = string.IsNullOrWhiteSpace(e.Target) ? null : e.Target.Trim();
            var sync = target is null ? null : catalog.Syncs.Find(s => s.NaturalOrder && string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
            if (sync is null)
            {
                sync = new SyncDef
                {
                    Id = NewId(),
                    Name = target ?? e.Name,
                    Mode = SyncModes.Normalize(e.Mode),
                    NaturalOrder = target is not null,
                    JfId = e.JfId,
                };
                catalog.Syncs.Add(sync);
            }
            else if (e.Mode == SyncModes.AddOnly)
            {
                sync.Mode = SyncModes.AddOnly;
            }

            sync.Sources.Add(e.Id);
            used.Add(e.Id);
            e.Enabled = false; // the sync owns it now
            changed = true;
        }

        return changed;
    }

    /// <summary>The name the Jellyfin playlist of a sync gets.</summary>
    /// <param name="sync">Sync.</param>
    /// <param name="prefix">Name prefix from the settings.</param>
    /// <returns>Name.</returns>
    public static string JellyfinName(SyncDef sync, string? prefix)
        => sync.NoPrefix || string.IsNullOrEmpty(prefix) ? sync.Name : prefix + sync.Name;

    /// <summary>
    /// Points a sync at an existing Jellyfin playlist, by id. The sync takes the playlist's name (so nothing
    /// gets renamed) and the next run writes into it as if it were new: mirror replaces what is there,
    /// add only appends the songs that are missing.
    /// </summary>
    /// <param name="sync">Sync.</param>
    /// <param name="playlist">The picked playlist.</param>
    /// <param name="prefix">Name prefix from the settings.</param>
    public static void LinkTo(SyncDef sync, JfPlaylistInfo playlist, string? prefix)
    {
        bool prefixed = !string.IsNullOrEmpty(prefix) && playlist.Name.StartsWith(prefix, StringComparison.Ordinal) && playlist.Name.Length > prefix.Length;
        sync.Name = prefixed ? playlist.Name[prefix!.Length..] : playlist.Name;
        sync.NoPrefix = !prefixed && !string.IsNullOrEmpty(prefix);
        sync.JfId = playlist.Id;
        sync.WrittenName = playlist.Name;
        sync.Written = null;
    }

    /// <summary>Makes a sync write into a new Jellyfin playlist called <paramref name="name"/> on the next run.</summary>
    /// <param name="sync">Sync.</param>
    /// <param name="name">Name.</param>
    public static void Unlink(SyncDef sync, string name)
    {
        sync.Name = name;
        sync.NoPrefix = false;
        sync.JfId = null;
        sync.WrittenName = null;
        sync.Written = null;
    }

    /// <summary>The sources of a sync in the order they are written.</summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="sync">Sync.</param>
    /// <returns>Entries.</returns>
    public static List<CatalogEntry> OrderedSources(Catalog catalog, SyncDef sync)
    {
        var list = new List<CatalogEntry>(sync.Sources.Count);
        foreach (var id in sync.Sources)
        {
            var e = catalog.Playlists.Find(p => p.Id == id);
            if (e is not null)
            {
                list.Add(e);
            }
        }

        if (sync.NaturalOrder)
        {
            list.Sort((a, b) => SyncEngine.NaturalCompare(a.Name, b.Name));
        }

        return list;
    }
}
