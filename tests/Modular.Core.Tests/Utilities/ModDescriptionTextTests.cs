using FluentAssertions;
using Modular.Core.Utilities;
using Xunit;

namespace Modular.Core.Tests.Utilities;

/// <summary>
/// Mod page descriptions turned into terminal text.
/// </summary>
public sealed class ModDescriptionTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void ToPlainText_IsEmpty_ForNoDescription(string? description)
    {
        ModDescriptionText.ToPlainText(description).Should().BeEmpty();
    }

    [Fact]
    public void ToPlainText_CountsNexusModsDoubledLineBreaksOnce()
    {
        // NexusMods sends every line break as a newline followed by <br />.
        var text = ModDescriptionText.ToPlainText("First line.\n<br />Second line.\n<br />\n<br />New paragraph.");

        text.Should().Be("First line.\nSecond line.\n\nNew paragraph.");
    }

    [Fact]
    public void ToPlainText_TurnsListItemsIntoBullets()
    {
        var text = ModDescriptionText.ToPlainText(
            "You can:\n<br />[list]\n<br />[*]Add files[/*]\n<br />[*]Download songs[/*]\n<br />[/list]\n<br />Done.");

        text.Should().Be("You can:\n\n• Add files\n• Download songs\n\nDone.");
    }

    [Fact]
    public void ToPlainText_KeepsLinkTargets()
    {
        ModDescriptionText.ToPlainText("See [url=https://example.com/a]the guide[/url].")
            .Should().Be("See the guide (https://example.com/a).");
        ModDescriptionText.ToPlainText("[url]https://example.com/b[/url]")
            .Should().Be("https://example.com/b");
        ModDescriptionText.ToPlainText("<a href=\"https://example.com/c\">mod.io page</a>")
            .Should().Be("mod.io page (https://example.com/c)");
    }

    [Fact]
    public void ToPlainText_DropsImagesAndFormatting()
    {
        var text = ModDescriptionText.ToPlainText(
            "[img]https://example.com/banner.png[/img][center][color=#ff7700][size=4][b]NO MUSIC FILES.[/b][/size][/color][/center]");

        text.Should().Be("NO MUSIC FILES.");
    }

    [Fact]
    public void ToPlainText_KeepsBracketedTextThatIsNotBbCode()
    {
        ModDescriptionText.ToPlainText("[WIP] Requires [Script Extender]")
            .Should().Be("[WIP] Requires [Script Extender]");
    }

    [Fact]
    public void ToPlainText_KeepsSpacingInsideCodeBlocks()
    {
        var text = ModDescriptionText.ToPlainText(
            "[code]Baldur's Gate 1      Baldur's Gate 2\n<br />Icewind Dale         Neverwinter Nights[/code]");

        text.Should().Be("Baldur's Gate 1      Baldur's Gate 2\nIcewind Dale         Neverwinter Nights");
    }

    [Fact]
    public void ToPlainText_ReadsHtmlDescriptions()
    {
        var text = ModDescriptionText.ToPlainText(
            "<p>Needs <strong>Script Extender</strong> &amp; MCM.</p><ul><li>One</li><li>Two</li></ul>");

        text.Should().Be("Needs Script Extender & MCM.\n\n• One\n• Two");
    }
}
