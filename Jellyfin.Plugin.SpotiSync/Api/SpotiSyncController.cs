using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SpotiSync.Server;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Jellyfin.Plugin.SpotiSync.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpotiSync.Api;

/// <summary>
/// Dashboard API. Everything except the OAuth callback requires an administrator.
/// Responses are serialized with the source-generated <see cref="StateJsonContext"/>; cached
/// state files are streamed straight from disk with ETags so unchanged lists cost a 304.
/// </summary>
[ApiController]
[Route("SpotiSync")]
[Authorize(Policy = Policies.RequiresElevation)]
public class SpotiSyncController : ControllerBase
{
    private const int MaxSearchResults = 15;

    private readonly SpotiSyncService _service;
    private readonly IServerApplicationHost _host;
    private readonly IUserManager _users;
    private readonly ITaskManager _tasks;
    private readonly ILogger<SpotiSyncController> _logger;

    /// <summary>Initializes a new instance of the <see cref="SpotiSyncController"/> class.</summary>
    /// <param name="service">Service.</param>
    /// <param name="host">Server host.</param>
    /// <param name="users">User manager.</param>
    /// <param name="tasks">Task manager.</param>
    /// <param name="logger">Logger.</param>
    public SpotiSyncController(SpotiSyncService service, IServerApplicationHost host, IUserManager users, ITaskManager tasks, ILogger<SpotiSyncController> logger)
    {
        _service = service;
        _host = host;
        _users = users;
        _tasks = tasks;
        _logger = logger;
    }

    // ------------------------------------------------------------------ settings & auth

    /// <summary>Gets settings, connection state and the redirect URI to register at Spotify.</summary>
    /// <returns>Settings.</returns>
    [HttpGet("Settings")]
    public async Task<ActionResult> GetSettings()
    {
        var cfg = SpotiSyncService.Config;
        var auth = await _service.Store.LoadAuthAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var catalog = await _service.Store.LoadCatalogAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var users = new List<UserDto>();
        foreach (var u in _users.GetUsers())
        {
            users.Add(new UserDto { Id = u.Id, Name = u.Username });
        }

        var dto = new SettingsDto
        {
            ClientId = cfg.ClientId,
            OwnerUserId = cfg.OwnerUserId,
            PublicUrl = cfg.PublicUrl,
            DefaultMode = cfg.DefaultMode,
            NamePrefix = cfg.NamePrefix,
            RedirectUri = RedirectUri(),
            Connected = !string.IsNullOrEmpty(auth.RefreshToken),
            SpotifyUser = catalog.MeName ?? catalog.MeId,
            RateLimitedUntil = auth.RateLimitedUntil is { } r && r > DateTimeOffset.UtcNow ? r : null,
            Users = users,
        };
        return Json(dto, StateJsonContext.Default.SettingsDto);
    }

    /// <summary>Saves settings.</summary>
    /// <returns>The saved settings.</returns>
    [HttpPost("Settings")]
    public async Task<ActionResult> SaveSettings()
    {
        var dto = await ReadAsync(StateJsonContext.Default.SettingsDto).ConfigureAwait(false);
        var plugin = Plugin.Instance;
        if (dto is null || plugin is null)
        {
            return BadRequest();
        }

        var cfg = plugin.Configuration;
        cfg.ClientId = (dto.ClientId ?? string.Empty).Trim();
        cfg.OwnerUserId = dto.OwnerUserId;
        cfg.PublicUrl = (dto.PublicUrl ?? string.Empty).Trim().TrimEnd('/');
        cfg.DefaultMode = SyncModes.Normalize(dto.DefaultMode);
        cfg.NamePrefix = dto.NamePrefix ?? string.Empty;
        plugin.SaveConfiguration();
        return await GetSettings().ConfigureAwait(false);
    }

    /// <summary>Starts the Spotify login: returns the authorize URL to open.</summary>
    /// <returns>Login URL.</returns>
    [HttpGet("Login")]
    public ActionResult Login()
    {
        try
        {
            return Json(new LoginDto { Url = _service.Spotify.BuildAuthorizeUrl(RedirectUri()) }, StateJsonContext.Default.LoginDto);
        }
        catch (SpotifyAuthException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>Forgets the Spotify tokens.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Logout")]
    public async Task<ActionResult> Logout()
    {
        await _service.Spotify.LogoutAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        await UpdateCatalogAsync(c =>
        {
            c.MeId = null;
            c.MeName = null;
        }).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>OAuth redirect target. Anonymous, protected by the one-time PKCE state.</summary>
    /// <param name="code">Authorization code.</param>
    /// <param name="state">State.</param>
    /// <param name="error">Error from Spotify.</param>
    /// <returns>A small HTML page.</returns>
    [HttpGet("Callback")]
    [AllowAnonymous]
    public async Task<ActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        string message;
        bool ok = false;
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            message = "Spotify login was cancelled or failed: " + (error ?? "missing code");
        }
        else
        {
            try
            {
                var me = await _service.Spotify.CompleteLoginAsync(state, code, HttpContext.RequestAborted).ConfigureAwait(false);
                await UpdateCatalogAsync(c =>
                {
                    c.MeId = me.Id;
                    c.MeName = me.DisplayName ?? me.Id;
                }).ConfigureAwait(false);
                message = "Connected to Spotify as " + (me.DisplayName ?? me.Id) + ".";
                ok = true;
            }
            catch (Exception e) when (e is SpotifyAuthException or SpotifyApiException or System.Net.Http.HttpRequestException)
            {
                _logger.LogWarning("SpotiSync: Spotify login failed: {Message}", e.Message);
                message = e.Message;
            }
        }

        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>SpotiSync</title>"
            + "<style>body{font-family:sans-serif;background:#101010;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}"
            + "div{max-width:30em;padding:2em;text-align:center}a{color:#00a4dc}</style></head><body><div><h2>"
            + (ok ? "&#10003; " : "&#9888; ") + WebUtility.HtmlEncode(message) + "</h2>"
            + "<p><a href=\"../web/#/configurationpage?name=SpotiSync\">Back to SpotiSync</a></p>"
            + "<script>try{if(window.opener){window.opener.postMessage('spotisync-login','*');window.close();}}catch(e){}</script>"
            + "</div></body></html>";
        return Content(html, "text/html", Encoding.UTF8);
    }

    // ------------------------------------------------------------------ playlists

    /// <summary>The playlist catalog (with counts), straight from disk, with ETag.</summary>
    /// <returns>Catalog JSON.</returns>
    [HttpGet("Playlists")]
    public ActionResult GetPlaylists() => FileWithETag(_service.Store.CatalogPath, "{\"playlists\":[]}");

    /// <summary>Toggles a playlist or changes its mode.</summary>
    /// <param name="id">Spotify playlist id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Playlists/{id}")]
    public async Task<ActionResult> PatchPlaylist([FromRoute] string id)
    {
        var patch = await ReadAsync(StateJsonContext.Default.PlaylistPatch).ConfigureAwait(false);
        if (patch is null)
        {
            return BadRequest();
        }

        bool found = false;
        await UpdateCatalogAsync(c =>
        {
            var e = c.Playlists.Find(p => p.Id == id);
            if (e is null)
            {
                return;
            }

            found = true;
            if (patch.Enabled is bool en)
            {
                e.Enabled = en && e.Readable;
            }

            if (patch.Mode is not null)
            {
                e.Mode = SyncModes.Normalize(patch.Mode);
            }

            if (patch.Target is not null)
            {
                var t = patch.Target.Trim();
                var target = t.Length == 0 ? null : t;
                if (!string.Equals(target, e.Target, StringComparison.Ordinal))
                {
                    e.Target = target;
                    e.JfId = null; // resolve the new destination on the next sync
                    e.Written = null;
                }
            }
        }).ConfigureAwait(false);
        return found ? NoContent() : NotFound();
    }

    /// <summary>Adds a public Spotify playlist by link (works without a Spotify account or Premium).</summary>
    /// <returns>The new catalog entry.</returns>
    [HttpPost("Public")]
    public async Task<ActionResult> AddPublic()
    {
        var req = await ReadAsync(StateJsonContext.Default.AddPublicRequest).ConfigureAwait(false);
        var spotifyId = SpotifyPublicClient.ParsePlaylistId(req?.Url);
        if (spotifyId is null)
        {
            return BadRequest("Paste a Spotify playlist link like https://open.spotify.com/playlist/...");
        }

        PublicPlaylist pub;
        try
        {
            pub = await _service.Public.GetAsync(spotifyId, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SpotifyApiException or System.Net.Http.HttpRequestException or JsonException)
        {
            return BadRequest("Could not read this playlist: " + e.Message);
        }

        var id = "pub-" + spotifyId;
        CatalogEntry? entry = null;
        await UpdateCatalogAsync(c =>
        {
            entry = c.Playlists.Find(p => p.Id == id);
            if (entry is null)
            {
                entry = new CatalogEntry { Id = id, Mode = SyncModes.Normalize(SpotiSyncService.Config.DefaultMode) };
                c.Playlists.Add(entry);
            }

            entry.Source = CatalogEntry.SourcePublic;
            entry.SpotifyId = spotifyId;
            entry.Name = pub.Name;
            entry.Image = pub.Image;
            entry.Total = pub.Tracks.Count;
            entry.Capped = pub.Capped;
            entry.Readable = true;
            entry.Enabled = true;
        }).ConfigureAwait(false);
        QueueRun(rematch: false);
        return Json(entry!, StateJsonContext.Default.CatalogEntry);
    }

    /// <summary>
    /// Imports one JSON file of Spotify's data export (Playlist1.json, YourLibrary.json, ...).
    /// The body is the file content. Imported playlists start switched off.
    /// </summary>
    /// <returns>What was imported.</returns>
    [HttpPost("Import")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<ActionResult> Import()
    {
        List<ExportedPlaylist> found;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, default, HttpContext.RequestAborted).ConfigureAwait(false);
            found = SpotifyExport.Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return BadRequest("This is not a JSON file from Spotify's data export.");
        }

        if (found.Count == 0)
        {
            return BadRequest("No playlists in this file. Use Playlist1.json (and Playlist2.json, ...) or YourLibrary.json from Spotify's data export.");
        }

        var result = new ImportResultDto();
        var ct = HttpContext.RequestAborted;
        var entries = new List<(string Id, ExportedPlaylist Pl)>();
        await UpdateCatalogAsync(c =>
        {
            foreach (var pl in found)
            {
                var id = "file-" + Slug(pl.Name);
                var entry = c.Playlists.Find(p => p.Id == id);
                if (entry is null)
                {
                    entry = new CatalogEntry { Id = id, Mode = SyncModes.Normalize(SpotiSyncService.Config.DefaultMode) };
                    c.Playlists.Add(entry);
                }

                entry.Source = CatalogEntry.SourceFile;
                entry.Name = pl.Name;
                entry.Total = pl.Tracks.Count;
                entry.Readable = true;
                entry.Snapshot = SyncEngine.Snapshot(pl.Tracks);
                entries.Add((id, pl));
            }
        }).ConfigureAwait(false);

        foreach (var (id, pl) in entries)
        {
            var state = await _service.Store.LoadPlaylistAsync(id, ct).ConfigureAwait(false) ?? new PlaylistState { Id = id };
            var snap = SyncEngine.Snapshot(pl.Tracks);
            if (state.Snapshot != snap)
            {
                SyncEngine.MergeTracks(state, pl.Tracks);
                state.Snapshot = snap;
                state.FetchedAt = DateTime.UtcNow;
                await _service.Store.SavePlaylistAsync(state, ct).ConfigureAwait(false);
            }

            result.Playlists++;
            result.Tracks += pl.Tracks.Count;
            result.Names.Add(pl.Name);
        }

        QueueRun(rematch: true);
        return Json(result, StateJsonContext.Default.ImportResultDto);
    }

    /// <summary>Removes a public-link or imported playlist from SpotiSync (the Jellyfin playlist stays).</summary>
    /// <param name="id">Catalog id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Playlists/{id}")]
    public async Task<ActionResult> RemovePlaylist([FromRoute] string id)
    {
        bool removed = false;
        await UpdateCatalogAsync(c => removed = c.Playlists.RemoveAll(p => p.Id == id && p.Source is not null) > 0).ConfigureAwait(false);
        if (!removed)
        {
            return NotFound();
        }

        _service.Store.DeletePlaylist(id);
        return NoContent();
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in Matching.TextNorm.Normalize(name))
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0 || slug.Length > 60)
        {
            slug = (slug.Length > 0 ? slug[..40] + "-" : string.Empty) + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 4)).ToLowerInvariant();
        }

        return slug;
    }

    /// <summary>Cached tracks and matches of a playlist, straight from disk, with ETag.</summary>
    /// <param name="id">Spotify playlist id.</param>
    /// <returns>Playlist JSON.</returns>
    [HttpGet("Playlists/{id}/Tracks")]
    public ActionResult GetTracks([FromRoute] string id) => FileWithETag(_service.Store.PlaylistPath(id), "{\"tracks\":[]}");

    /// <summary>Accepts every suggestion of a playlist.</summary>
    /// <param name="id">Spotify playlist id.</param>
    /// <returns>Number accepted.</returns>
    [HttpPost("Playlists/{id}/AcceptAll")]
    public async Task<ActionResult> AcceptAll([FromRoute] string id)
    {
        var state = await _service.Store.LoadPlaylistAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
        if (state is null)
        {
            return NotFound();
        }

        int n = 0;
        await UpdateOverridesAsync(o =>
        {
            foreach (var t in state.Tracks)
            {
                if (t.St == TrackStatus.Review && t.Item is Guid item)
                {
                    o.Items[t.Key] = new OverrideEntry { Item = item, Text = t.Text, Rev = o.Rev };
                    n++;
                }
            }
        }).ConfigureAwait(false);
        return Content(n.ToString(CultureInfo.InvariantCulture), "text/plain");
    }

    /// <summary>Plain-text list of Spotify links that are not in the library.</summary>
    /// <param name="playlist">Playlist id, or empty for all synced playlists.</param>
    /// <returns>One URL per line.</returns>
    [HttpGet("Missing")]
    public async Task<ActionResult> Missing([FromQuery] string? playlist)
    {
        var ct = HttpContext.RequestAborted;
        var ids = new List<string>();
        if (!string.IsNullOrEmpty(playlist))
        {
            ids.Add(playlist);
        }
        else
        {
            foreach (var e in (await _service.Store.LoadCatalogAsync(ct).ConfigureAwait(false)).Playlists)
            {
                if (e.Enabled)
                {
                    ids.Add(e.Id);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            var state = await _service.Store.LoadPlaylistAsync(id, ct).ConfigureAwait(false);
            if (state is null)
            {
                continue;
            }

            foreach (var t in state.Tracks)
            {
                if (t.St is TrackStatus.Missing or TrackStatus.Pending && t.Id is not null && seen.Add(t.Id))
                {
                    sb.Append("https://open.spotify.com/track/").Append(t.Id).Append('\n');
                }
            }
        }

        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }

    // ------------------------------------------------------------------ overrides & search

    /// <summary>Sets or clears a manual decision for a track, then applies it with a quick rematch.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Override")]
    public async Task<ActionResult> SetOverride()
    {
        var req = await ReadAsync(StateJsonContext.Default.OverrideRequest).ConfigureAwait(false);
        if (req is null || string.IsNullOrEmpty(req.Key))
        {
            return BadRequest();
        }

        OverrideEntry? entry;
        switch (req.Action)
        {
            case "pick" when req.Item is Guid item:
                entry = new OverrideEntry { Item = item, Text = _service.Library.Describe(item) };
                if (entry.Text is null)
                {
                    return BadRequest("Item not found");
                }

                break;
            case "accept":
                var state = req.Playlist is null ? null : await _service.Store.LoadPlaylistAsync(req.Playlist, HttpContext.RequestAborted).ConfigureAwait(false);
                var track = state?.Tracks.Find(t => t.Key == req.Key);
                if (track?.Item is not Guid suggestion)
                {
                    return BadRequest("No suggestion for this track");
                }

                entry = new OverrideEntry { Item = suggestion, Text = track.Text };
                break;
            case "ignore":
                entry = new OverrideEntry { Ignore = true };
                break;
            case "clear":
                entry = null;
                break;
            default:
                return BadRequest("Unknown action");
        }

        await UpdateOverridesAsync(o =>
        {
            if (entry is null)
            {
                o.Items.Remove(req.Key);
            }
            else
            {
                entry.Rev = o.Rev;
                o.Items[req.Key] = entry;
            }
        }).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Searches the library (server side, on the inverted index).</summary>
    /// <param name="q">Query.</param>
    /// <returns>Up to 15 results.</returns>
    [HttpGet("Search")]
    public async Task<ActionResult> Search([FromQuery] string? q)
    {
        var results = new List<SearchResultDto>();
        if (string.IsNullOrWhiteSpace(q) || q.Length > 200)
        {
            return Json(results, StateJsonContext.Default.ListSearchResultDto);
        }

        // Tag the index with the current library generation so a sync right after can reuse it.
        var gen = (await _service.Store.LoadCatalogAsync(HttpContext.RequestAborted).ConfigureAwait(false)).LibGen;
        var index = await _service.Cache.GetAsync(-gen, _service.Library, HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            foreach (var hit in index.Search(q, MaxSearchResults))
            {
                int i = hit.Index;
                results.Add(new SearchResultDto
                {
                    Id = index.Ids[i],
                    Title = index.Names[i],
                    Artist = index.ArtistDisplay[i],
                    Album = index.AlbumDisplay[i],
                    Sec = index.DurationSec[i],
                    Text = SyncEngine.Describe(index, i),
                });
            }
        }
        finally
        {
            _service.Cache.ReleaseLater();
        }

        return Json(results, StateJsonContext.Default.ListSearchResultDto);
    }

    // ------------------------------------------------------------------ sync

    /// <summary>Queues a sync ("full") or a rematch ("rematch") through Jellyfin's task manager.</summary>
    /// <returns>Accepted.</returns>
    [HttpPost("Sync")]
    public async Task<ActionResult> Sync()
    {
        var req = await ReadAsync(StateJsonContext.Default.SyncRequest).ConfigureAwait(false);
        QueueRun(string.Equals(req?.Mode, "rematch", StringComparison.OrdinalIgnoreCase));
        return Accepted();
    }

    /// <summary>Live status plus log lines newer than <paramref name="since"/>.</summary>
    /// <param name="since">Last log sequence the client has.</param>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    public ActionResult Status([FromQuery] long since = 0)
    {
        var s = _service.Engine.Status;
        var etag = "W/\"s" + s.Version.ToString(CultureInfo.InvariantCulture) + "-" + since.ToString(CultureInfo.InvariantCulture) + "\"";
        if (NotModified(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var dto = new StatusDto
        {
            Running = s.Running,
            Mode = s.Mode == SyncMode.Full ? "full" : "rematch",
            Current = s.Current,
            Progress = s.Progress,
            StartedAt = s.StartedAt,
            FinishedAt = s.FinishedAt,
            LastResult = s.LastResult,
            LogSeq = s.LogSeq,
            Log = s.LogSince(since),
        };
        Response.Headers.ETag = etag;
        return Json(dto, StateJsonContext.Default.StatusDto);
    }

    // ------------------------------------------------------------------ helpers

    private void QueueRun(bool rematch)
    {
        if (rematch)
        {
            _tasks.QueueScheduledTask<RematchTask>();
        }
        else
        {
            _tasks.QueueScheduledTask<SyncPlaylistsTask>();
        }
    }

    private string RedirectUri()
    {
        var cfg = SpotiSyncService.Config;
        var baseUrl = string.IsNullOrWhiteSpace(cfg.PublicUrl) ? _host.GetSmartApiUrl(Request) : cfg.PublicUrl;
        return baseUrl.TrimEnd('/') + "/SpotiSync/Callback";
    }

    private async Task UpdateCatalogAsync(Action<Catalog> change)
    {
        var store = _service.Store;
        await store.Gate.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var c = await store.LoadCatalogAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            change(c);
            await store.SaveCatalogAsync(c, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            store.Gate.Release();
        }
    }

    private async Task UpdateOverridesAsync(Action<Overrides> change)
    {
        var store = _service.Store;
        await store.Gate.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var o = await store.LoadOverridesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            o.Rev++;
            change(o);
            await store.SaveOverridesAsync(o, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            store.Gate.Release();
        }

        // Apply right away: a rematch is incremental and does not call Spotify. If a sync is
        // already running it picks the change up at its end.
        QueueRun(rematch: true);
    }

    private ActionResult FileWithETag(string path, string emptyJson)
    {
        var etag = StateStore.ETagOf(path);
        if (etag is null)
        {
            return Content(emptyJson, "application/json", Encoding.UTF8);
        }

        Response.Headers.CacheControl = "no-cache";
        if (NotModified(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;
        return PhysicalFile(path, "application/json");
    }

    private bool NotModified(string etag)
    {
        foreach (var v in Request.Headers.IfNoneMatch)
        {
            if (string.Equals(v, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private FileContentResult Json<T>(T value, JsonTypeInfo<T> type)
        => new(JsonSerializer.SerializeToUtf8Bytes(value, type), "application/json");

    private async Task<T?> ReadAsync<T>(JsonTypeInfo<T> type)
        where T : class
    {
        if (Request.ContentLength is 0)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(Request.Body, type, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
