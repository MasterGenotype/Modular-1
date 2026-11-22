using FluentAssertions;
using Modular.Core.Dependencies;
using Xunit;

namespace Modular.Core.Tests.Dependencies;

public class RequirementClassificationTests
{
    [Theory]
    [InlineData("(Optional) Adds custom icon", true)]
    [InlineData("Not a hard requirement but if not using Vortex use this", true)]
    [InlineData("KAVT Only (optional)", true)]
    [InlineData("Only required if you want the dialogue options to display", true)]
    [InlineData("If you use Unique Tav instead of KAVT", true)]
    [InlineData("Hard Requirement!", false)]
    [InlineData("REQUIRED version 1.2", false)]
    [InlineData("Fully required for ravener models", false)]
    [InlineData(null, false)]
    public void OptionalNotes(string? note, bool optional) =>
        MissingDependencyService.IsOptionalNote(note).Should().Be(optional);

    [Theory]
    [InlineData("BG3 Mod Manager", null, true)]
    [InlineData("Some mod", "https://github.com/LaughingLeader/BG3ModManager/releases", true)]
    [InlineData("Latest Microsoft Visual C++ Redistributable", null, true)]
    [InlineData("BG3 Patch 8", null, true)]
    [InlineData("Vortex", null, true)]
    [InlineData("ImprovedUI", null, false)]
    [InlineData("Kaz Virtual Tav - Custom Appearance System", null, false)]
    public void NotMods(string name, string? url, bool isTool) =>
        MissingDependencyService.IsTool(name, url).Should().Be(isTool);
}
