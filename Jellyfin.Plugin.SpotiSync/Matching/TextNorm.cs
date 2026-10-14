using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.SpotiSync.Matching;

/// <summary>
/// String normalization shared by both sides of the matcher. Everything here runs once per
/// string (when the library index is built or a Spotify track is prepared), never inside the
/// candidate scoring loop.
/// </summary>
public static class TextNorm
{
    /// <summary>
    /// Lowercases, strips accents, drops apostrophes, turns "&amp;" into "and" and replaces every
    /// other non letter/digit with a single space.
    /// </summary>
    /// <param name="value">Raw text.</param>
    /// <returns>Normalized text, possibly empty.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Fast path: plain ASCII needs no Unicode decomposition.
        bool ascii = true;
        foreach (char c in value)
        {
            if (c > 127)
            {
                ascii = false;
                break;
            }
        }

        string source = ascii ? value : value.Normalize(NormalizationForm.FormD);
        char[]? rented = null;
        int cap = (source.Length * 5) + 2; // "&" -> " and " is the worst expansion
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : (rented = ArrayPool<char>.Shared.Rent(cap));
        int n = 0;
        bool pendingSpace = false;

        foreach (char raw in source)
        {
            char c = raw;
            if (c > 127)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark)
                {
                    continue;
                }

                switch (c)
                {
                    case '’': case '‘': case '´': case 'ʼ':
                        continue; // typographic apostrophes
                    case 'ß': Append(buf, ref n, ref pendingSpace, "ss"); continue;
                    case 'æ': case 'Æ': Append(buf, ref n, ref pendingSpace, "ae"); continue;
                    case 'œ': case 'Œ': Append(buf, ref n, ref pendingSpace, "oe"); continue;
                    case 'ø': case 'Ø': c = 'o'; break;
                    case 'ł': case 'Ł': c = 'l'; break;
                    case 'đ': case 'Đ': c = 'd'; break;
                    case 'þ': case 'Þ': Append(buf, ref n, ref pendingSpace, "th"); continue;
                }
            }

            if (c == '\'' || c == '`')
            {
                continue;
            }

            if (c == '&')
            {
                pendingSpace = n > 0;
                Append(buf, ref n, ref pendingSpace, "and");
                pendingSpace = true;
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && n > 0)
                {
                    buf[n++] = ' ';
                }

                pendingSpace = false;
                buf[n++] = char.ToLowerInvariant(c);
            }
            else
            {
                pendingSpace = n > 0;
            }
        }

        string result = new string(buf[..n]);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }

        return result;
    }

    private static void Append(Span<char> buf, ref int n, ref bool pendingSpace, string s)
    {
        if (pendingSpace && n > 0)
        {
            buf[n++] = ' ';
        }

        pendingSpace = false;
        foreach (char c in s)
        {
            buf[n++] = c;
        }
    }

    /// <summary>
    /// Returns the distinct tokens of a normalized string, sorted ordinally and joined by a space.
    /// </summary>
    /// <param name="normalized">Output of <see cref="Normalize"/>.</param>
    /// <returns>The sorted token string.</returns>
    public static string SortedTokens(string normalized)
    {
        if (normalized.IndexOf(' ', StringComparison.Ordinal) < 0)
        {
            return normalized;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(tokens, StringComparer.Ordinal);
        int w = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (w == 0 || !string.Equals(tokens[w - 1], tokens[i], StringComparison.Ordinal))
            {
                tokens[w++] = tokens[i];
            }
        }

        string joined = string.Join(' ', tokens, 0, w);
        return string.Equals(joined, normalized, StringComparison.Ordinal) ? normalized : joined;
    }

    /// <summary>
    /// Normalizes an artist name: like <see cref="Normalize"/> plus a leading "the " is dropped.
    /// </summary>
    /// <param name="value">Raw artist name.</param>
    /// <returns>Normalized artist.</returns>
    public static string NormalizeArtist(string? value)
    {
        var n = Normalize(value);
        return n.StartsWith("the ", StringComparison.Ordinal) && n.Length > 4 ? n[4..] : n;
    }

    private static readonly string[] ArtistSeparators =
    [
        ",", ";", "/", " & ", " + ", " x ", " X ", " feat. ", " feat ", " ft. ", " ft ", " featuring ",
        " Feat. ", " Feat ", " Ft. ", " Featuring ", " with ", " With ", " vs. ", " vs ", " Vs. ", " Vs ", " and ", " And "
    ];

    /// <summary>
    /// Splits raw artist strings ("A feat. B", "A, B &amp; C") into normalized individual artists.
    /// The unsplit name is kept too so bands like "Earth, Wind &amp; Fire" still match whole.
    /// </summary>
    /// <param name="raw">Raw artist strings.</param>
    /// <param name="into">Destination list (distinct values are appended).</param>
    public static void SplitArtists(IEnumerable<string?> raw, List<string> into)
    {
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r))
            {
                continue;
            }

            AddDistinct(into, NormalizeArtist(r));
            var parts = r.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                foreach (var p in parts)
                {
                    AddDistinct(into, NormalizeArtist(p));
                }
            }
        }
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        foreach (var v in list)
        {
            if (string.Equals(v, value, StringComparison.Ordinal))
            {
                return;
            }
        }

        list.Add(value);
    }
}
