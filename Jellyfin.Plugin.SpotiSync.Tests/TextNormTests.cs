using Jellyfin.Plugin.SpotiSync.Matching;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

public class TextNormTests
{
    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Don't Stop Me Now", "dont stop me now")]
    [InlineData("Don’t Stop", "dont stop")]
    [InlineData("Simon & Garfunkel", "simon and garfunkel")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("  Hello,   World!! ", "hello world")]
    [InlineData("Mötley Crüe", "motley crue")]
    [InlineData("Sigur Rós", "sigur ros")]
    [InlineData("Straße", "strasse")]
    [InlineData("Røyksopp", "royksopp")]
    [InlineData("", "")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, TextNorm.Normalize(input));

    [Fact]
    public void SortedTokens_SortsAndDedupes() => Assert.Equal("a b c", TextNorm.SortedTokens("c a b a"));

    [Fact]
    public void NormalizeArtist_DropsLeadingThe() => Assert.Equal("beatles", TextNorm.NormalizeArtist("The Beatles"));

    [Fact]
    public void SplitArtists_SplitsCommonSeparatorsAndKeepsWhole()
    {
        var list = new List<string>();
        TextNorm.SplitArtists(["Earth, Wind & Fire"], list);
        Assert.Contains("earth wind and fire", list);
        Assert.Contains("earth", list);
        Assert.Contains("fire", list);

        list.Clear();
        TextNorm.SplitArtists(["Calvin Harris feat. Rihanna"], list);
        Assert.Contains("calvin harris", list);
        Assert.Contains("rihanna", list);

        list.Clear();
        TextNorm.SplitArtists(["Jay-Z x Kanye West"], list);
        Assert.Contains("jay z", list);
        Assert.Contains("kanye west", list);
    }
}
