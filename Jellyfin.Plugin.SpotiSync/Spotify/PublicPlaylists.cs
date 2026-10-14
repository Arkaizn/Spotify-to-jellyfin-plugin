using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SpotiSync.Spotify;

/// <summary>A playlist read without a Spotify account.</summary>
/// <param name="Name">Playlist name.</param>
/// <param name="Image">Cover image url.</param>
/// <param name="Tracks">Tracks in order.</param>
/// <param name="Capped">True when Spotify only returned part of the playlist.</param>
public sealed record PublicPlaylist(string Name, string? Image, List<SpTrack> Tracks, bool Capped);

/// <summary>Reads public playlists without an account.</summary>
public interface IPublicPlaylistSource
{
    /// <summary>Reads a public playlist.</summary>
    /// <param name="spotifyId">Spotify playlist id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The playlist.</returns>
    Task<PublicPlaylist> GetAsync(string spotifyId, CancellationToken ct);
}

/// <summary>
/// Reads public playlists from Spotify's embed page (the player websites embed). It needs no
/// login, but Spotify only puts the first 100 songs of a playlist on that page.
/// </summary>
public sealed partial class SpotifyPublicClient : IPublicPlaylistSource
{
    /// <summary>Songs Spotify shows on a public embed page at most.</summary>
    public const int EmbedLimit = 100;

    private static readonly string WebBase = (Environment.GetEnvironmentVariable("SPOTISYNC_SPOTIFY_WEB_URL") ?? "https://open.spotify.com").TrimEnd('/');

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _clientName;

    /// <summary>Initializes a new instance of the <see cref="SpotifyPublicClient"/> class.</summary>
    /// <param name="httpFactory">HTTP client factory.</param>
    /// <param name="clientName">Named client.</param>
    public SpotifyPublicClient(IHttpClientFactory httpFactory, string clientName)
    {
        _httpFactory = httpFactory;
        _clientName = clientName;
    }

    /// <inheritdoc />
    public async Task<PublicPlaylist> GetAsync(string spotifyId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, WebBase + "/embed/playlist/" + Uri.EscapeDataString(spotifyId));
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var resp = await _httpFactory.CreateClient(_clientName).SendAsync(req, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode == 404)
        {
            throw new SpotifyApiException(404, "Playlist not found. Is it public?");
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new SpotifyApiException((int)resp.StatusCode, "Spotify returned " + (int)resp.StatusCode + " for the public playlist page");
        }

        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseEmbed(html);
    }

    /// <summary>Extracts the playlist id from a link, URI or bare id.</summary>
    /// <param name="input">e.g. https://open.spotify.com/playlist/ID?si=..., spotify:playlist:ID or ID.</param>
    /// <returns>The id, or null when none was found.</returns>
    public static string? ParsePlaylistId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var m = PlaylistIdRegex().Match(input.Trim());
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>Parses the embed page HTML.</summary>
    /// <param name="html">Page HTML.</param>
    /// <returns>The playlist.</returns>
    public static PublicPlaylist ParseEmbed(string html)
    {
        const string Marker = "<script id=\"__NEXT_DATA__\" type=\"application/json\">";
        int start = html.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new SpotifyApiException(0, "Spotify's public playlist page has an unexpected format (no data block).");
        }

        start += Marker.Length;
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new SpotifyApiException(0, "Spotify's public playlist page has an unexpected format (unterminated data block).");
        }

        using var doc = JsonDocument.Parse(html.AsMemory(start, end - start));
        var entity = FindEntity(doc.RootElement, 0)
            ?? throw new SpotifyApiException(0, "Spotify's public playlist page has no track list. Is the playlist public?");

        string name = Str(entity, "name") ?? Str(entity, "title") ?? "Spotify playlist";
        string? image = null;
        if (entity.TryGetProperty("coverArt", out var cover) && cover.ValueKind == JsonValueKind.Object
            && cover.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            int best = int.MaxValue;
            foreach (var src in sources.EnumerateArray())
            {
                var url = Str(src, "url");
                int w = src.TryGetProperty("width", out var we) && we.ValueKind == JsonValueKind.Number ? we.GetInt32() : 1000;
                if (url is not null && w < best)
                {
                    best = w;
                    image = url;
                }
            }
        }

        var tracks = new List<SpTrack>();
        foreach (var item in entity.GetProperty("trackList").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var uri = Str(item, "uri") ?? string.Empty;
            if (uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            {
                continue;
            }

            bool local = uri.StartsWith("spotify:local:", StringComparison.Ordinal);
            var t = new SpTrack
            {
                Id = uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri["spotify:track:".Length..] : null,
                Uri = uri.Length > 0 ? uri : null,
                Name = Str(item, "title") ?? Str(item, "name") ?? string.Empty,
                Type = "track",
                IsLocal = local,
                DurationMs = Int(item, "duration") ?? Int(item, "duration_ms") ?? 0,
                Artists = Artists(item),
            };
            if (t.Name.Length > 0)
            {
                tracks.Add(t);
            }
        }

        return new PublicPlaylist(name, image, tracks, tracks.Count >= EmbedLimit);
    }

    private static List<SpArtist> Artists(JsonElement item)
    {
        var list = new List<SpArtist>();
        if (item.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var n = a.ValueKind == JsonValueKind.String ? a.GetString() : Str(a, "name");
                if (!string.IsNullOrWhiteSpace(n))
                {
                    list.Add(new SpArtist { Name = n });
                }
            }

            if (list.Count > 0)
            {
                return list;
            }
        }

        // The embed joins artist names with ", ".
        var subtitle = Str(item, "subtitle");
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            foreach (var part in ArtistSplitRegex().Split(subtitle.Replace(' ', ' ')))
            {
                if (part.Trim().Length > 0)
                {
                    list.Add(new SpArtist { Name = part.Trim() });
                }
            }
        }

        return list;
    }

    private static JsonElement? FindEntity(JsonElement e, int depth)
    {
        if (depth > 12)
        {
            return null;
        }

        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("trackList", out var tl) && tl.ValueKind == JsonValueKind.Array)
            {
                return e;
            }

            foreach (var p in e.EnumerateObject())
            {
                if (FindEntity(p.Value, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in e.EnumerateArray())
            {
                if (FindEntity(v, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    internal static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    [GeneratedRegex(@"(?:playlist[/:])(?<id>[A-Za-z0-9]{10,40})|^(?<id>[A-Za-z0-9]{22})$")]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@",\s*")]
    private static partial Regex ArtistSplitRegex();
}
