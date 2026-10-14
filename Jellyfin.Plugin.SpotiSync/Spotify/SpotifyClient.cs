using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpotiSync.Spotify;

/// <summary>
/// Minimal Spotify Web API client: Authorization Code + PKCE, token refresh (with refresh token
/// rotation), paging with limit=50, 429 Retry-After handling. Holds no background resources.
/// </summary>
public sealed class SpotifyClient : ISpotifySource, IDisposable
{
    /// <summary>Scopes requested at login.</summary>
    public const string Scopes = "playlist-read-private playlist-read-collaborative user-library-read";

    /// <summary>Longest Retry-After we wait for inline; longer ones stop fetching.</summary>
    public static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(90);

    // Overridable for end-to-end tests against a local mock (read once at startup).
    private static readonly string Api = (Environment.GetEnvironmentVariable("SPOTISYNC_SPOTIFY_API_URL") ?? "https://api.spotify.com/v1").TrimEnd('/');
    private static readonly string Accounts = (Environment.GetEnvironmentVariable("SPOTISYNC_SPOTIFY_ACCOUNTS_URL") ?? "https://accounts.spotify.com").TrimEnd('/');

    // Response filter for /items. Both the 2026 ("item") and older ("track") shapes are listed.
    private const string ItemFields = "next,total,items(is_local,item(id,uri,name,type,duration_ms,is_local,artists(name),album(name),external_ids(isrc)),track(id,uri,name,type,duration_ms,is_local,artists(name),album(name),external_ids(isrc)))";

    private static readonly ConcurrentDictionary<string, PendingLogin> Pending = new(StringComparer.Ordinal);

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _httpClientName;
    private readonly StateStore _store;
    private readonly Func<string> _clientId;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AuthState? _auth;

    /// <summary>Initializes a new instance of the <see cref="SpotifyClient"/> class.</summary>
    /// <param name="httpFactory">HTTP client factory (Jellyfin's; the default client decompresses gzip/brotli).</param>
    /// <param name="httpClientName">Named client to use.</param>
    /// <param name="store">State store.</param>
    /// <param name="clientId">Accessor for the configured client id.</param>
    /// <param name="logger">Logger.</param>
    public SpotifyClient(IHttpClientFactory httpFactory, string httpClientName, StateStore store, Func<string> clientId, ILogger logger)
    {
        _httpFactory = httpFactory;
        _httpClientName = httpClientName;
        _store = store;
        _clientId = clientId;
        _logger = logger;
    }

    /// <summary>Gets or sets the delay function (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <inheritdoc />
    public void Dispose() => _tokenLock.Dispose();

    /// <summary>Drops the cached token copy (call at the end of a sync so idle holds nothing).</summary>
    public void ReleaseCache() => _auth = null;

    // ---------------------------------------------------------------- login

    /// <summary>Creates the authorize URL and remembers the PKCE verifier for <paramref name="redirectUri"/>.</summary>
    /// <param name="redirectUri">Redirect URI registered in the Spotify dashboard.</param>
    /// <returns>URL to open in the browser.</returns>
    public string BuildAuthorizeUrl(string redirectUri)
    {
        var clientId = _clientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new SpotifyAuthException("Set the Spotify client id first.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Pending)
        {
            if (kv.Value.Expires < now)
            {
                Pending.TryRemove(kv.Key, out _);
            }
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        Pending[state] = new PendingLogin(verifier, redirectUri, now.AddMinutes(15));

        return Accounts + "/authorize?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(clientId)
            + "&scope=" + Uri.EscapeDataString(Scopes)
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&code_challenge_method=S256&code_challenge=" + challenge
            + "&state=" + state;
    }

    /// <summary>Completes the login: validates <paramref name="state"/> and exchanges the code.</summary>
    /// <param name="state">State from the callback.</param>
    /// <param name="code">Authorization code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Spotify user.</returns>
    public async Task<SpUser> CompleteLoginAsync(string state, string code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || !Pending.TryRemove(state, out var pending) || pending.Expires < DateTimeOffset.UtcNow)
        {
            throw new SpotifyAuthException("Unknown or expired login state. Start the login again from the SpotiSync page.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = _clientId(),
            ["code_verifier"] = pending.Verifier,
        };

        var token = await PostTokenAsync(form, ct).ConfigureAwait(false);
        var auth = new AuthState
        {
            AccessToken = token.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)),
            RefreshToken = token.RefreshToken,
            Scope = token.Scope,
        };
        await _store.SaveAuthAsync(auth, ct).ConfigureAwait(false);
        _auth = auth;
        return await GetMeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Forgets the tokens.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task LogoutAsync(CancellationToken ct)
    {
        _auth = null;
        await _store.SaveAuthAsync(new AuthState(), ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- ISpotifySource

    /// <inheritdoc />
    public async Task<bool> IsConnectedAsync(CancellationToken ct)
    {
        var a = _auth ?? await _store.LoadAuthAsync(ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(a.RefreshToken);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetRateLimitDeadlineAsync(CancellationToken ct)
    {
        var a = _auth ?? await _store.LoadAuthAsync(ct).ConfigureAwait(false);
        return a.RateLimitedUntil is { } u && u > DateTimeOffset.UtcNow ? u : null;
    }

    /// <inheritdoc />
    public Task<SpUser> GetMeAsync(CancellationToken ct)
        => GetJsonAsync(Api + "/me", SpotifyJsonContext.Default.SpUser, ct);

    /// <inheritdoc />
    public async Task<List<SpPlaylist>> GetPlaylistsAsync(CancellationToken ct)
    {
        var list = new List<SpPlaylist>();
        string? url = Api + "/me/playlists?limit=50";
        while (url is not null)
        {
            var page = await GetJsonAsync(url, SpotifyJsonContext.Default.SpPlaylistPage, ct).ConfigureAwait(false);
            if (page.Items is not null)
            {
                foreach (var p in page.Items)
                {
                    if (p?.Id is not null)
                    {
                        list.Add(p);
                    }
                }
            }

            url = page.Next;
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<PlaylistItemsResult> GetPlaylistItemsAsync(string playlistId, bool useFields, CancellationToken ct)
    {
        var baseUrl = Api + "/playlists/" + Uri.EscapeDataString(playlistId) + "/items?limit=50&additional_types=track";
        if (useFields)
        {
            try
            {
                var withFields = await ReadEntriesAsync(baseUrl + "&fields=" + Uri.EscapeDataString(ItemFields), ct).ConfigureAwait(false);

                // If the filter silently stripped the track objects, treat it as unsupported.
                if (withFields.Total == 0 || withFields.Tracks.Count > 0)
                {
                    return new PlaylistItemsResult(withFields.Tracks, false);
                }
            }
            catch (SpotifyApiException e) when (e.Status == 400)
            {
                _logger.LogInformation("Spotify rejected the fields filter on /items; continuing without it");
            }

            var plain = await ReadEntriesAsync(baseUrl, ct).ConfigureAwait(false);
            return new PlaylistItemsResult(plain.Tracks, true);
        }

        return new PlaylistItemsResult((await ReadEntriesAsync(baseUrl, ct).ConfigureAwait(false)).Tracks, false);
    }

    /// <inheritdoc />
    public async Task<string> GetLikedSnapshotAsync(CancellationToken ct)
    {
        var page = await GetJsonAsync(Api + "/me/tracks?limit=1", SpotifyJsonContext.Default.SpSavedPage, ct).ConfigureAwait(false);
        var first = page.Items is { Count: > 0 } ? page.Items[0] : null;
        return page.Total.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + first?.AddedAt + ":" + (first?.Track ?? first?.Item)?.Id;
    }

    /// <inheritdoc />
    public async Task<List<SpTrack>> GetLikedTracksAsync(CancellationToken ct)
    {
        var list = new List<SpTrack>();
        string? url = Api + "/me/tracks?limit=50";
        while (url is not null)
        {
            var page = await GetJsonAsync(url, SpotifyJsonContext.Default.SpSavedPage, ct).ConfigureAwait(false);
            if (page.Items is not null)
            {
                foreach (var s in page.Items)
                {
                    var t = s?.Track ?? s?.Item;
                    if (t is not null && (t.Type is null || t.Type == "track"))
                    {
                        list.Add(t);
                    }
                }
            }

            url = page.Next;
        }

        return list;
    }

    private async Task<(List<SpTrack> Tracks, int Total)> ReadEntriesAsync(string firstUrl, CancellationToken ct)
    {
        var list = new List<SpTrack>();
        int total = 0;
        string? url = firstUrl;
        while (url is not null)
        {
            var page = await GetJsonAsync(url, SpotifyJsonContext.Default.SpEntryPage, ct).ConfigureAwait(false);
            total = page.Total;
            if (page.Items is not null)
            {
                foreach (var e in page.Items)
                {
                    var t = e?.Value;
                    if (t is null || (t.Type is not null && t.Type != "track"))
                    {
                        continue; // podcast episodes and removed tracks
                    }

                    t.IsLocal |= e!.IsLocal;
                    list.Add(t);
                }
            }

            url = page.Next;
        }

        return (list, total);
    }

    // ---------------------------------------------------------------- HTTP plumbing

    private async Task<T> GetJsonAsync<T>(string url, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        bool refreshed = false;
        for (int attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(false, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.AcceptEncoding.ParseAdd("gzip, br");
            using var resp = await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                await GetAccessTokenAsync(true, ct).ConfigureAwait(false);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(resp);
                if (wait <= MaxInlineWait && attempt < 5)
                {
                    _logger.LogInformation("Spotify rate limit: waiting {Seconds}s", (int)wait.TotalSeconds);
                    await Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                var until = DateTimeOffset.UtcNow + wait;
                await RememberRateLimitAsync(until, ct).ConfigureAwait(false);
                throw new SpotifyRateLimitedException(until);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new SpotifyApiException((int)resp.StatusCode, "Spotify " + (int)resp.StatusCode + " for " + StripQuery(url) + ": " + Truncate(body, 300));
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(stream, type, ct).ConfigureAwait(false)
                    ?? throw new SpotifyApiException(0, "Empty response from Spotify");
            }
        }
    }

    private async Task<string> GetAccessTokenAsync(bool force, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _auth ??= await _store.LoadAuthAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(_auth.RefreshToken))
            {
                throw new SpotifyAuthException("Not connected to Spotify.");
            }

            if (!force && !string.IsNullOrEmpty(_auth.AccessToken) && _auth.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            {
                return _auth.AccessToken;
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _auth.RefreshToken,
                ["client_id"] = _clientId(),
            };

            SpToken token;
            try
            {
                token = await PostTokenAsync(form, ct).ConfigureAwait(false);
            }
            catch (SpotifyAuthException)
            {
                // Revoked or rotated away: the user has to log in again.
                _auth.AccessToken = null;
                _auth.RefreshToken = null;
                await _store.SaveAuthAsync(_auth, ct).ConfigureAwait(false);
                throw;
            }

            _auth.AccessToken = token.AccessToken;
            _auth.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));
            if (!string.IsNullOrEmpty(token.RefreshToken))
            {
                _auth.RefreshToken = token.RefreshToken; // rotation
            }

            if (!string.IsNullOrEmpty(token.Scope))
            {
                _auth.Scope = token.Scope;
            }

            await _store.SaveAuthAsync(_auth, ct).ConfigureAwait(false);
            return _auth.AccessToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<SpToken> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await Client().PostAsync(Accounts + "/api/token", content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        SpToken? token = null;
        try
        {
            token = JsonSerializer.Deserialize(body, SpotifyJsonContext.Default.SpToken);
        }
        catch (JsonException)
        {
        }

        if (!resp.IsSuccessStatusCode || token?.AccessToken is null)
        {
            var msg = token?.ErrorDescription ?? token?.Error ?? Truncate(body, 200);
            if ((int)resp.StatusCode is 400 or 401)
            {
                throw new SpotifyAuthException("Spotify token request failed: " + msg);
            }

            throw new SpotifyApiException((int)resp.StatusCode, "Spotify token request failed: " + msg);
        }

        return token;
    }

    private async Task RememberRateLimitAsync(DateTimeOffset until, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _auth ??= await _store.LoadAuthAsync(ct).ConfigureAwait(false);
            _auth.RateLimitedUntil = until;
            await _store.SaveAuthAsync(_auth, ct).ConfigureAwait(false);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private HttpClient Client() => _httpFactory.CreateClient(_httpClientName);

    internal static TimeSpan RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } d)
        {
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }

        if (ra?.Date is { } date)
        {
            var w = date - DateTimeOffset.UtcNow;
            return w < TimeSpan.Zero ? TimeSpan.Zero : w;
        }

        return TimeSpan.FromSeconds(5);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string StripQuery(string url)
    {
        int q = url.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? url : url[..q];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record PendingLogin(string Verifier, string RedirectUri, DateTimeOffset Expires);
}
