using System.Net;
using System.Text;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

internal sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    public Func<HttpRequestMessage, string, HttpResponseMessage> Handler { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

    public List<string> Requests { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(request.Method + " " + request.RequestUri);
        return Handler(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class SpotifyClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("spotisync-client-").FullName;
    private readonly StateStore _store;
    private readonly FakeHttp _http = new();
    private readonly SpotifyClient _client;
    private readonly List<TimeSpan> _waits = [];

    public SpotifyClientTests()
    {
        _store = new StateStore(_dir);
        _client = new SpotifyClient(_http, "default", _store, () => "client123", NullLogger.Instance)
        {
            Delay = (t, _) =>
            {
                _waits.Add(t);
                return Task.CompletedTask;
            },
        };
    }

    public void Dispose()
    {
        _client.Dispose();
        _store.Dispose();
        _http.Dispose();
        Directory.Delete(_dir, true);
    }

    private Task Connect(string access = "A1", string refresh = "R1", int expiresInSeconds = 3600)
        => _store.SaveAuthAsync(new AuthState { AccessToken = access, RefreshToken = refresh, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds) });

    private static string Entries(int from, int count, string? next, int total, string key = "item")
    {
        var items = string.Join(",", Enumerable.Range(from, count).Select(i =>
            "{\"" + key + "\":{\"id\":\"t" + i + "\",\"uri\":\"spotify:track:t" + i + "\",\"name\":\"Song " + i + "\",\"type\":\"track\",\"duration_ms\":1000,\"artists\":[{\"name\":\"A\"}],\"album\":{\"name\":\"B\"},\"external_ids\":{\"isrc\":\"X" + i + "\"}}}"));
        return "{\"items\":[" + items + "],\"next\":" + (next is null ? "null" : "\"" + next + "\"") + ",\"total\":" + total + "}";
    }

    [Fact]
    public async Task PlaylistItems_FollowsNextAcrossPages_For230Tracks()
    {
        await Connect();
        _http.Handler = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            Assert.Equal("Bearer A1", req.Headers.Authorization!.ToString());
            int offset = url.Contains("offset=", StringComparison.Ordinal) ? int.Parse(url[(url.IndexOf("offset=", StringComparison.Ordinal) + 7)..].Split('&')[0], System.Globalization.CultureInfo.InvariantCulture) : 0;
            int count = Math.Min(50, 230 - offset);
            string? next = offset + 50 < 230 ? "https://api.spotify.com/v1/playlists/p1/items?offset=" + (offset + 50) + "&limit=50" : null;
            return FakeHttp.Json(Entries(offset, count, next, 230));
        };

        var r = await _client.GetPlaylistItemsAsync("p1", true, CancellationToken.None);
        Assert.Equal(230, r.Tracks.Count);
        Assert.False(r.FieldsRejected);
        Assert.Equal("t229", r.Tracks[^1].Id);
        Assert.Equal("X5", r.Tracks[5].ExternalIds!.Isrc);
        Assert.Contains("fields=", _http.Requests[0], StringComparison.Ordinal);
        Assert.Contains("limit=50", _http.Requests[0], StringComparison.Ordinal);
        Assert.Equal(5, _http.Requests.Count);
    }

    [Fact]
    public async Task PlaylistItems_ReadsLegacyTrackShape()
    {
        await Connect();
        _http.Handler = (_, _) => FakeHttp.Json(Entries(0, 3, null, 3, key: "track"));
        var r = await _client.GetPlaylistItemsAsync("p1", false, CancellationToken.None);
        Assert.Equal(3, r.Tracks.Count);
    }

    [Fact]
    public async Task FieldsRejected_FallsBackWithoutFields()
    {
        await Connect();
        _http.Handler = (req, _) => req.RequestUri!.Query.Contains("fields=", StringComparison.Ordinal)
            ? FakeHttp.Json("{\"error\":{\"status\":400,\"message\":\"bad fields\"}}", HttpStatusCode.BadRequest)
            : FakeHttp.Json(Entries(0, 2, null, 2));
        var r = await _client.GetPlaylistItemsAsync("p1", true, CancellationToken.None);
        Assert.True(r.FieldsRejected);
        Assert.Equal(2, r.Tracks.Count);
    }

    [Fact]
    public async Task FieldsThatStripTracks_FallBackToo()
    {
        await Connect();
        _http.Handler = (req, _) => req.RequestUri!.Query.Contains("fields=", StringComparison.Ordinal)
            ? FakeHttp.Json("{\"items\":[{},{}],\"next\":null,\"total\":2}")
            : FakeHttp.Json(Entries(0, 2, null, 2));
        var r = await _client.GetPlaylistItemsAsync("p1", true, CancellationToken.None);
        Assert.True(r.FieldsRejected);
        Assert.Equal(2, r.Tracks.Count);
    }

    [Fact]
    public async Task ShortRetryAfter_WaitsAndRetries()
    {
        await Connect();
        int calls = 0;
        _http.Handler = (_, _) =>
        {
            if (calls++ == 0)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            }

            return FakeHttp.Json("{\"items\":[],\"next\":null,\"total\":0}");
        };
        var lists = await _client.GetPlaylistsAsync(CancellationToken.None);
        Assert.Empty(lists);
        Assert.Equal([TimeSpan.FromSeconds(7)], _waits);
    }

    [Fact]
    public async Task LongRetryAfter_StopsAndRemembersDeadline()
    {
        await Connect();
        _http.Handler = (_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
            return r;
        };
        var e = await Assert.ThrowsAsync<SpotifyRateLimitedException>(() => _client.GetPlaylistsAsync(CancellationToken.None));
        Assert.True(e.Until > DateTimeOffset.UtcNow.AddMinutes(59));
        Assert.Empty(_waits);
        Assert.NotNull(await _client.GetRateLimitDeadlineAsync(CancellationToken.None));
        Assert.NotNull((await _store.LoadAuthAsync()).RateLimitedUntil);
    }

    [Fact]
    public async Task ExpiredToken_IsRefreshed_AndRotatedRefreshTokenSaved()
    {
        await Connect(access: "old", refresh: "R1", expiresInSeconds: -10);
        string? tokenBody = null;
        _http.Handler = (req, body) =>
        {
            if (req.RequestUri!.Host == "accounts.spotify.com")
            {
                tokenBody = body;
                return FakeHttp.Json("{\"access_token\":\"A2\",\"expires_in\":3600,\"refresh_token\":\"R2\"}");
            }

            Assert.Equal("Bearer A2", req.Headers.Authorization!.ToString());
            return FakeHttp.Json("{\"id\":\"me\",\"display_name\":\"Me\"}");
        };
        var me = await _client.GetMeAsync(CancellationToken.None);
        Assert.Equal("me", me.Id);
        Assert.Contains("grant_type=refresh_token", tokenBody, StringComparison.Ordinal);
        Assert.Contains("refresh_token=R1", tokenBody, StringComparison.Ordinal);
        Assert.Contains("client_id=client123", tokenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", tokenBody, StringComparison.Ordinal);
        var saved = await _store.LoadAuthAsync();
        Assert.Equal("R2", saved.RefreshToken);
        Assert.Equal("A2", saved.AccessToken);
    }

    [Fact]
    public async Task Unauthorized_RefreshesOnceAndRetries()
    {
        await Connect(access: "revoked");
        int apiCalls = 0;
        _http.Handler = (req, _) =>
        {
            if (req.RequestUri!.Host == "accounts.spotify.com")
            {
                return FakeHttp.Json("{\"access_token\":\"fresh\",\"expires_in\":3600}");
            }

            apiCalls++;
            return req.Headers.Authorization!.Parameter == "fresh"
                ? FakeHttp.Json("{\"id\":\"me\"}")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        };
        Assert.Equal("me", (await _client.GetMeAsync(CancellationToken.None)).Id);
        Assert.Equal(2, apiCalls);
        Assert.Equal("R1", (await _store.LoadAuthAsync()).RefreshToken); // not rotated: keep old one
    }

    [Fact]
    public async Task RevokedRefreshToken_Disconnects()
    {
        await Connect(expiresInSeconds: -10);
        _http.Handler = (_, _) => FakeHttp.Json("{\"error\":\"invalid_grant\",\"error_description\":\"Refresh token revoked\"}", HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<SpotifyAuthException>(() => _client.GetMeAsync(CancellationToken.None));
        Assert.False(await _client.IsConnectedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Login_UsesPkceAndValidatesState()
    {
        var url = _client.BuildAuthorizeUrl("https://jf.example.com/SpotiSync/Callback");
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(SpotifyClient.Scopes, query["scope"]);
        Assert.Equal("client123", query["client_id"]);
        Assert.Equal("https://jf.example.com/SpotiSync/Callback", query["redirect_uri"]);
        var state = query["state"]!;
        var challenge = query["code_challenge"]!;

        string? tokenBody = null;
        _http.Handler = (req, body) =>
        {
            if (req.RequestUri!.Host == "accounts.spotify.com")
            {
                tokenBody = body;
                return FakeHttp.Json("{\"access_token\":\"A\",\"expires_in\":3600,\"refresh_token\":\"R\",\"scope\":\"x\"}");
            }

            return FakeHttp.Json("{\"id\":\"me\",\"display_name\":\"Me\"}");
        };

        await Assert.ThrowsAsync<SpotifyAuthException>(() => _client.CompleteLoginAsync("forged", "code", CancellationToken.None));
        var me = await _client.CompleteLoginAsync(state, "thecode", CancellationToken.None);
        Assert.Equal("Me", me.DisplayName);
        var form = System.Web.HttpUtility.ParseQueryString(tokenBody!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("thecode", form["code"]);
        var verifier = form["code_verifier"]!;
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);

        // State is single use.
        await Assert.ThrowsAsync<SpotifyAuthException>(() => _client.CompleteLoginAsync(state, "thecode", CancellationToken.None));
    }

    [Fact]
    public async Task LikedSongs_ReadTrackAndSnapshot()
    {
        await Connect();
        _http.Handler = (req, _) => req.RequestUri!.Query.Contains("limit=1&", StringComparison.Ordinal) || req.RequestUri.Query.EndsWith("limit=1", StringComparison.Ordinal)
            ? FakeHttp.Json("{\"items\":[{\"added_at\":\"2026-01-01T00:00:00Z\",\"track\":{\"id\":\"t1\",\"type\":\"track\"}}],\"next\":null,\"total\":42}")
            : FakeHttp.Json("{\"items\":[{\"added_at\":\"x\",\"track\":{\"id\":\"t1\",\"name\":\"S\",\"type\":\"track\"}},{\"track\":{\"id\":\"e1\",\"type\":\"episode\"}}],\"next\":null,\"total\":2}");
        Assert.Equal("42:2026-01-01T00:00:00Z:t1", await _client.GetLikedSnapshotAsync(CancellationToken.None));
        var liked = await _client.GetLikedTracksAsync(CancellationToken.None);
        Assert.Single(liked);
    }

    [Fact]
    public async Task PlaylistList_ReadsNewItemsTotal()
    {
        await Connect();
        _http.Handler = (_, _) => FakeHttp.Json("{\"items\":[{\"id\":\"p1\",\"name\":\"Mix\",\"owner\":{\"id\":\"me\"},\"snapshot_id\":\"s\",\"items\":{\"total\":250}},{\"id\":\"p2\",\"name\":\"Old\",\"tracks\":{\"total\":7}}],\"next\":null}");
        var lists = await _client.GetPlaylistsAsync(CancellationToken.None);
        Assert.Equal(250, lists[0].Total);
        Assert.Equal(7, lists[1].Total);
    }
}
