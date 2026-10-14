using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.SpotiSync.Spotify;

/// <summary>A playlist found in a Spotify data export.</summary>
/// <param name="Name">Name.</param>
/// <param name="Tracks">Tracks.</param>
public sealed record ExportedPlaylist(string Name, List<SpTrack> Tracks);

/// <summary>
/// Reads the JSON files of Spotify's "Download your data" (Account → Privacy):
/// Playlist1.json, Playlist2.json, ... and YourLibrary.json (liked songs).
/// </summary>
public static class SpotifyExport
{
    /// <summary>Name used for liked songs from YourLibrary.json.</summary>
    public const string LikedName = "Liked Songs (imported)";

    /// <summary>Parses one export file.</summary>
    /// <param name="root">Parsed JSON.</param>
    /// <returns>Playlists found (empty when the file is not a playlist / library file).</returns>
    public static List<ExportedPlaylist> Parse(JsonElement root)
    {
        var result = new List<ExportedPlaylist>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (root.TryGetProperty("playlists", out var pls) && pls.ValueKind == JsonValueKind.Array)
        {
            foreach (var pl in pls.EnumerateArray())
            {
                var name = SpotifyPublicClient.Str(pl, "name");
                if (string.IsNullOrWhiteSpace(name) || !pl.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var tracks = new List<SpTrack>();
                foreach (var it in items.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Object
                        && Track(SpotifyPublicClient.Str(tr, "trackName"), SpotifyPublicClient.Str(tr, "artistName"), SpotifyPublicClient.Str(tr, "albumName"), SpotifyPublicClient.Str(tr, "trackUri")) is { } t)
                    {
                        tracks.Add(t);
                    }
                    else if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("localTrack", out var lt) && lt.ValueKind == JsonValueKind.Object
                        && Track(SpotifyPublicClient.Str(lt, "trackName") ?? SpotifyPublicClient.Str(lt, "name"), SpotifyPublicClient.Str(lt, "artistName"), SpotifyPublicClient.Str(lt, "albumName"), SpotifyPublicClient.Str(lt, "uri")) is { } l)
                    {
                        l.IsLocal = true;
                        tracks.Add(l);
                    }
                }

                result.Add(new ExportedPlaylist(name, tracks));
            }
        }

        if (root.TryGetProperty("tracks", out var lib) && lib.ValueKind == JsonValueKind.Array)
        {
            var tracks = new List<SpTrack>();
            foreach (var tr in lib.EnumerateArray())
            {
                if (tr.ValueKind == JsonValueKind.Object
                    && Track(SpotifyPublicClient.Str(tr, "track"), SpotifyPublicClient.Str(tr, "artist"), SpotifyPublicClient.Str(tr, "album"), SpotifyPublicClient.Str(tr, "uri")) is { } t)
                {
                    tracks.Add(t);
                }
            }

            if (tracks.Count > 0)
            {
                result.Add(new ExportedPlaylist(LikedName, tracks));
            }
        }

        return result;
    }

    private static SpTrack? Track(string? name, string? artist, string? album, string? uri)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new SpTrack
        {
            Id = uri is not null && uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri["spotify:track:".Length..] : null,
            Uri = uri ?? "local:" + name + "|" + artist,
            Name = name,
            Type = "track",
            IsLocal = uri is null || !uri.StartsWith("spotify:track:", StringComparison.Ordinal),
            Artists = string.IsNullOrWhiteSpace(artist) ? [] : [new SpArtist { Name = artist }],
            Album = string.IsNullOrWhiteSpace(album) ? null : new SpAlbum { Name = album },
        };
    }
}
