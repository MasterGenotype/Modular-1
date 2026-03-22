using FluentAssertions;
using Modular.Core.Versioning;
using Xunit;

namespace Modular.Core.Tests;

public class VersionRangeTests
{
    // --- Wildcard ranges ---

    [Theory]
    [InlineData("*", "0.0.1")]
    [InlineData("*", "1.0.0")]
    [InlineData("*", "99.99.99")]
    public void Wildcard_Star_MatchesEverything(string range, string version)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.x", "1.0.0", true)]
    [InlineData("1.x", "1.5.3", true)]
    [InlineData("1.x", "1.99.99", true)]
    [InlineData("1.x", "0.9.9", false)]
    [InlineData("1.x", "2.0.0", false)]
    [InlineData("0.x", "0.0.0", true)]
    [InlineData("0.x", "0.5.0", true)]
    [InlineData("0.x", "1.0.0", false)]
    public void WildcardMinor_MatchesSameMajor(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.X", "1.0.0", true)]
    [InlineData("1.*", "1.0.0", true)]
    [InlineData("1.X", "2.0.0", false)]
    [InlineData("1.*", "0.9.0", false)]
    public void WildcardMinor_AcceptsVariants(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.2.x", "1.2.0", true)]
    [InlineData("1.2.x", "1.2.5", true)]
    [InlineData("1.2.x", "1.2.99", true)]
    [InlineData("1.2.x", "1.1.9", false)]
    [InlineData("1.2.x", "1.3.0", false)]
    [InlineData("1.2.x", "2.2.0", false)]
    [InlineData("0.0.x", "0.0.0", true)]
    [InlineData("0.0.x", "0.0.5", true)]
    [InlineData("0.0.x", "0.1.0", false)]
    public void WildcardPatch_MatchesSameMajorMinor(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.2.X", "1.2.0", true)]
    [InlineData("1.2.*", "1.2.3", true)]
    [InlineData("1.2.X", "1.3.0", false)]
    public void WildcardPatch_AcceptsVariants(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    // --- ToString round-trip ---

    [Theory]
    [InlineData("1.x")]
    [InlineData("1.2.x")]
    [InlineData("*")]
    public void Wildcard_ToString_Roundtrips(string range)
    {
        var r = VersionRange.Parse(range);
        r.ToString().Should().Be(range);
    }

    // --- Existing constraint types (regression tests) ---

    [Theory]
    [InlineData(">=1.0.0", "1.0.0", true)]
    [InlineData(">=1.0.0", "2.0.0", true)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    public void GreaterThanOrEqual_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData(">1.0.0", "1.0.1", true)]
    [InlineData(">1.0.0", "1.0.0", false)]
    public void GreaterThan_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("<2.0.0", "1.9.9", true)]
    [InlineData("<2.0.0", "2.0.0", false)]
    public void LessThan_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("<=2.0.0", "2.0.0", true)]
    [InlineData("<=2.0.0", "2.0.1", false)]
    public void LessThanOrEqual_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("~1.2.3", "1.2.3", true)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("~1.2.3", "1.2.2", false)]
    public void Tilde_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("^1.2.3", "1.2.3", true)]
    [InlineData("^1.2.3", "1.9.9", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^0.2.3", "0.2.5", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.3", true)]
    [InlineData("^0.0.3", "0.0.4", false)]
    public void Caret_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("=1.0.0", "1.0.0", true)]
    [InlineData("=1.0.0", "1.0.1", false)]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.1", false)]
    public void Exact_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    // --- OR operator (||) ---

    [Theory]
    [InlineData(">=1.0.0 <2.0.0 || >=3.0.0", "1.5.0", true)]
    [InlineData(">=1.0.0 <2.0.0 || >=3.0.0", "3.0.0", true)]
    [InlineData(">=1.0.0 <2.0.0 || >=3.0.0", "3.5.0", true)]
    [InlineData(">=1.0.0 <2.0.0 || >=3.0.0", "2.5.0", false)]
    [InlineData(">=1.0.0 <2.0.0 || >=3.0.0", "0.9.0", false)]
    public void Or_WithAndGroups_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("~1.2.3 || ^2.0.0", "1.2.5", true)]
    [InlineData("~1.2.3 || ^2.0.0", "2.5.0", true)]
    [InlineData("~1.2.3 || ^2.0.0", "1.3.0", false)]
    [InlineData("~1.2.3 || ^2.0.0", "3.0.0", false)]
    public void Or_WithTildeAndCaret_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("=1.0.0 || =2.0.0 || =3.0.0", "1.0.0", true)]
    [InlineData("=1.0.0 || =2.0.0 || =3.0.0", "2.0.0", true)]
    [InlineData("=1.0.0 || =2.0.0 || =3.0.0", "3.0.0", true)]
    [InlineData("=1.0.0 || =2.0.0 || =3.0.0", "1.5.0", false)]
    public void Or_MultipleExactVersions_Works(string range, string version, bool expected)
    {
        var r = VersionRange.Parse(range);
        r.IsSatisfiedBy(SemanticVersion.Parse(version)).Should().Be(expected);
    }

    [Fact]
    public void Or_ToString_Roundtrips()
    {
        var r = VersionRange.Parse("~1.2.3 || >=2.0.0");
        r.ToString().Should().Be("~1.2.3 || >=2.0.0");
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForInvalidRanges()
    {
        VersionRange.TryParse("", out _).Should().BeFalse();
        VersionRange.TryParse("   ", out _).Should().BeFalse();
        VersionRange.TryParse(">=not-a-version", out _).Should().BeFalse();
    }
}
