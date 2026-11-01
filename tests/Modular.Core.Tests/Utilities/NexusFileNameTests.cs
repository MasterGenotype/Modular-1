using FluentAssertions;
using Modular.Core.Utilities;
using Xunit;

namespace Modular.Core.Tests.Utilities;

/// <summary>
/// What NexusMods encodes in its download names, in the dashed form and the newer spaced one.
/// </summary>
public sealed class NexusFileNameTests
{
    [Theory]
    [InlineData("Native Camera Tweaks-945-2-4-5-1758665322.zip", "945", "Native Camera Tweaks")]
    [InlineData("KAVT - Kazstra's Virtual Tav 16325 2.0 2026-10-31T23-05Z rxn6oH0JD.zip", "16325", "KAVT - Kazstra's Virtual Tav")]
    [InlineData("Goon's Library 12834 4.27.0.0 2026-09-08T11-56Z CoqglV0q0", "12834", "Goon's Library")]
    [InlineData("Baldur's Gate 3 Script Extender 2172 v33-20261012 2026-10-12T21-52Z Sl3HiX1Qr.zip", "2172", "Baldur's Gate 3 Script Extender")]
    public void ReadsTheModIdAndName(string file, string id, string name)
    {
        NexusFileName.ParseModId(file).Should().Be(id);
        NexusFileName.ParseModName(file).Should().Be(name);
    }

    [Theory]
    [InlineData("SBBFStylizedBeautifulBodyFemale.pak")]
    [InlineData("Mod 2.0 2026-10-31T23-05Z.zip")]
    [InlineData("homemade_feats_and_itens_ac319-b8ve.zip")]
    public void NamesInNeitherForm_GiveNothing(string file)
    {
        NexusFileName.ParseModId(file).Should().BeNull();
    }

    [Fact]
    public void ReadsTheUploadTime_InBothForms()
    {
        NexusFileName.ParseUploadTime("Native Camera Tweaks-945-2-4-5-1758665322.zip")
            .Should().Be(DateTimeOffset.FromUnixTimeSeconds(1758665322));
        NexusFileName.ParseUploadTime("KAVT - Kazstra's Virtual Tav 16325 2.0 2026-10-31T23-05Z rxn6oH0JD.zip")
            .Should().Be(new DateTimeOffset(2026, 10, 31, 23, 5, 0, TimeSpan.Zero));
    }
}
