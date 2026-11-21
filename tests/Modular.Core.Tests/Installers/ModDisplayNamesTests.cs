using FluentAssertions;
using Modular.Core.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

public class ModDisplayNamesTests
{
    [Theory]
    [InlineData("/m/JB - Nude Patch FPP V3-3620-V3-1669128334.rar", "3620", "JB - Nude Patch FPP V3")]
    [InlineData("/m/Mixed Input Fix 31759 1.1.0 2026-10-26T18-52Z 8ViLVPPfE.zip", "31759", "Mixed Input Fix")]
    [InlineData("/m/25130_130917_SGT_Bloody Star - Astarion Edit 25130 1 2026-09-19T19-24Z tvZRm6qtf.zip", "25130", "SGT Bloody Star - Astarion Edit")]
    [InlineData("/m/Ancestral Guardian-6411-1-1-1707131389.zip", null, "Ancestral Guardian")]
    [InlineData("/m/whatever.zip", "3620", null)]
    public void FromArchiveFileName_TakesTheNamePart(string archive, string? modId, string? expected)
    {
        ModDisplayNames.FromArchiveFileName(archive, modId).Should().Be(expected);
    }

    [Theory]
    [InlineData("3620", true)]
    [InlineData("24893-1", false)]
    [InlineData("my-mod", false)]
    [InlineData("", false)]
    public void IsNumericId(string id, bool expected) => ModDisplayNames.IsNumericId(id).Should().Be(expected);
}
