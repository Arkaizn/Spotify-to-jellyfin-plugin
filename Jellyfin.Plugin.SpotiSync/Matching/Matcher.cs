using System;
using System.Collections.Generic;
using System.Numerics;

namespace Jellyfin.Plugin.SpotiSync.Matching;

/// <summary>Outcome class of a match.</summary>
public enum MatchKind
{
    /// <summary>No usable candidate.</summary>
    NotFound = 0,

    /// <summary>A plausible candidate that a human should confirm.</summary>
    Review = 1,

    /// <summary>Confident match.</summary>
    Matched = 2,
}

/// <summary>Spotify-side track prepared for matching (normalized once).</summary>
public sealed class MatchQuery
{
    /// <summary>Initializes a new instance of the <see cref="MatchQuery"/> class.</summary>
    /// <param name="title">Raw title.</param>
    /// <param name="artists">Raw artists.</param>
    /// <param name="album">Raw album.</param>
    /// <param name="durationMs">Duration in ms (0 = unknown).</param>
    /// <param name="isrc">ISRC or null.</param>
    public MatchQuery(string? title, IReadOnlyList<string>? artists, string? album, int durationMs, string? isrc)
    {
        var clean = TitleCleaner.Clean(title);
        TitleBase = clean.Base;
        TitleSorted = TextNorm.SortedTokens(clean.Base);
        Tags = clean.Tags | TitleCleaner.AlbumTags(album);
        Numbers = clean.Numbers;
        AlbumNorm = string.IsNullOrEmpty(album) ? string.Empty : TitleCleaner.Clean(album).Base;
        DurationSec = durationMs > 0 ? (int)Math.Round(durationMs / 1000.0) : 0;
        Isrc = string.IsNullOrWhiteSpace(isrc) ? null : isrc.Trim();

        var raw = new List<string>((artists?.Count ?? 0) + clean.Featured.Count);
        if (artists is not null)
        {
            raw.AddRange(artists);
        }

        // Spotify lists the primary artist first; keep that order so index 0 is the primary.
        var split = new List<string>(raw.Count + 2);
        TextNorm.SplitArtists(raw, split);
        var featured = new List<string>();
        TextNorm.SplitArtists(clean.Featured, featured);
        foreach (var f in featured)
        {
            if (!split.Contains(f))
            {
                split.Add(f);
            }
        }

        Artists = split.ToArray();
    }

    /// <summary>Gets the cleaned normalized title.</summary>
    public string TitleBase { get; }

    /// <summary>Gets the sorted distinct title tokens.</summary>
    public string TitleSorted { get; }

    /// <summary>Gets the version tags.</summary>
    public TrackTags Tags { get; }

    /// <summary>Gets the number signature.</summary>
    public string? Numbers { get; }

    /// <summary>Gets the cleaned normalized album.</summary>
    public string AlbumNorm { get; }

    /// <summary>Gets the duration in seconds.</summary>
    public int DurationSec { get; }

    /// <summary>Gets the normalized artists, primary first.</summary>
    public string[] Artists { get; }

    /// <summary>Gets the ISRC.</summary>
    public string? Isrc { get; }
}

/// <summary>Result of matching one track.</summary>
/// <param name="Index">Best library item index, or -1.</param>
/// <param name="Score">Score of that item.</param>
/// <param name="Kind">Classification.</param>
public readonly record struct MatchResult(int Index, double Score, MatchKind Kind);

/// <summary>
/// Scores Spotify tracks against a <see cref="LibraryIndex"/>. Not thread safe: one instance
/// per sync, reused for every track (its scratch buffers are sized to the library once).
/// </summary>
public sealed class Matcher
{
    /// <summary>Score at or above which a match is accepted automatically.</summary>
    public const double MatchThreshold = 0.82;

    /// <summary>Score at or above which a candidate is offered as a suggestion.</summary>
    public const double ReviewThreshold = 0.60;

    /// <summary>Maximum candidates scored per track.</summary>
    public const int MaxCandidates = 64;

    /// <summary>Bump when scoring changes so stored matches get recomputed.</summary>
    public const int Version = 1;

    internal const double WeightTitle = 0.50;
    internal const double WeightArtist = 0.30;
    internal const double WeightDuration = 0.15;
    internal const double WeightAlbum = 0.05;
    internal const double TagPenalty = 0.20;
    internal const double TagPenaltyMax = 0.40;
    internal const double NumberPenalty = 0.25;

    private readonly LibraryIndex _index;
    private readonly float[] _acc;
    private readonly int[] _touched;
    private readonly float[] _candKeys;
    private readonly int[] _candItems;

    /// <summary>Initializes a new instance of the <see cref="Matcher"/> class.</summary>
    /// <param name="index">Library index.</param>
    public Matcher(LibraryIndex index)
    {
        _index = index;
        _acc = new float[index.Count];
        _touched = new int[index.Count];
        _candKeys = new float[index.Count];
        _candItems = new int[index.Count];
    }

    /// <summary>Finds the best library item for a track.</summary>
    /// <param name="q">Prepared track.</param>
    /// <returns>Best match.</returns>
    public MatchResult Match(MatchQuery q)
    {
        var x = _index;
        if (q.Isrc is not null && x.IsrcIndex is not null && x.IsrcIndex.TryGetValue(q.Isrc, out int byIsrc))
        {
            return new MatchResult(byIsrc, 1.0, MatchKind.Matched);
        }

        int n = CollectCandidates(q);
        int best = -1;
        double bestScore = 0;
        for (int c = 0; c < n; c++)
        {
            int i = _candItems[c];
            double s = Score(q, x, i, bestScore);
            if (s > bestScore)
            {
                bestScore = s;
                best = i;
            }
        }

        var kind = bestScore >= MatchThreshold ? MatchKind.Matched : bestScore >= ReviewThreshold ? MatchKind.Review : MatchKind.NotFound;
        return new MatchResult(kind == MatchKind.NotFound ? -1 : best, bestScore, kind);
    }

    /// <summary>
    /// Selects candidates through the inverted index: items sharing a title token, weighted by
    /// token rarity, boosted when an artist token is shared too, capped at <see cref="MaxCandidates"/>.
    /// </summary>
    private int CollectCandidates(MatchQuery q)
    {
        var x = _index;
        int touched = 0;
        int total = Math.Max(1, x.Count);

        // Skip very common title tokens ("the", "love", "i") when the title has rarer ones.
        Span<int> tids = stackalloc int[32];
        Span<int> dfs = stackalloc int[32];
        int nt = 0;
        int minDf = int.MaxValue;
        var e = new Fuzzy.TokenEnumerator(q.TitleSorted);
        while (e.MoveNext() && nt < 32)
        {
            int tid = x.TokenId(e.Current);
            if (tid < 0)
            {
                continue;
            }

            int df = x.TitlePostStart[tid + 1] - x.TitlePostStart[tid];
            if (df == 0)
            {
                continue;
            }

            tids[nt] = tid;
            dfs[nt++] = df;
            minDf = Math.Min(minDf, df);
        }

        int commonCutoff = Math.Max(total / 20, 200);
        for (int k = 0; k < nt; k++)
        {
            if (dfs[k] > commonCutoff && minDf <= commonCutoff)
            {
                continue;
            }

            float w = (float)Math.Log(1.0 + ((double)total / dfs[k]));
            foreach (int i in x.TitlePostings(tids[k]))
            {
                if (_acc[i] == 0)
                {
                    _touched[touched++] = i;
                }

                _acc[i] += w;
            }
        }

        if (touched == 0)
        {
            return 0;
        }

        // Artist boost only for items already reached through the title.
        foreach (var artist in q.Artists)
        {
            var ae = new Fuzzy.TokenEnumerator(artist);
            while (ae.MoveNext())
            {
                int tid = x.TokenId(ae.Current);
                if (tid < 0)
                {
                    continue;
                }

                // Very common artist words ("dj", "band", "orchestra") would touch most of the
                // library for little ranking value.
                var postings = x.ArtistPostings(tid);
                if (postings.Length > commonCutoff)
                {
                    continue;
                }

                foreach (int i in postings)
                {
                    if (_acc[i] > 0)
                    {
                        _acc[i] += 1.5f;
                    }
                }
            }
        }

        int n;
        if (touched <= MaxCandidates)
        {
            for (int k = 0; k < touched; k++)
            {
                _candItems[k] = _touched[k];
            }

            n = touched;
        }
        else
        {
            for (int k = 0; k < touched; k++)
            {
                _candKeys[k] = -_acc[_touched[k]];
                _candItems[k] = _touched[k];
            }

            SelectTop(_candKeys, _candItems, touched, MaxCandidates);
            n = MaxCandidates;
        }

        for (int k = 0; k < touched; k++)
        {
            _acc[_touched[k]] = 0;
        }

        return n;
    }

    /// <summary>Full score of library item <paramref name="i"/> for query <paramref name="q"/>.</summary>
    /// <param name="q">Prepared track.</param>
    /// <param name="x">Index.</param>
    /// <param name="i">Item index.</param>
    /// <returns>Score in [0, 1].</returns>
    public static double Score(MatchQuery q, LibraryIndex x, int i) => Score(q, x, i, -1);

    /// <summary>
    /// Score, or a value &lt;= <paramref name="beat"/> as soon as an upper bound shows the item
    /// cannot beat it (skips the artist / album comparisons for hopeless candidates).
    /// </summary>
    /// <param name="q">Prepared track.</param>
    /// <param name="x">Index.</param>
    /// <param name="i">Item index.</param>
    /// <param name="beat">Score to beat.</param>
    /// <returns>Score in [0, 1].</returns>
    public static double Score(MatchQuery q, LibraryIndex x, int i, double beat)
    {
        double title = TitleScore(q.TitleBase, q.TitleSorted, x.TitleBase[i], x.TitleSorted[i]);
        double duration = DurationScore(q.DurationSec, x.DurationSec[i]);
        if (beat >= 0)
        {
            double upper = ApplyPenalties((WeightTitle * title) + WeightArtist + (WeightDuration * duration) + WeightAlbum, title, 1, q.Tags, x.Tags[i], q.Numbers, x.Numbers[i]);
            if (upper <= beat)
            {
                return 0;
            }
        }

        double artist = ArtistScore(q.Artists, x.ArtistNorm.AsSpan(x.ArtistStart[i], x.ArtistStart[i + 1] - x.ArtistStart[i]));
        string album = x.AlbumNorm[i];
        double albumScore = q.AlbumNorm.Length > 0 && album.Length > 0 ? Fuzzy.Ratio(q.AlbumNorm, album) : 0.5;

        double s = (WeightTitle * title) + (WeightArtist * artist) + (WeightDuration * duration) + (WeightAlbum * albumScore);
        return ApplyPenalties(s, title, artist, q.Tags, x.Tags[i], q.Numbers, x.Numbers[i]);
    }

    /// <summary>Moves the <paramref name="k"/> smallest keys (with their items) to the front (quickselect).</summary>
    private static void SelectTop(float[] keys, int[] items, int n, int k)
    {
        int lo = 0, hi = n - 1;
        var rnd = 12345u;
        while (lo < hi)
        {
            rnd = (rnd * 1103515245u) + 12345u;
            float pivot = keys[lo + (int)(rnd % (uint)(hi - lo + 1))];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (keys[i] < pivot)
                {
                    i++;
                }

                while (keys[j] > pivot)
                {
                    j--;
                }

                if (i <= j)
                {
                    (keys[i], keys[j]) = (keys[j], keys[i]);
                    (items[i], items[j]) = (items[j], items[i]);
                    i++;
                    j--;
                }
            }

            if (k - 1 <= j)
            {
                hi = j;
            }
            else if (k - 1 >= i)
            {
                lo = i;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Applies tag / number penalties and the title / artist caps.</summary>
    /// <param name="s">Weighted score.</param>
    /// <param name="title">Title score.</param>
    /// <param name="artist">Artist score.</param>
    /// <param name="tagsA">Tags of a.</param>
    /// <param name="tagsB">Tags of b.</param>
    /// <param name="numbersA">Numbers of a.</param>
    /// <param name="numbersB">Numbers of b.</param>
    /// <returns>Final score.</returns>
    internal static double ApplyPenalties(double s, double title, double artist, TrackTags tagsA, TrackTags tagsB, string? numbersA, string? numbersB)
    {
        int tagDiff = BitOperations.PopCount((uint)(tagsA ^ tagsB));
        s -= Math.Min(TagPenaltyMax, TagPenalty * tagDiff);
        if (!string.Equals(numbersA, numbersB, StringComparison.Ordinal))
        {
            s -= NumberPenalty;
        }

        if (title < 0.60)
        {
            s = Math.Min(s, 0.55);
        }

        if (artist < 0.50)
        {
            s = Math.Min(s, 0.75);
        }

        return Math.Clamp(s, 0.0, 1.0);
    }

    /// <summary>Title similarity: best of plain ratio, token sort and (damped) token set.</summary>
    /// <param name="baseA">Cleaned title a.</param>
    /// <param name="sortedA">Sorted tokens a.</param>
    /// <param name="baseB">Cleaned title b.</param>
    /// <param name="sortedB">Sorted tokens b.</param>
    /// <returns>Similarity.</returns>
    public static double TitleScore(string baseA, string sortedA, string baseB, string sortedB)
    {
        if (string.Equals(baseA, baseB, StringComparison.Ordinal))
        {
            return 1.0;
        }

        double r = Fuzzy.Ratio(baseA, baseB);
        double ts = ReferenceEquals(sortedA, baseA) && ReferenceEquals(sortedB, baseB) ? r : Fuzzy.TokenSortRatio(sortedA, sortedB);
        double set = 0.9 * Fuzzy.TokenSetRatio(sortedA, sortedB);
        return Math.Max(r, Math.Max(ts, set));
    }

    /// <summary>
    /// Artist similarity: how well the primary Spotify artist is covered, or (slightly damped)
    /// any Spotify artist. Neutral 0.5 when either side has no artists.
    /// </summary>
    /// <param name="spotify">Normalized Spotify artists, primary first.</param>
    /// <param name="library">Normalized library artists.</param>
    /// <returns>Similarity.</returns>
    public static double ArtistScore(string[] spotify, ReadOnlySpan<string> library)
    {
        if (spotify.Length == 0 || library.Length == 0)
        {
            return 0.5;
        }

        double primary = 0, any = 0;
        for (int k = 0; k < spotify.Length; k++)
        {
            string a = spotify[k];
            double best = 0;
            foreach (var l in library)
            {
                double r = string.Equals(a, l, StringComparison.Ordinal) ? 1.0 : Fuzzy.Ratio(a, l);
                if (r > best)
                {
                    best = r;
                    if (best >= 1.0)
                    {
                        break;
                    }
                }
            }

            if (k == 0)
            {
                primary = best;
            }

            any = Math.Max(any, best);
            if (primary >= 1.0)
            {
                break;
            }
        }

        return Math.Max(primary, 0.85 * any);
    }

    /// <summary>Duration similarity from the absolute difference in seconds.</summary>
    /// <param name="a">Seconds a (0 = unknown).</param>
    /// <param name="b">Seconds b (0 = unknown).</param>
    /// <returns>Similarity.</returns>
    public static double DurationScore(int a, int b)
    {
        if (a <= 0 || b <= 0)
        {
            return 0.5;
        }

        int d = Math.Abs(a - b);
        return d <= 2 ? 1.0 : d <= 5 ? 0.9 : d <= 10 ? 0.7 : d <= 20 ? 0.4 : d <= 40 ? 0.2 : 0.0;
    }
}
