using System;
using System.Buffers;
using System.Numerics;

namespace Jellyfin.Plugin.SpotiSync.Matching;

/// <summary>
/// Allocation-free fuzzy string similarity in the style of python-Levenshtein / RapidFuzz.
/// All scores are in [0, 1].
/// </summary>
public static class Fuzzy
{
    private const int StackLimit = 256;

    /// <summary>
    /// Normalized Levenshtein similarity where a substitution costs 2 (insert + delete), i.e.
    /// <c>1 - dist / (len(a) + len(b))</c>. Same definition as <c>Levenshtein.ratio</c>.
    /// </summary>
    /// <param name="a">First string.</param>
    /// <param name="b">Second string.</param>
    /// <returns>Similarity in [0, 1].</returns>
    public static double Ratio(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int total = a.Length + b.Length;
        if (total == 0)
        {
            return 1.0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        int lcs = Lcs(a, b);
        return 2.0 * lcs / total;
    }

    /// <summary>
    /// Weighted Levenshtein distance (substitution = 2) between two strings.
    /// </summary>
    /// <param name="a">First string.</param>
    /// <param name="b">Second string.</param>
    /// <returns>The distance.</returns>
    public static int IndelDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        => a.Length + b.Length - (2 * Lcs(a, b));

    /// <summary>
    /// Ratio of the two strings after their tokens are sorted. Both inputs must already be
    /// sorted token strings (see <see cref="TextNorm.SortedTokens"/>); the index precomputes them.
    /// </summary>
    /// <param name="sortedA">Sorted tokens of a.</param>
    /// <param name="sortedB">Sorted tokens of b.</param>
    /// <returns>Similarity in [0, 1].</returns>
    public static double TokenSortRatio(ReadOnlySpan<char> sortedA, ReadOnlySpan<char> sortedB)
        => Ratio(sortedA, sortedB);

    /// <summary>
    /// RapidFuzz-style token set ratio. Inputs must be distinct, ordinally sorted tokens joined by
    /// single spaces. Returns 1 when one token set contains the other.
    /// </summary>
    /// <param name="sortedA">Sorted distinct tokens of a.</param>
    /// <param name="sortedB">Sorted distinct tokens of b.</param>
    /// <returns>Similarity in [0, 1].</returns>
    public static double TokenSetRatio(ReadOnlySpan<char> sortedA, ReadOnlySpan<char> sortedB)
    {
        if (sortedA.Length == 0 || sortedB.Length == 0)
        {
            return 0.0;
        }

        int cap = sortedA.Length + sortedB.Length + 2;
        char[]? rented = null;
        Span<char> buf = cap * 2 <= StackLimit * 2 ? stackalloc char[cap * 2] : (rented = ArrayPool<char>.Shared.Rent(cap * 2));
        Span<char> diffA = buf[..cap];
        Span<char> diffB = buf[cap..];
        int sectLen = 0, sectCount = 0, lenA = 0, lenB = 0;

        var ea = new TokenEnumerator(sortedA);
        var eb = new TokenEnumerator(sortedB);
        bool hasA = ea.MoveNext(), hasB = eb.MoveNext();
        while (hasA || hasB)
        {
            int cmp = !hasA ? 1 : !hasB ? -1 : ea.Current.SequenceCompareTo(eb.Current);
            if (cmp == 0)
            {
                sectLen += ea.Current.Length + (sectCount > 0 ? 1 : 0);
                sectCount++;
                hasA = ea.MoveNext();
                hasB = eb.MoveNext();
            }
            else if (cmp < 0)
            {
                AppendToken(diffA, ref lenA, ea.Current);
                hasA = ea.MoveNext();
            }
            else
            {
                AppendToken(diffB, ref lenB, eb.Current);
                hasB = eb.MoveNext();
            }
        }

        double result;
        if (sectCount > 0 && (lenA == 0 || lenB == 0))
        {
            result = 1.0;
        }
        else
        {
            // ratio(sect + diffA, sect + diffB): the shared prefix contributes equally to both.
            int sep = sectCount > 0 ? 1 : 0;
            int sectAbLen = sectLen + sep + lenA;
            int sectBaLen = sectLen + sep + lenB;
            int dist = IndelDistance(diffA[..lenA], diffB[..lenB]);
            result = 1.0 - ((double)dist / (sectAbLen + sectBaLen));

            if (sectCount > 0)
            {
                // ratio(sect, sect + diff) has a closed form because sect is a prefix.
                double r1 = 2.0 * sectLen / (sectLen + sectAbLen);
                double r2 = 2.0 * sectLen / (sectLen + sectBaLen);
                result = Math.Max(result, Math.Max(r1, r2));
            }
        }

        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }

        return result;
    }

    /// <summary>
    /// Counts the distinct tokens two sorted token strings share.
    /// </summary>
    /// <param name="sortedA">Sorted distinct tokens of a.</param>
    /// <param name="sortedB">Sorted distinct tokens of b.</param>
    /// <returns>Number of shared tokens.</returns>
    public static int SharedTokens(ReadOnlySpan<char> sortedA, ReadOnlySpan<char> sortedB)
    {
        var ea = new TokenEnumerator(sortedA);
        var eb = new TokenEnumerator(sortedB);
        bool hasA = ea.MoveNext(), hasB = eb.MoveNext();
        int shared = 0;
        while (hasA && hasB)
        {
            int cmp = ea.Current.SequenceCompareTo(eb.Current);
            if (cmp == 0)
            {
                shared++;
                hasA = ea.MoveNext();
                hasB = eb.MoveNext();
            }
            else if (cmp < 0)
            {
                hasA = ea.MoveNext();
            }
            else
            {
                hasB = eb.MoveNext();
            }
        }

        return shared;
    }

    private static void AppendToken(Span<char> dest, ref int len, ReadOnlySpan<char> token)
    {
        if (len > 0)
        {
            dest[len++] = ' ';
        }

        token.CopyTo(dest[len..]);
        len += token.Length;
    }

    /// <summary>
    /// Length of the longest common subsequence. Uses the bit-parallel algorithm (Hyyrö 2004)
    /// when the shorter string fits in 64 chars, otherwise a single-row dynamic program.
    /// </summary>
    /// <param name="a">First string.</param>
    /// <param name="b">Second string.</param>
    /// <returns>LCS length.</returns>
    internal static int Lcs(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length > b.Length)
        {
            var t = a;
            a = b;
            b = t;
        }

        if (a.Length == 0)
        {
            return 0;
        }

        return a.Length <= 64 ? LcsBitParallel(a, b) : LcsDp(a, b);
    }

    private static int LcsBitParallel(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        // Match masks: ASCII via a direct table, anything else via a small linear list.
        Span<ulong> ascii = stackalloc ulong[128];
        Span<char> otherChars = stackalloc char[64];
        Span<ulong> otherMasks = stackalloc ulong[64];
        int otherCount = 0;

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            ulong bit = 1UL << i;
            if (c < 128)
            {
                ascii[c] |= bit;
            }
            else
            {
                int j = otherChars[..otherCount].IndexOf(c);
                if (j < 0)
                {
                    otherChars[otherCount] = c;
                    otherMasks[otherCount++] = bit;
                }
                else
                {
                    otherMasks[j] |= bit;
                }
            }
        }

        ulong v = ~0UL;
        foreach (char c in text)
        {
            ulong m;
            if (c < 128)
            {
                m = ascii[c];
            }
            else
            {
                int j = otherChars[..otherCount].IndexOf(c);
                m = j < 0 ? 0UL : otherMasks[j];
            }

            ulong u = v & m;
            v = (v + u) | (v - u);
        }

        ulong lenMask = pattern.Length == 64 ? ~0UL : (1UL << pattern.Length) - 1;
        return BitOperations.PopCount(~v & lenMask);
    }

    private static int LcsDp(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int[]? rented = null;
        Span<int> row = b.Length + 1 <= StackLimit ? stackalloc int[b.Length + 1] : (rented = ArrayPool<int>.Shared.Rent(b.Length + 1));
        row[..(b.Length + 1)].Clear();
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = 0;
            char ca = a[i - 1];
            for (int j = 1; j <= b.Length; j++)
            {
                int up = row[j];
                row[j] = ca == b[j - 1] ? diag + 1 : Math.Max(up, row[j - 1]);
                diag = up;
            }
        }

        int result = row[b.Length];
        if (rented is not null)
        {
            ArrayPool<int>.Shared.Return(rented);
        }

        return result;
    }

    /// <summary>Iterates space separated tokens without allocating.</summary>
    internal ref struct TokenEnumerator
    {
        private ReadOnlySpan<char> _rest;

        public TokenEnumerator(ReadOnlySpan<char> text)
        {
            _rest = text;
            Current = default;
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public bool MoveNext()
        {
            while (_rest.Length > 0 && _rest[0] == ' ')
            {
                _rest = _rest[1..];
            }

            if (_rest.Length == 0)
            {
                return false;
            }

            int i = _rest.IndexOf(' ');
            if (i < 0)
            {
                Current = _rest;
                _rest = default;
            }
            else
            {
                Current = _rest[..i];
                _rest = _rest[(i + 1)..];
            }

            return true;
        }
    }
}
