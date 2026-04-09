using FluentAssertions;
using Modular.Core.Utilities;
using Xunit;

namespace Modular.Core.Tests;

public class FuzzyMatcherTests
{
    // --- Score: basic tiers ---

    [Fact]
    public void Score_ExactMatch_Returns100()
    {
        FuzzyMatcher.Score("skyui", "skyui").Should().Be(100);
    }

    [Fact]
    public void Score_ExactMatchCaseInsensitive_Returns100()
    {
        FuzzyMatcher.Score("SkyUI", "skyui").Should().Be(100);
    }

    [Fact]
    public void Score_PrefixMatch_Returns90()
    {
        FuzzyMatcher.Score("sky", "skyrimspecialedition").Should().Be(90);
    }

    [Fact]
    public void Score_WordBoundaryPrefix_Returns80()
    {
        // "ultra" starts the second word "Ultra HD Textures"
        FuzzyMatcher.Score("ultra", "HD Ultra Textures").Should().Be(80);
    }

    [Fact]
    public void Score_SubstringMatch_Returns70()
    {
        // "arm" appears inside "Farming" but does not start any word
        FuzzyMatcher.Score("arm", "Farming Tools").Should().Be(70);
    }

    [Fact]
    public void Score_SubsequenceMatch_ReturnsNonZeroLessThan70()
    {
        var score = FuzzyMatcher.Score("ammo", "Ammunition Manager");
        score.Should().BeGreaterThan(0).And.BeLessThan(70);
    }

    [Fact]
    public void Score_NoMatch_ReturnsZero()
    {
        FuzzyMatcher.Score("zzz", "SkyUI").Should().Be(0);
    }

    [Fact]
    public void Score_EmptyQuery_ReturnsZero()
    {
        FuzzyMatcher.Score("", "SkyUI").Should().Be(0);
    }

    [Fact]
    public void Score_EmptyTarget_ReturnsZero()
    {
        FuzzyMatcher.Score("skyui", "").Should().Be(0);
    }

    // --- Score: ordering sanity ---

    [Fact]
    public void Score_PrefixRanksHigherThanSubstring()
    {
        var prefix = FuzzyMatcher.Score("armor", "Armor of the Gods");
        var substring = FuzzyMatcher.Score("armor", "Heavy Armor Collection");
        prefix.Should().BeGreaterThan(substring);
    }

    [Fact]
    public void Score_SubstringRanksHigherThanSubsequence()
    {
        var substring = FuzzyMatcher.Score("cat", "Scattered Caves");
        var subsequence = FuzzyMatcher.Score("cat", "Combat Acrobatics Tweak");
        substring.Should().BeGreaterThan(subsequence);
    }

    // --- Rank ---

    [Fact]
    public void Rank_ReturnsItemsSortedBestFirst()
    {
        var items = new[] { "Heavy Armor Collection", "Armory of Dragons", "Armor of the Gods" };
        var ranked = FuzzyMatcher.Rank("armor", items, s => s).ToList();

        // "Armor of the Gods" (prefix) should beat "Heavy Armor Collection" (substring)
        ranked[0].Should().Be("Armor of the Gods");
    }

    [Fact]
    public void Rank_ExcludesZeroScoreItems()
    {
        var items = new[] { "SkyUI", "Complete Crafting Overhaul", "No Match Here ZZZ" };
        var ranked = FuzzyMatcher.Rank("zzz", items, s => s).ToList();

        ranked.Should().ContainSingle().Which.Should().Be("No Match Here ZZZ");
    }

    [Fact]
    public void Rank_EmptyQuery_ReturnsAllItems()
    {
        var items = new[] { "Mod A", "Mod B" };
        var ranked = FuzzyMatcher.Rank("", items, s => s).ToList();
        ranked.Should().BeEquivalentTo(items);
    }
}
