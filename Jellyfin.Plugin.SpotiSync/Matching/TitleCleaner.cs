using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.SpotiSync.Matching;

/// <summary>Version markers that make two recordings of the same song different tracks.</summary>
[Flags]
public enum TrackTags : ushort
{
    /// <summary>No marker.</summary>
    None = 0,

    /// <summary>Live recording.</summary>
    Live = 1,

    /// <summary>Remix / rework / bootleg.</summary>
    Remix = 2,

    /// <summary>Acoustic or unplugged.</summary>
    Acoustic = 4,

    /// <summary>Instrumental.</summary>
    Instrumental = 8,

    /// <summary>Demo.</summary>
    Demo = 16,

    /// <summary>Karaoke.</summary>
    Karaoke = 32,

    /// <summary>Extended version / mix.</summary>
    Extended = 64,

    /// <summary>Reprise.</summary>
    Reprise = 128,

    /// <summary>Sped up / nightcore.</summary>
    SpedUp = 256,

    /// <summary>Slowed (+ reverb).</summary>
    Slowed = 512,

    /// <summary>A cappella.</summary>
    Acapella = 1024,

    /// <summary>Orchestral / symphonic version.</summary>
    Orchestral = 2048,
}

/// <summary>Result of cleaning a title.</summary>
/// <param name="Base">Normalized title without version suffixes and featured artists.</param>
/// <param name="Tags">Significant version markers found in the removed parts.</param>
/// <param name="Numbers">Sorted digit tokens of <paramref name="Base"/>, or null when there are none.</param>
/// <param name="Featured">Raw featured artist names pulled out of the title.</param>
public readonly record struct CleanTitle(string Base, TrackTags Tags, string? Numbers, IReadOnlyList<string> Featured);

/// <summary>
/// Strips decoration from song titles so "Song - Remastered 2011", "Song (feat. X)" and
/// "Song [Radio Edit]" all reduce to "song", while recording that a part said "live" or "remix".
/// </summary>
public static class TitleCleaner
{
    // Words that mark a bracket group or " - " suffix as version information rather than part of the name.
    private static readonly HashSet<string> VersionWords = new(StringComparer.Ordinal)
    {
        "remaster", "remastered", "remasterizado", "remasterisee", "live", "remix", "remixed", "rmx", "mix", "mixed",
        "edit", "version", "versao", "mono", "stereo", "acoustic", "acustico", "instrumental", "demo",
        "from", "feat", "ft", "featuring", "recorded", "session", "sessions", "take", "single", "bonus", "anniversary",
        "unplugged", "extended", "cut", "reprise", "karaoke", "slowed", "sped", "nightcore", "reverb", "acapella",
        "cappella", "capella", "orchestral", "symphonic", "rework", "reworked", "bootleg", "vip", "rerecorded",
        "deluxe", "explicit", "clean", "original", "radio", "album", "soundtrack", "ost", "theme", "intro", "outro",
        "interlude", "skit", "dub", "rmstr", "digital", "mastered", "taylors", "re", "reimagined", "orchestra",
    };

    /// <summary>Cleans a raw title.</summary>
    /// <param name="raw">Raw title.</param>
    /// <returns>The cleaned title.</returns>
    public static CleanTitle Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new CleanTitle(string.Empty, TrackTags.None, null, Array.Empty<string>());
        }

        var featured = new List<string>(0);
        var tags = TrackTags.None;
        var sb = new StringBuilder(raw.Length);

        // 1. Bracket groups. A group at the very start is part of the name: "(I Can't Get No) Satisfaction".
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            char close = c switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
            if (close != '\0')
            {
                int end = raw.IndexOf(close, i + 1);
                if (end > i)
                {
                    var inner = raw.AsSpan(i + 1, end - i - 1).ToString();
                    bool atStart = sb.ToString().Trim().Length == 0;
                    if (!TryFeatured(inner, featured))
                    {
                        if (atStart && !IsVersionText(inner))
                        {
                            sb.Append(' ').Append(inner).Append(' ');
                        }
                        else
                        {
                            // Version info ("Live", "2011 Remaster") and other trailing bracket text are
                            // dropped; both sides of a comparison are cleaned the same way.
                            tags |= DetectTags(inner);
                        }
                    }

                    i = end + 1;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        string text = sb.ToString();

        // 2. " - suffix" parts that describe the version: "Song - Live at Wembley", "Song - 2011 Remaster".
        while (true)
        {
            int dash = LastDash(text);
            if (dash <= 0)
            {
                break;
            }

            var suffix = text[(dash + 3)..];
            if (TryFeatured(suffix, featured))
            {
                text = text[..dash];
                continue;
            }

            if (!IsVersionText(suffix))
            {
                break;
            }

            tags |= DetectTags(suffix);
            text = text[..dash];
        }

        // 3. Inline "feat." without brackets.
        int feat = FindInlineFeat(text);
        if (feat > 0)
        {
            TryFeatured(text[feat..].Trim(), featured);
            text = text[..feat];
        }

        string baseNorm = TextNorm.Normalize(text);
        if (baseNorm.Length == 0)
        {
            baseNorm = TextNorm.Normalize(raw);
        }

        return new CleanTitle(baseNorm, tags, ExtractNumbers(baseNorm), featured.Count == 0 ? Array.Empty<string>() : featured);
    }

    /// <summary>Album names carry the live / unplugged information that library titles often lack.</summary>
    /// <param name="album">Raw album name.</param>
    /// <returns>Live / acoustic tags implied by the album.</returns>
    public static TrackTags AlbumTags(string? album)
    {
        if (string.IsNullOrEmpty(album))
        {
            return TrackTags.None;
        }

        var norm = TextNorm.Normalize(album);
        var tags = TrackTags.None;
        foreach (var tok in norm.Split(' '))
        {
            if (tok == "live")
            {
                tags |= TrackTags.Live;
            }
            else if (tok == "unplugged")
            {
                tags |= TrackTags.Live | TrackTags.Acoustic;
            }
        }

        return tags;
    }

    /// <summary>Detects significant version markers in a piece of text.</summary>
    /// <param name="text">Raw text, e.g. the content of a bracket group.</param>
    /// <returns>Tags found.</returns>
    public static TrackTags DetectTags(string text)
    {
        var norm = TextNorm.Normalize(text);
        if (norm.Length == 0)
        {
            return TrackTags.None;
        }

        var toks = norm.Split(' ');
        var tags = TrackTags.None;
        for (int k = 0; k < toks.Length; k++)
        {
            string t = toks[k];
            string prev = k > 0 ? toks[k - 1] : string.Empty;
            string next = k + 1 < toks.Length ? toks[k + 1] : string.Empty;
            switch (t)
            {
                case "live": tags |= TrackTags.Live; break;
                case "remix": case "remixed": case "rmx": case "rework": case "reworked": case "bootleg": case "vip": case "dub":
                    tags |= TrackTags.Remix; break;
                case "mix":
                    // "Original Mix", "Radio Mix", "Album Mix", "Main Mix" are just the normal track.
                    if (prev != "original" && prev != "radio" && prev != "album" && prev != "main" && prev != "single" && prev != "stereo" && prev != "mono")
                    {
                        tags |= prev == "extended" ? TrackTags.Extended : TrackTags.Remix;
                    }

                    break;
                case "acoustic": case "acustico": tags |= TrackTags.Acoustic; break;
                case "unplugged": tags |= TrackTags.Acoustic | TrackTags.Live; break;
                case "instrumental": tags |= TrackTags.Instrumental; break;
                case "demo": tags |= TrackTags.Demo; break;
                case "karaoke": tags |= TrackTags.Karaoke; break;
                case "extended": tags |= TrackTags.Extended; break;
                case "reprise": tags |= TrackTags.Reprise; break;
                case "nightcore": tags |= TrackTags.SpedUp; break;
                case "sped": case "speed": if (next == "up") { tags |= TrackTags.SpedUp; } break;
                case "slowed": tags |= TrackTags.Slowed; break;
                case "acapella": tags |= TrackTags.Acapella; break;
                case "cappella": case "capella": if (prev == "a") { tags |= TrackTags.Acapella; } break;
                case "orchestral": case "symphonic": tags |= TrackTags.Orchestral; break;
            }
        }

        return tags;
    }

    private static bool IsVersionText(string text)
    {
        var norm = TextNorm.Normalize(text);
        if (norm.Length == 0)
        {
            return true;
        }

        foreach (var tok in norm.Split(' '))
        {
            if (VersionWords.Contains(tok))
            {
                return true;
            }

            // a bare year such as "- 2011" or "(1997)"
            if (tok.Length == 4 && (tok.StartsWith("19", StringComparison.Ordinal) || tok.StartsWith("20", StringComparison.Ordinal)) && IsDigits(tok) && norm.Length == 4)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFeatured(string text, List<string> featured)
    {
        var t = text.TrimStart();
        string[] prefixes = ["feat.", "feat ", "ft.", "ft ", "featuring ", "with "];
        foreach (var p in prefixes)
        {
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                var names = t[p.Length..].Trim();
                if (names.Length > 0)
                {
                    featured.Add(names);
                }

                return true;
            }
        }

        return false;
    }

    private static int LastDash(string text)
    {
        int best = -1;
        foreach (var d in new[] { " - ", " – ", " — " })
        {
            int k = text.LastIndexOf(d, StringComparison.Ordinal);
            if (k > best)
            {
                best = k;
            }
        }

        return best;
    }

    private static int FindInlineFeat(string text)
    {
        int best = -1;
        foreach (var marker in new[] { " feat. ", " feat ", " ft. ", " featuring " })
        {
            int k = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (k > 0 && (best < 0 || k < best))
            {
                best = k;
            }
        }

        return best;
    }

    private static string? ExtractNumbers(string norm)
    {
        List<string>? nums = null;
        foreach (var tok in norm.Split(' '))
        {
            if (tok.Length > 0 && IsDigits(tok))
            {
                nums ??= new List<string>(2);
                var trimmed = tok.TrimStart('0');
                nums.Add(trimmed.Length == 0 ? "0" : trimmed);
            }
        }

        if (nums is null)
        {
            return null;
        }

        nums.Sort(StringComparer.Ordinal);
        return string.Join(' ', nums);
    }

    private static bool IsDigits(string s)
    {
        foreach (char c in s)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }
}
