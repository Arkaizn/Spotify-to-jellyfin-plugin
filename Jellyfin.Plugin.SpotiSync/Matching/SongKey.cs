using System;

namespace Jellyfin.Plugin.SpotiSync.Matching;

/// <summary>
/// Identity of a song for duplicate detection: cleaned title + main artist + version tags.
/// Two different files of the same recording (album vs. single, remaster) get the same key;
/// a live or remix version does not.
/// </summary>
public static class SongKey
{
    /// <summary>Key from a title and its main artist.</summary>
    /// <param name="title">Raw title.</param>
    /// <param name="artist">Raw main artist.</param>
    /// <returns>The key, or null without a title.</returns>
    public static string? Of(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var clean = TitleCleaner.Clean(title);
        var main = artist ?? string.Empty;
        int comma = main.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            main = main[..comma];
        }

        return clean.Base + "|" + TextNorm.NormalizeArtist(main) + "|" + ((int)clean.Tags).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Key from a display text "Title — Artist, Artist · Album" (as stored for matches).</summary>
    /// <param name="text">Display text.</param>
    /// <returns>The key, or null.</returns>
    public static string? FromText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int dash = text.IndexOf(" — ", StringComparison.Ordinal);
        string title = dash >= 0 ? text[..dash] : text;
        string artist = string.Empty;
        if (dash >= 0)
        {
            artist = text[(dash + 3)..];
            int dot = artist.IndexOf(" · ", StringComparison.Ordinal);
            if (dot >= 0)
            {
                artist = artist[..dot];
            }
        }
        else
        {
            int dot = title.IndexOf(" · ", StringComparison.Ordinal);
            if (dot >= 0)
            {
                title = title[..dot];
            }
        }

        return Of(title, artist);
    }
}
