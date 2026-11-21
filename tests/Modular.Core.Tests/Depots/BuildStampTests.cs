using FluentAssertions;
using Modular.Core.Depots;
using Xunit;

namespace Modular.Core.Tests.Depots;

public class BuildStampTests
{
    private static BuildStamp Stamp(ulong main, string? branch = null, uint? build = null) =>
        new(branch, build, new Dictionary<uint, ulong> { [107101] = main, [107103] = 3571113824443740111 }, DateTimeOffset.UtcNow);

    [Fact]
    public void SameBuild_ComparesSharedDepots()
    {
        Stamp(362369545429999215).SameBuildAs(Stamp(362369545429999215)).Should().BeTrue();
        Stamp(362369545429999215).SameBuildAs(Stamp(4592976686021117920)).Should().BeFalse();
    }

    [Fact]
    public void Matches_AVersionsManifests() =>
        Stamp(362369545429999215).Matches(new Dictionary<uint, ulong> { [107101] = 362369545429999215 }).Should().BeTrue();

    [Fact]
    public void Json_RoundTrips_AndLabelReadsWell()
    {
        var back = BuildStamp.FromJson(Stamp(362369545429999215, "legacy", 322538).ToJson())!;
        back.Depots[107101].Should().Be(362369545429999215UL);
        back.Label.Should().Be("legacy · build 322538");
        BuildStamp.FromJson("not json").Should().BeNull();
    }
}
