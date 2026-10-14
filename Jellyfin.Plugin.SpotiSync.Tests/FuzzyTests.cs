using Jellyfin.Plugin.SpotiSync.Matching;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

// Expected values were produced with RapidFuzz 3.x (fuzz.ratio / token_sort_ratio / token_set_ratio).
public class FuzzyTests
{
    [Theory]
    [InlineData("kitten", "sitting", 0.615385)]
    [InlineData("fuzzy wuzzy was a bear", "wuzzy fuzzy was a bear", 0.909091)]
    [InlineData("mariners vs angels", "los angeles angels of anaheim at seattle mariners", 0.358209)]
    [InlineData("hey jude", "hey judy", 0.875)]
    [InlineData("a b c", "d e f", 0.4)]
    [InlineData("", "", 1.0)]
    [InlineData("abc", "", 0.0)]
    public void Ratio_MatchesRapidFuzz(string a, string b, double expected)
        => Assert.Equal(expected, Fuzzy.Ratio(a, b), 5);

    [Theory]
    [InlineData("fuzzy wuzzy was a bear", "wuzzy fuzzy was a bear", 1.0)]
    [InlineData("mariners vs angels", "los angeles angels of anaheim at seattle mariners", 0.507463)]
    public void TokenSortRatio_MatchesRapidFuzz(string a, string b, double expected)
        => Assert.Equal(expected, Fuzzy.TokenSortRatio(TextNorm.SortedTokens(a), TextNorm.SortedTokens(b)), 5);

    [Theory]
    [InlineData("fuzzy wuzzy was a bear", "wuzzy fuzzy was a bear", 1.0)]
    [InlineData("mariners vs angels", "los angeles angels of anaheim at seattle mariners", 0.909091)]
    [InlineData("bohemian rhapsody", "bohemian rhapsody live", 1.0)]
    [InlineData("dont stop me now", "dont stop me now 2011 remaster", 1.0)]
    [InlineData("hey jude", "hey judy", 0.875)]
    [InlineData("a b c", "d e f", 0.4)]
    public void TokenSetRatio_MatchesRapidFuzz(string a, string b, double expected)
        => Assert.Equal(expected, Fuzzy.TokenSetRatio(TextNorm.SortedTokens(a), TextNorm.SortedTokens(b)), 5);

    [Fact]
    public void LongStrings_UseDpPathAndAgreeWithBitParallel()
    {
        var a = new string('a', 40) + "xyz" + new string('b', 40);
        var b = new string('a', 39) + "xz" + new string('b', 45);
        // LCS = 39 a + "xz" + 40 b = 81
        Assert.Equal(81, Fuzzy.Lcs(a, b));
        Assert.Equal(Fuzzy.Lcs("kitten", "sitting"), Fuzzy.Lcs("sitting", "kitten"));
    }

    [Fact]
    public void NonAsciiCharacters_AreCompared()
    {
        Assert.Equal(1.0, Fuzzy.Ratio("東京事変", "東京事変"));
        Assert.Equal(0.75, Fuzzy.Ratio("東京事変", "東京事件"), 5);
    }

    [Fact]
    public void SharedTokens_CountsIntersection()
        => Assert.Equal(2, Fuzzy.SharedTokens("a b c", "b c d"));
}
