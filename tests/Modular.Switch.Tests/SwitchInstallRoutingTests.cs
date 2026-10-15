using FluentAssertions;
using Modular.Switch.Models;
using Xunit;

namespace Modular.Switch.Tests;

public sealed class SwitchInstallRoutingTests
{
    [Theory]
    [InlineData("0100F2C0115B6000")]
    [InlineData("0100f2c0115b6000")]
    public void TearsOfTheKingdom_IsMerged(string titleId)
    {
        SwitchInstallRouting.For(new SwitchTitleId(titleId)).Should().Be(SwitchInstallMethod.TotkMerge);
    }

    [Theory]
    [InlineData("01007EF00011E000")] // Breath of the Wild
    [InlineData("0100F2C0115B6800")] // a TotK DLC/update ID, not the game
    [InlineData("0100000000010000")] // Super Mario Odyssey
    public void OtherTitles_KeepTheLayeredFsInstaller(string titleId)
    {
        SwitchInstallRouting.For(new SwitchTitleId(titleId)).Should().Be(SwitchInstallMethod.LayeredFsSlots);
    }
}
