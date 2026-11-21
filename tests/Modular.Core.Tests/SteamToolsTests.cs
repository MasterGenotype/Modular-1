using FluentAssertions;
using Modular.Core.GameDetection;
using Xunit;

namespace Modular.Core.Tests;

public class SteamToolsTests
{
    [Theory]
    [InlineData(1070560, "Steam Linux Runtime 1.0 (scout)", true)]
    [InlineData(3810310, "Steam Linux Runtime 4.0", true)]
    [InlineData(1493710, "Proton Experimental", true)]
    [InlineData(3658110, "Proton 11.0", true)]
    [InlineData(228980, "Steamworks Common Redistributables", true)]
    [InlineData(107100, "Bastion", false)]
    [InlineData(1401730, "Crashlands 2", false)]
    [InlineData(1, "Protonaut Racing", false)]
    public void IsTool(int appId, string name, bool expected) => SteamTools.IsTool(appId, name).Should().Be(expected);
}
