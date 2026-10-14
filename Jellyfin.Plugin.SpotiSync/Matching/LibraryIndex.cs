using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.SpotiSync.Matching;

/// <summary>One audio item as read from the library. Only the fields the matcher needs.</summary>
/// <param name="Id">Jellyfin item id.</param>
/// <param name="Name">Track title.</param>
/// <param name="Artists">Track artists.</param>
/// <param name="AlbumArtists">Album artists.</param>
/// <param name="Album">Album name.</param>
/// <param name="RunTimeTicks">Duration in ticks.</param>
/// <param name="Isrc">ISRC if the item carries one.</param>
public readonly record struct LibraryRecord(
    Guid Id,
    string? Name,
    IReadOnlyList<string>? Artists,
    IReadOnlyList<string>? AlbumArtists,
    string? Album,
    long? RunTimeTicks,
    string? Isrc);

/// <summary>A library search hit for the "Find" dialog.</summary>
/// <param name="Index">Item index.</param>
/// <param name="Score">Relevance.</param>
public readonly record struct SearchHit(int Index, double Score);

/// <summary>
/// Immutable, compact (struct-of-arrays) snapshot of the audio library with every string the
/// matcher compares already normalized, plus an inverted index token → items.
/// Built at the start of a sync and dropped afterwards.
/// </summary>
public sealed class LibraryIndex
{
    private readonly Dictionary<string, int> _tokenIds;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _tokenLookup;

    internal LibraryIndex(
        int count,
        Guid[] ids,
        string[] names,
        string[] artistDisplay,
        string[] albumDisplay,
        string[] titleBase,
        string[] titleSorted,
        TrackTags[] tags,
        string?[] numbers,
        string[] albumNorm,
        int[] durationSec,
        int[] artistStart,
        string[] artistNorm,
        Dictionary<string, int> tokenIds,
        int[] titlePostStart,
        int[] titlePosts,
        int[] artistPostStart,
        int[] artistPosts,
        Dictionary<string, int>? isrc)
    {
        Count = count;
        Ids = ids;
        Names = names;
        ArtistDisplay = artistDisplay;
        AlbumDisplay = albumDisplay;
        TitleBase = titleBase;
        TitleSorted = titleSorted;
        Tags = tags;
        Numbers = numbers;
        AlbumNorm = albumNorm;
        DurationSec = durationSec;
        ArtistStart = artistStart;
        ArtistNorm = artistNorm;
        _tokenIds = tokenIds;
        _tokenLookup = tokenIds.GetAlternateLookup<ReadOnlySpan<char>>();
        TitlePostStart = titlePostStart;
        TitlePosts = titlePosts;
        ArtistPostStart = artistPostStart;
        ArtistPosts = artistPosts;
        IsrcIndex = isrc;
        IdToIndex = new Dictionary<Guid, int>(count);
        for (int i = 0; i < count; i++)
        {
            IdToIndex.TryAdd(ids[i], i);
        }
    }

    /// <summary>Gets the number of items.</summary>
    public int Count { get; }

    /// <summary>Gets the item ids.</summary>
    public Guid[] Ids { get; }

    /// <summary>Gets the raw titles (for display).</summary>
    public string[] Names { get; }

    /// <summary>Gets the raw artists joined by ", " (for display).</summary>
    public string[] ArtistDisplay { get; }

    /// <summary>Gets the raw album names (for display).</summary>
    public string[] AlbumDisplay { get; }

    /// <summary>Gets the cleaned normalized titles.</summary>
    public string[] TitleBase { get; }

    /// <summary>Gets the sorted distinct tokens of <see cref="TitleBase"/>.</summary>
    public string[] TitleSorted { get; }

    /// <summary>Gets the version tags.</summary>
    public TrackTags[] Tags { get; }

    /// <summary>Gets the number signatures.</summary>
    public string?[] Numbers { get; }

    /// <summary>Gets the cleaned normalized album names.</summary>
    public string[] AlbumNorm { get; }

    /// <summary>Gets durations in seconds (0 = unknown).</summary>
    public int[] DurationSec { get; }

    /// <summary>Gets offsets into <see cref="ArtistNorm"/>; item i owns [ArtistStart[i], ArtistStart[i+1]).</summary>
    public int[] ArtistStart { get; }

    /// <summary>Gets all normalized artists, flat.</summary>
    public string[] ArtistNorm { get; }

    /// <summary>Gets CSR offsets of title postings per token id.</summary>
    internal int[] TitlePostStart { get; }

    /// <summary>Gets title postings (item indices).</summary>
    internal int[] TitlePosts { get; }

    /// <summary>Gets CSR offsets of artist postings per token id.</summary>
    internal int[] ArtistPostStart { get; }

    /// <summary>Gets artist postings (item indices).</summary>
    internal int[] ArtistPosts { get; }

    /// <summary>Gets ISRC → item index, or null when no item has an ISRC.</summary>
    public Dictionary<string, int>? IsrcIndex { get; }

    /// <summary>Gets item id → index.</summary>
    public Dictionary<Guid, int> IdToIndex { get; }

    /// <summary>Gets the number of distinct tokens.</summary>
    public int TokenCount => _tokenIds.Count;

    /// <summary>Looks up a token id without allocating.</summary>
    /// <param name="token">Token.</param>
    /// <returns>Token id or -1.</returns>
    public int TokenId(ReadOnlySpan<char> token) => _tokenLookup.TryGetValue(token, out int id) ? id : -1;

    /// <summary>Gets the title postings of a token.</summary>
    /// <param name="tokenId">Token id.</param>
    /// <returns>Item indices.</returns>
    public ReadOnlySpan<int> TitlePostings(int tokenId) => TitlePosts.AsSpan(TitlePostStart[tokenId], TitlePostStart[tokenId + 1] - TitlePostStart[tokenId]);

    /// <summary>Gets the artist postings of a token.</summary>
    /// <param name="tokenId">Token id.</param>
    /// <returns>Item indices.</returns>
    public ReadOnlySpan<int> ArtistPostings(int tokenId) => ArtistPosts.AsSpan(ArtistPostStart[tokenId], ArtistPostStart[tokenId + 1] - ArtistPostStart[tokenId]);

    /// <summary>
    /// Free-text search over titles and artists for the "Find" dialog. The last query word
    /// also matches as a prefix so results show up while typing.
    /// </summary>
    /// <param name="query">User query.</param>
    /// <param name="max">Maximum results.</param>
    /// <returns>Best hits, best first.</returns>
    public List<SearchHit> Search(string query, int max)
    {
        var results = new List<SearchHit>(max);
        var norm = TextNorm.Normalize(query);
        if (norm.Length == 0 || Count == 0)
        {
            return results;
        }

        var words = norm.Split(' ');
        var hits = new Dictionary<int, double>();
        for (int w = 0; w < words.Length; w++)
        {
            bool last = w == words.Length - 1;
            var matched = new HashSet<int>();
            void Collect(int tid, double weight)
            {
                foreach (var i in TitlePostings(tid))
                {
                    if (matched.Add(i))
                    {
                        hits[i] = (hits.TryGetValue(i, out var s) ? s : 0) + weight;
                    }
                }

                foreach (var i in ArtistPostings(tid))
                {
                    if (matched.Add(i))
                    {
                        hits[i] = (hits.TryGetValue(i, out var s) ? s : 0) + (weight * 0.9);
                    }
                }
            }

            int exact = TokenId(words[w]);
            if (exact >= 0)
            {
                Collect(exact, 1.0);
            }

            if (last && words[w].Length >= 2)
            {
                foreach (var kv in _tokenIds)
                {
                    if (kv.Value != exact && kv.Key.StartsWith(words[w], StringComparison.Ordinal))
                    {
                        Collect(kv.Value, 0.8);
                    }
                }
            }

            if (hits.Count > 20000)
            {
                break;
            }
        }

        var list = new List<SearchHit>(hits.Count);
        foreach (var kv in hits)
        {
            // Tie-break by how close the whole query is to the title.
            double sim = Fuzzy.Ratio(norm, TitleBase[kv.Key]);
            list.Add(new SearchHit(kv.Key, kv.Value + (0.5 * sim)));
        }

        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        for (int k = 0; k < list.Count && k < max; k++)
        {
            results.Add(list[k]);
        }

        return results;
    }

    /// <summary>Rough estimate of the managed memory held by this index, in bytes.</summary>
    /// <returns>Estimated bytes.</returns>
    public long EstimateBytes()
    {
        long b = (long)Count * (16 + (8 * 8) + 2 + 4) + ((long)ArtistNorm.Length * 8);
        b += (TitlePosts.Length + ArtistPosts.Length + TitlePostStart.Length + ArtistPostStart.Length) * 4L;
        foreach (var s in TitleBase)
        {
            b += 22 + (s.Length * 2);
        }

        return b;
    }
}

/// <summary>Builds a <see cref="LibraryIndex"/> from a stream of records in one pass.</summary>
public sealed class LibraryIndexBuilder
{
    private static readonly string[] VariousArtists = ["various artists", "various", "va", "verschiedene interpreten", "multi interpretes", "artistes divers"];

    private readonly Dictionary<string, string> _intern = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tokenIds = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _seen = new();
    private readonly List<Guid> _ids;
    private readonly List<string> _names;
    private readonly List<string> _artistDisplay;
    private readonly List<string> _albumDisplay;
    private readonly List<string> _titleBase;
    private readonly List<string> _titleSorted;
    private readonly List<TrackTags> _tags;
    private readonly List<string?> _numbers;
    private readonly List<string> _albumNorm;
    private readonly List<int> _duration;
    private readonly List<int> _artistStart;
    private readonly List<string> _artistNorm;
    private readonly List<int> _titlePairs; // tokenId, item, tokenId, item ...
    private readonly List<int> _artistPairs;
    private readonly Dictionary<string, (string Norm, TrackTags Tags)> _albumCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _artistCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _variousCache = new(StringComparer.Ordinal);
    private readonly List<string> _artistScratch = new(8);
    private Dictionary<string, int>? _isrc;

    /// <summary>Initializes a new instance of the <see cref="LibraryIndexBuilder"/> class.</summary>
    /// <param name="expectedCount">Expected number of items (capacity hint).</param>
    public LibraryIndexBuilder(int expectedCount = 1024)
    {
        int c = Math.Max(16, expectedCount);
        _ids = new(c);
        _names = new(c);
        _artistDisplay = new(c);
        _albumDisplay = new(c);
        _titleBase = new(c);
        _titleSorted = new(c);
        _tags = new(c);
        _numbers = new(c);
        _albumNorm = new(c);
        _duration = new(c);
        _artistStart = new(c + 1);
        _artistNorm = new(c * 2);
        _titlePairs = new(c * 6);
        _artistPairs = new(c * 6);
    }

    /// <summary>Adds one record. Duplicates (same id) are ignored.</summary>
    /// <param name="r">Record.</param>
    public void Add(in LibraryRecord r)
    {
        if (!_seen.Add(r.Id))
        {
            return;
        }

        int idx = _ids.Count;
        var clean = TitleCleaner.Clean(r.Name);
        var album = Album(r.Album);
        var tags = clean.Tags | album.Tags;

        _ids.Add(r.Id);
        _names.Add(r.Name ?? string.Empty);
        _titleBase.Add(clean.Base);
        var sorted = TextNorm.SortedTokens(clean.Base);
        _titleSorted.Add(ReferenceEquals(sorted, clean.Base) ? clean.Base : sorted);
        _tags.Add(tags);
        _numbers.Add(clean.Numbers is null ? null : Intern(clean.Numbers));
        _albumDisplay.Add(Intern(r.Album ?? string.Empty));
        _albumNorm.Add(album.Norm);
        _duration.Add(r.RunTimeTicks is long t && t > 0 ? (int)(t / TimeSpan.TicksPerSecond) : 0);

        // Artists: track artists, album artists (unless "Various Artists"), featured artists in the title.
        // Artist strings repeat a lot, so their split / normalized form is cached per raw string.
        _artistScratch.Clear();
        if (r.Artists is not null)
        {
            foreach (var a in r.Artists)
            {
                AddArtists(a);
            }
        }

        if (r.AlbumArtists is not null)
        {
            foreach (var a in r.AlbumArtists)
            {
                if (!IsVarious(a))
                {
                    AddArtists(a);
                }
            }
        }

        foreach (var a in clean.Featured)
        {
            AddArtists(a);
        }

        _artistStart.Add(_artistNorm.Count);
        foreach (var a in _artistScratch)
        {
            _artistNorm.Add(Intern(a));
        }

        _artistDisplay.Add(Intern(r.Artists is { Count: > 0 } ? string.Join(", ", r.Artists) : (r.AlbumArtists is { Count: > 0 } ? string.Join(", ", r.AlbumArtists) : string.Empty)));

        if (!string.IsNullOrEmpty(r.Isrc))
        {
            _isrc ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _isrc.TryAdd(r.Isrc.Trim(), idx);
        }

        // Postings: distinct title tokens, distinct artist tokens.
        AddTokens(_titleSorted[idx], idx, _titlePairs, _titlePairs.Count);
        int artistPairStart = _artistPairs.Count;
        foreach (var a in _artistScratch)
        {
            AddTokens(a, idx, _artistPairs, artistPairStart);
        }
    }

    /// <summary>Freezes the builder into an index.</summary>
    /// <returns>The index.</returns>
    public LibraryIndex Build()
    {
        int n = _ids.Count;
        _artistStart.Add(_artistNorm.Count);
        int tokens = _tokenIds.Count;
        var (tStart, tPosts) = Csr(_titlePairs, tokens);
        var (aStart, aPosts) = Csr(_artistPairs, tokens);
        _titlePairs.Clear();
        _artistPairs.Clear();
        _intern.Clear();
        _seen.Clear();
        _albumCache.Clear();
        _artistCache.Clear();
        _variousCache.Clear();

        return new LibraryIndex(
            n,
            _ids.ToArray(),
            _names.ToArray(),
            _artistDisplay.ToArray(),
            _albumDisplay.ToArray(),
            _titleBase.ToArray(),
            _titleSorted.ToArray(),
            _tags.ToArray(),
            _numbers.ToArray(),
            _albumNorm.ToArray(),
            _duration.ToArray(),
            _artistStart.ToArray(),
            _artistNorm.ToArray(),
            _tokenIds,
            tStart,
            tPosts,
            aStart,
            aPosts,
            _isrc);
    }

    /// <summary>Convenience: builds an index from a sequence.</summary>
    /// <param name="records">Records.</param>
    /// <param name="expectedCount">Capacity hint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The index.</returns>
    public static LibraryIndex Build(IEnumerable<LibraryRecord> records, int expectedCount, CancellationToken cancellationToken = default)
    {
        var b = new LibraryIndexBuilder(expectedCount);
        int k = 0;
        foreach (var r in records)
        {
            if ((++k & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            b.Add(r);
        }

        return b.Build();
    }

    private (string Norm, TrackTags Tags) Album(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (string.Empty, TrackTags.None);
        }

        if (!_albumCache.TryGetValue(raw, out var v))
        {
            v = (Intern(TitleCleaner.Clean(raw).Base), TitleCleaner.AlbumTags(raw));
            _albumCache[raw] = v;
        }

        return v;
    }

    private void AddArtists(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!_artistCache.TryGetValue(raw, out var split))
        {
            var list = new List<string>(2);
            TextNorm.SplitArtists([raw], list);
            split = new string[list.Count];
            for (int k = 0; k < split.Length; k++)
            {
                split[k] = Intern(list[k]);
            }

            _artistCache[raw] = split;
        }

        foreach (var a in split)
        {
            if (!_artistScratch.Contains(a))
            {
                _artistScratch.Add(a);
            }
        }
    }

    private bool IsVarious(string raw)
    {
        if (!_variousCache.TryGetValue(raw, out bool various))
        {
            various = Array.IndexOf(VariousArtists, TextNorm.Normalize(raw)) >= 0;
            _variousCache[raw] = various;
        }

        return various;
    }

    private void AddTokens(string text, int idx, List<int> pairs, int startCount)
    {
        var lookup = _tokenIds.GetAlternateLookup<ReadOnlySpan<char>>();
        var e = new Fuzzy.TokenEnumerator(text);
        while (e.MoveNext())
        {
            if (!lookup.TryGetValue(e.Current, out int id))
            {
                id = _tokenIds.Count;
                _tokenIds[e.Current.ToString()] = id;
            }

            // keep postings distinct per item (artist names can repeat tokens)
            bool dup = false;
            for (int p = startCount; p < pairs.Count; p += 2)
            {
                if (pairs[p] == id)
                {
                    dup = true;
                    break;
                }
            }

            if (!dup)
            {
                pairs.Add(id);
                pairs.Add(idx);
            }
        }
    }

    private static (int[] Start, int[] Posts) Csr(List<int> pairs, int tokens)
    {
        var start = new int[tokens + 1];
        for (int p = 0; p < pairs.Count; p += 2)
        {
            start[pairs[p] + 1]++;
        }

        for (int t = 0; t < tokens; t++)
        {
            start[t + 1] += start[t];
        }

        var posts = new int[pairs.Count / 2];
        var fill = new int[tokens];
        for (int p = 0; p < pairs.Count; p += 2)
        {
            int tok = pairs[p];
            posts[start[tok] + fill[tok]++] = pairs[p + 1];
        }

        return (start, posts);
    }

    private string Intern(string s)
    {
        if (s.Length == 0)
        {
            return string.Empty;
        }

        if (_intern.TryGetValue(s, out var existing))
        {
            return existing;
        }

        _intern[s] = s;
        return s;
    }
}
