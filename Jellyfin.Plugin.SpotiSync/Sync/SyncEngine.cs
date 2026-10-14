using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SpotiSync.Matching;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpotiSync.Sync;

/// <summary>Settings the engine reads at the start of each run.</summary>
/// <param name="OwnerUserId">Jellyfin user owning the playlists.</param>
/// <param name="NamePrefix">Prefix for Jellyfin playlist names.</param>
/// <param name="DefaultMode">Mode for newly discovered playlists.</param>
public sealed record SyncSettings(Guid OwnerUserId, string NamePrefix, string DefaultMode);

/// <summary>Counters of one run (also used by tests and the benchmark).</summary>
public sealed class SyncOutcome
{
    /// <summary>Gets or sets a value indicating whether the run was skipped because one was already running.</summary>
    public bool AlreadyRunning { get; set; }

    /// <summary>Gets or sets the number of playlists processed.</summary>
    public int Playlists { get; set; }

    /// <summary>Gets or sets the number of playlists re-downloaded from Spotify.</summary>
    public int Fetched { get; set; }

    /// <summary>Gets or sets the number of tracks scored.</summary>
    public int Matched { get; set; }

    /// <summary>Gets or sets the number of Jellyfin write calls.</summary>
    public int Writes { get; set; }

    /// <summary>Gets or sets the number of state files written.</summary>
    public int StateWrites { get; set; }

    /// <summary>Gets or sets a value indicating whether the library index was needed.</summary>
    public bool IndexUsed { get; set; }

    /// <summary>Gets or sets the Spotify problem, if any.</summary>
    public string? SpotifyError { get; set; }
}

/// <summary>
/// The sync pipeline: Spotify → cached track lists → incremental matching → minimal playlist
/// writes. One run at a time; single threaded; yields between playlists.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly ISpotifySource _spotify;
    private readonly ILibrarySource _library;
    private readonly IPlaylistTarget _target;
    private readonly StateStore _store;
    private readonly IndexCache _cache;
    private readonly Func<SyncSettings> _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SyncEngine"/> class.</summary>
    /// <param name="spotify">Spotify.</param>
    /// <param name="library">Library.</param>
    /// <param name="target">Playlist writer.</param>
    /// <param name="store">State store.</param>
    /// <param name="cache">Index cache.</param>
    /// <param name="settings">Settings accessor.</param>
    /// <param name="logger">Logger.</param>
    public SyncEngine(ISpotifySource spotify, ILibrarySource library, IPlaylistTarget target, StateStore store, IndexCache cache, Func<SyncSettings> settings, ILogger logger)
    {
        _spotify = spotify;
        _library = library;
        _target = target;
        _store = store;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _runLock.Dispose();

    /// <summary>Gets the live status.</summary>
    public SyncStatus Status { get; } = new();

    /// <summary>Gets a value indicating whether a run is in progress.</summary>
    public bool IsRunning => Status.Running;

    /// <summary>Runs a sync unless one is already running.</summary>
    /// <param name="mode">Mode.</param>
    /// <param name="progress">Progress (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Counters.</returns>
    public async Task<SyncOutcome> RunAsync(SyncMode mode, IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new SyncOutcome { AlreadyRunning = true };
        }

        var outcome = new SyncOutcome();
        var sw = Stopwatch.StartNew();
        Status.Start(mode);
        string result = "failed";
        try
        {
            await RunCoreAsync(mode, outcome, progress, ct).ConfigureAwait(false);
            result = string.Format(
                CultureInfo.InvariantCulture,
                "{0} playlists, {1} downloaded, {2} tracks matched, {3} playlist writes in {4:0.0}s{5}",
                outcome.Playlists,
                outcome.Fetched,
                outcome.Matched,
                outcome.Writes,
                sw.Elapsed.TotalSeconds,
                outcome.SpotifyError is null ? string.Empty : " (Spotify: " + outcome.SpotifyError + ")");
            _logger.LogInformation("SpotiSync {Mode} finished: {Result}", mode, result);
            Status.Log("info", (mode == SyncMode.Full ? "Sync" : "Rematch") + " done: " + result);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            result = "cancelled";
            Status.Log("warn", "Sync cancelled");
            throw;
        }
        catch (Exception e)
        {
            result = "failed: " + e.Message;
            _logger.LogError(e, "SpotiSync sync failed");
            Status.Log("error", "Sync failed: " + e.Message);
            throw;
        }
        finally
        {
            _cache.ReleaseLater();
            _spotify.ReleaseCache();
            Status.Finish(result);
            _runLock.Release();
        }
    }

    private async Task RunCoreAsync(SyncMode mode, SyncOutcome outcome, IProgress<double>? progress, CancellationToken ct)
    {
        var settings = _settings();
        var catalog = await _store.LoadCatalogAsync(ct).ConfigureAwait(false);
        var overrides = await _store.LoadOverridesAsync(ct).ConfigureAwait(false);
        bool catalogDirty = false;
        var ctx = new RunContext(catalog, overrides, settings, outcome);

        // 1. Spotify playlist list: one listing per sync.
        if (mode == SyncMode.Full)
        {
            ctx.SpotifyOk = await _spotify.IsConnectedAsync(ct).ConfigureAwait(false);
            if (!ctx.SpotifyOk)
            {
                outcome.SpotifyError = "not connected";
            }
            else if (await _spotify.GetRateLimitDeadlineAsync(ct).ConfigureAwait(false) is { } until)
            {
                ctx.SpotifyOk = false;
                outcome.SpotifyError = "rate limited until " + until.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) + ", using cached tracks";
            }
            else
            {
                try
                {
                    if (catalog.MeId is null)
                    {
                        var me = await _spotify.GetMeAsync(ct).ConfigureAwait(false);
                        catalog.MeId = me.Id;
                        catalog.MeName = me.DisplayName;
                        catalogDirty = true;
                    }

                    var lists = await _spotify.GetPlaylistsAsync(ct).ConfigureAwait(false);
                    catalogDirty |= MergeCatalog(catalog, lists, settings);
                }
                catch (Exception e) when (e is SpotifyRateLimitedException or SpotifyAuthException or SpotifyApiException or System.Net.Http.HttpRequestException)
                {
                    ctx.SpotifyOk = false;
                    outcome.SpotifyError = e.Message;
                    Status.Log("warn", "Spotify: " + e.Message);
                }
            }
        }

        // 2. Library fingerprint: a few cheap queries, no item loading.
        var fp = _library.GetFingerprint().ToString();
        bool libChanged = !string.Equals(fp, catalog.LibFp, StringComparison.Ordinal)
            || catalog.LibCheckedAt is null
            || _library.CountChangedSince(catalog.LibCheckedAt.Value) > 0;
        if (libChanged)
        {
            catalog.LibGen++;
            catalog.LibFp = fp;
            catalog.LibCheckedAt = DateTime.UtcNow;
            catalogDirty = true;
        }

        // 3. Playlists.
        int startOverrides = overrides.Rev;
        for (int pass = 0; pass < 2; pass++)
        {
            var enabled = catalog.Playlists.FindAll(p => p.Enabled && p.Readable);
            for (int i = 0; i < enabled.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                var entry = enabled[i];
                double pct = 100.0 * i / Math.Max(1, enabled.Count);
                Status.Step(entry.Name, pct);
                progress?.Report(pct);
                try
                {
                    catalogDirty |= await SyncPlaylistAsync(entry, ctx, pass == 0, ct).ConfigureAwait(false);
                    if (entry.Error is not null)
                    {
                        entry.Error = null;
                        catalogDirty = true;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning(e, "SpotiSync: playlist {Name} failed", entry.Name);
                    Status.Log("error", entry.Name + ": " + e.Message);
                    entry.Error = e.Message;
                    catalogDirty = true;
                }

                if (pass == 0)
                {
                    outcome.Playlists++;
                }
            }

            // Overrides changed while we were running: apply them now (no Spotify, incremental).
            var latest = await _store.LoadOverridesAsync(ct).ConfigureAwait(false);
            if (latest.Rev == startOverrides)
            {
                break;
            }

            ctx.Overrides = latest;
            startOverrides = latest.Rev;
            ctx.SpotifyOk = false;
        }

        if (catalogDirty)
        {
            await _store.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // The dashboard may have toggled playlists while we ran: keep the user's choices.
                var disk = await _store.LoadCatalogAsync(ct).ConfigureAwait(false);
                foreach (var d in disk.Playlists)
                {
                    var mine = catalog.Playlists.Find(p => p.Id == d.Id);
                    if (mine is not null)
                    {
                        mine.Enabled = d.Enabled;
                        mine.Mode = d.Mode;
                    }
                }

                await _store.SaveCatalogAsync(catalog, ct).ConfigureAwait(false);
            }
            finally
            {
                _store.Gate.Release();
            }

            outcome.StateWrites++;
        }

        progress?.Report(100);
    }

    /// <summary>Fetch (if changed) → match (incrementally) → write (if different) for one playlist.</summary>
    /// <returns>True when the catalog entry changed.</returns>
    private async Task<bool> SyncPlaylistAsync(CatalogEntry entry, RunContext ctx, bool firstPass, CancellationToken ct)
    {
        bool catalogDirty = false;
        bool dirty = false;
        var state = await _store.LoadPlaylistAsync(entry.Id, ct).ConfigureAwait(false);
        if (state is null)
        {
            state = new PlaylistState { Id = entry.Id };
            dirty = true;
        }

        bool liked = entry.Id == CatalogEntry.LikedId;

        // --- fetch only when the snapshot changed
        if (ctx.SpotifyOk && firstPass)
        {
            try
            {
                if (liked)
                {
                    var snap = await _spotify.GetLikedSnapshotAsync(ct).ConfigureAwait(false);
                    if (!string.Equals(entry.Snapshot, snap, StringComparison.Ordinal))
                    {
                        entry.Snapshot = snap;
                        catalogDirty = true;
                    }
                }

                if (state.FetchedAt is null || !string.Equals(state.Snapshot, entry.Snapshot, StringComparison.Ordinal))
                {
                    List<SpTrack> tracks;
                    if (liked)
                    {
                        tracks = await _spotify.GetLikedTracksAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var r = await _spotify.GetPlaylistItemsAsync(entry.Id, !ctx.Catalog.NoFields, ct).ConfigureAwait(false);
                        tracks = r.Tracks;
                        if (r.FieldsRejected && !ctx.Catalog.NoFields)
                        {
                            ctx.Catalog.NoFields = true;
                            catalogDirty = true;
                        }
                    }

                    MergeTracks(state, tracks);
                    state.Snapshot = entry.Snapshot;
                    state.FetchedAt = DateTime.UtcNow;
                    dirty = true;
                    ctx.Outcome.Fetched++;
                }
            }
            catch (SpotifyRateLimitedException e)
            {
                // Stop fetching for this run; continue with cached track lists.
                ctx.SpotifyOk = false;
                ctx.Outcome.SpotifyError = e.Message;
                Status.Log("warn", "Spotify rate limit: using cached tracks until " + e.Until.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            }
        }

        // --- incremental matching
        dirty |= await MatchAsync(state, ctx, ct).ConfigureAwait(false);

        // --- counts
        int matched = 0, review = 0, missing = 0, ignored = 0;
        foreach (var t in state.Tracks)
        {
            switch (t.St)
            {
                case TrackStatus.Matched: matched++; break;
                case TrackStatus.Review: review++; break;
                case TrackStatus.Ignored: ignored++; break;
                default: missing++; break;
            }
        }

        if (entry.Matched != matched || entry.Review != review || entry.Missing != missing || entry.Ignored != ignored)
        {
            entry.Matched = matched;
            entry.Review = review;
            entry.Missing = missing;
            entry.Ignored = ignored;
            catalogDirty = true;
        }

        // --- write to Jellyfin only if the content differs
        var desired = DesiredItems(state);
        int writesBefore = ctx.Outcome.Writes;
        bool mirror = entry.Mode != SyncModes.AddOnly;
        var name = ctx.Settings.NamePrefix + entry.Name;
        var owner = ctx.Settings.OwnerUserId;
        string summary;
        if (owner == Guid.Empty)
        {
            summary = "no owner user set, not written";
        }
        else
        {
            var jfId = _target.Resolve(entry.JfId, name, owner);
            if (jfId is Guid found && found != entry.JfId && ctx.Catalog.Playlists.Exists(p => p != entry && p.JfId == found))
            {
                jfId = null; // same name as a playlist another Spotify playlist already syncs into
            }

            if (jfId is null)
            {
                if (desired.Count > 0)
                {
                    jfId = await _target.CreateAsync(name, owner, desired, ct).ConfigureAwait(false);
                    ctx.Outcome.Writes++;
                    summary = "created with " + desired.Count + " songs";
                }
                else
                {
                    summary = "nothing matched yet";
                }
            }
            else
            {
                var current = _target.GetItems(jfId.Value);
                var plan = PlaylistDiff.Compute(current, desired, mirror);
                if (plan.IsEmpty)
                {
                    summary = "unchanged";
                }
                else if (plan.Replace)
                {
                    await _target.ReplaceAsync(jfId.Value, desired, owner, ct).ConfigureAwait(false);
                    ctx.Outcome.Writes++;
                    summary = "rewritten (" + desired.Count + " songs)";
                }
                else
                {
                    if (plan.Remove.Count > 0)
                    {
                        await _target.RemoveAsync(jfId.Value, plan.Remove, ct).ConfigureAwait(false);
                        ctx.Outcome.Writes++;
                    }

                    if (plan.Append.Count > 0)
                    {
                        await _target.AppendAsync(jfId.Value, plan.Append, owner, ct).ConfigureAwait(false);
                        ctx.Outcome.Writes++;
                    }

                    foreach (var m in plan.Moves)
                    {
                        await _target.MoveAsync(jfId.Value, m.Item, m.NewIndex, owner, ct).ConfigureAwait(false);
                        ctx.Outcome.Writes++;
                    }

                    summary = "+" + plan.Append.Count + " -" + plan.Remove.Count + " moved " + plan.Moves.Count;
                }
            }

            if (entry.JfId != jfId)
            {
                entry.JfId = jfId;
                catalogDirty = true;
            }

            var hash = Hash(desired);
            if (jfId is not null && entry.Written != hash)
            {
                entry.Written = hash;
                catalogDirty = true;
            }
        }

        if (dirty)
        {
            await _store.SavePlaylistAsync(state, ct).ConfigureAwait(false);
            ctx.Outcome.StateWrites++;
        }

        if (dirty || writesBefore != ctx.Outcome.Writes)
        {
            entry.LastSync = DateTime.UtcNow;
            catalogDirty = true;
        }

        var line = string.Format(CultureInfo.InvariantCulture, "{0}: {1}/{2} in Jellyfin, {3} need a look, {4} not found, {5} ignored; {6}", entry.Name, matched, state.Tracks.Count, review, missing, ignored, summary);
        _logger.LogInformation("SpotiSync: {Line}", line);
        Status.Log("info", line);
        return catalogDirty;
    }

    private async Task<bool> MatchAsync(PlaylistState state, RunContext ctx, CancellationToken ct)
    {
        bool dirty = false;
        bool libChanged = state.LibGen != ctx.Catalog.LibGen;
        bool versionChanged = state.MatcherVersion != Matcher.Version;

        // Overrides first (cheap, no index needed).
        foreach (var t in state.Tracks)
        {
            ctx.Overrides.Items.TryGetValue(t.Key, out var ov);
            int rev = ov?.Rev ?? 0;
            if (rev != t.OvRev)
            {
                ApplyOverride(t, ov);
                dirty = true;
            }
        }

        // Which tracks need the index?
        bool needIndex = false;
        foreach (var t in state.Tracks)
        {
            if (NeedsMatch(t, libChanged, versionChanged) || (libChanged && t.Item is not null))
            {
                needIndex = true;
                break;
            }
        }

        if (needIndex)
        {
            var index = await GetIndexAsync(ctx, ct).ConfigureAwait(false);
            var matcher = ctx.Matcher!;
            foreach (var t in state.Tracks)
            {
                bool itemGone = t.Item is Guid g && !index.IdToIndex.ContainsKey(g);
                if (t.Ov)
                {
                    // A chosen item that disappeared from the library cannot be synced.
                    if (t.St == TrackStatus.Matched && itemGone)
                    {
                        t.St = TrackStatus.Missing;
                        dirty = true;
                    }
                    else if (t.St == TrackStatus.Missing && t.Item is Guid back && index.IdToIndex.ContainsKey(back))
                    {
                        t.St = TrackStatus.Matched;
                        dirty = true;
                    }

                    continue;
                }

                if (!NeedsMatch(t, libChanged, versionChanged) && !(t.St == TrackStatus.Matched && itemGone))
                {
                    continue; // matched item still exists: keep without rescoring
                }

                var r = matcher.Match(new MatchQuery(t.Title, t.Artists, t.Album, t.Ms, t.Isrc));
                ctx.Outcome.Matched++;
                var st = r.Kind switch { MatchKind.Matched => TrackStatus.Matched, MatchKind.Review => TrackStatus.Review, _ => TrackStatus.Missing };
                Guid? item = r.Index >= 0 ? index.Ids[r.Index] : null;
                double score = Math.Round(r.Score, 3);
                if (t.St != st || t.Item != item || t.Score != score)
                {
                    t.St = st;
                    t.Item = item;
                    t.Score = score;
                    t.Text = r.Index >= 0 ? Describe(index, r.Index) : null;
                    dirty = true;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("SpotiSync match {Title} / {Artist} -> {Status} {Score} {Item}", t.Title, t.Artists.Length > 0 ? t.Artists[0] : string.Empty, st, score, t.Text);
                    }
                }
            }
        }

        if (state.LibGen != ctx.Catalog.LibGen || state.MatcherVersion != Matcher.Version)
        {
            state.LibGen = ctx.Catalog.LibGen;
            state.MatcherVersion = Matcher.Version;
            dirty = true;
        }

        return dirty;
    }

    private static bool NeedsMatch(TrackState t, bool libChanged, bool versionChanged)
        => !t.Ov && (t.St == TrackStatus.Pending || versionChanged || (libChanged && t.St is TrackStatus.Review or TrackStatus.Missing));

    private async Task<LibraryIndex> GetIndexAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.Index is null)
        {
            var sw = Stopwatch.StartNew();
            bool cached = _cache.IsLoaded;
            ctx.Index = await _cache.GetAsync(ctx.Catalog.LibGen, _library, ct).ConfigureAwait(false);
            ctx.Matcher = new Matcher(ctx.Index);
            ctx.Outcome.IndexUsed = true;
            _logger.LogInformation("SpotiSync: library index {How} ({Count} songs, {Ms} ms)", cached ? "reused" : "built", ctx.Index.Count, sw.ElapsedMilliseconds);
        }

        return ctx.Index;
    }

    /// <summary>Applies an override (or its removal) to a track.</summary>
    /// <param name="t">Track.</param>
    /// <param name="ov">Override or null.</param>
    public static void ApplyOverride(TrackState t, OverrideEntry? ov)
    {
        t.OvRev = ov?.Rev ?? 0;
        if (ov is null)
        {
            if (t.Ov)
            {
                t.Ov = false;
                t.St = TrackStatus.Pending;
                t.Item = null;
                t.Text = null;
                t.Score = 0;
            }
        }
        else if (ov.Ignore)
        {
            t.Ov = true;
            t.St = TrackStatus.Ignored;
        }
        else if (ov.Item is Guid item)
        {
            t.Ov = true;
            t.St = TrackStatus.Matched;
            t.Item = item;
            t.Text = ov.Text ?? t.Text;
            t.Score = 1;
        }
    }

    /// <summary>Display text for an item.</summary>
    /// <param name="index">Index.</param>
    /// <param name="i">Item index.</param>
    /// <returns>"Title — Artist · Album".</returns>
    public static string Describe(LibraryIndex index, int i)
    {
        var s = index.Names[i];
        if (index.ArtistDisplay[i].Length > 0)
        {
            s += " — " + index.ArtistDisplay[i];
        }

        if (index.AlbumDisplay[i].Length > 0)
        {
            s += " · " + index.AlbumDisplay[i];
        }

        return s;
    }

    /// <summary>Distinct matched items in Spotify order.</summary>
    /// <param name="state">Playlist.</param>
    /// <returns>Item ids.</returns>
    public static List<Guid> DesiredItems(PlaylistState state)
    {
        var list = new List<Guid>(state.Tracks.Count);
        var seen = new HashSet<Guid>();
        foreach (var t in state.Tracks)
        {
            if (t.St == TrackStatus.Matched && t.Item is Guid g && seen.Add(g))
            {
                list.Add(g);
            }
        }

        return list;
    }

    /// <summary>Merges a freshly fetched track list into the state, keeping matches of known tracks.</summary>
    /// <param name="state">Playlist state.</param>
    /// <param name="tracks">Tracks from Spotify, in order.</param>
    public static void MergeTracks(PlaylistState state, List<SpTrack> tracks)
    {
        var old = new Dictionary<string, Queue<TrackState>>(state.Tracks.Count, StringComparer.Ordinal);
        foreach (var t in state.Tracks)
        {
            if (!old.TryGetValue(t.Key, out var q))
            {
                old[t.Key] = q = new Queue<TrackState>(1);
            }

            q.Enqueue(t);
        }

        var merged = new List<TrackState>(tracks.Count);
        foreach (var sp in tracks)
        {
            var artists = new string[sp.Artists?.Count ?? 0];
            for (int i = 0; i < artists.Length; i++)
            {
                artists[i] = sp.Artists![i].Name ?? string.Empty;
            }

            string key = !string.IsNullOrEmpty(sp.Id) && !sp.IsLocal ? sp.Id : (sp.Uri ?? ("local:" + sp.Name + "|" + (artists.Length > 0 ? artists[0] : string.Empty)));
            if (old.TryGetValue(key, out var q) && q.Count > 0)
            {
                merged.Add(q.Dequeue());
                continue;
            }

            merged.Add(new TrackState
            {
                Key = key,
                Id = sp.IsLocal ? null : sp.Id,
                Title = sp.Name ?? string.Empty,
                Artists = artists,
                Album = sp.Album?.Name,
                Ms = sp.DurationMs,
                Isrc = sp.ExternalIds?.Isrc,
                St = TrackStatus.Pending,
            });
        }

        state.Tracks = merged;
    }

    /// <summary>Updates the catalog from /me/playlists. New playlists start disabled.</summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="lists">Playlists from Spotify.</param>
    /// <param name="settings">Settings.</param>
    /// <returns>True when the catalog changed.</returns>
    public static bool MergeCatalog(Catalog catalog, List<SpPlaylist> lists, SyncSettings settings)
    {
        bool dirty = false;
        var byId = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var e in catalog.Playlists)
        {
            byId[e.Id] = e;
        }

        var result = new List<CatalogEntry>(lists.Count + 1);
        if (!byId.TryGetValue(CatalogEntry.LikedId, out var liked))
        {
            liked = new CatalogEntry { Id = CatalogEntry.LikedId, Name = "Liked Songs", Readable = true, Mode = SyncModes.Normalize(settings.DefaultMode) };
            dirty = true;
        }

        result.Add(liked);
        foreach (var p in lists)
        {
            if (!byId.TryGetValue(p.Id!, out var e))
            {
                e = new CatalogEntry { Id = p.Id!, Mode = SyncModes.Normalize(settings.DefaultMode) };
                dirty = true;
            }

            bool readable = p.Collaborative || string.Equals(p.Owner?.Id, catalog.MeId, StringComparison.Ordinal);
            string? image = null;
            if (p.Images is { Count: > 0 })
            {
                // smallest image (Spotify lists them largest first)
                image = p.Images[^1]?.Url;
            }

            dirty |= Set(e.Name, p.Name ?? string.Empty, v => e.Name = v ?? string.Empty)
                | Set(e.OwnerName, p.Owner?.DisplayName ?? p.Owner?.Id, v => e.OwnerName = v)
                | Set(e.Snapshot, p.SnapshotId, v => e.Snapshot = v)
                | Set(e.Image, image, v => e.Image = v);
            if (e.Collab != p.Collaborative || e.Readable != readable || e.Total != p.Total)
            {
                e.Collab = p.Collaborative;
                e.Readable = readable;
                e.Total = p.Total;
                dirty = true;
            }

            result.Add(e);
        }

        if (result.Count != catalog.Playlists.Count)
        {
            dirty = true;
        }
        else
        {
            for (int i = 0; i < result.Count && !dirty; i++)
            {
                dirty |= !ReferenceEquals(result[i], catalog.Playlists[i]);
            }
        }

        catalog.Playlists = result;
        return dirty;

        static bool Set(string? old, string? value, Action<string?> set)
        {
            if (string.Equals(old, value, StringComparison.Ordinal))
            {
                return false;
            }

            set(value);
            return true;
        }
    }

    private static string Hash(List<Guid> items)
    {
        var bytes = new byte[items.Count * 16];
        for (int i = 0; i < items.Count; i++)
        {
            items[i].TryWriteBytes(bytes.AsSpan(i * 16, 16));
        }

        return Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 12));
    }

    private sealed class RunContext
    {
        public RunContext(Catalog catalog, Overrides overrides, SyncSettings settings, SyncOutcome outcome)
        {
            Catalog = catalog;
            Overrides = overrides;
            Settings = settings;
            Outcome = outcome;
        }

        public Catalog Catalog { get; }

        public Overrides Overrides { get; set; }

        public SyncSettings Settings { get; }

        public SyncOutcome Outcome { get; }

        public bool SpotifyOk { get; set; }

        public LibraryIndex? Index { get; set; }

        public Matcher? Matcher { get; set; }
    }
}
