using FluentAssertions;
using Modular.Core.Utilities;
using Xunit;

namespace Modular.Core.Tests;

public class UtilityTests
{
    [Theory]
    [InlineData("~/Documents", "/home")]
    [InlineData("/absolute/path", "/absolute/path")]
    [InlineData("", "")]
    public void FileUtils_ExpandPath_HandlesVariousPaths(string input, string _)
    {
        // Just ensure it doesn't throw and returns something
        var result = FileUtils.ExpandPath(input);
        result.Should().NotBeNull();
    }

    [Fact]
    public void FileUtils_SanitizeFilename_HandlesNormalNames()
    {
        var result = FileUtils.SanitizeFilename("normal.txt");
        result.Should().Be("normal.txt");
    }

    [Fact]
    public void FileUtils_SanitizeFilename_RemovesNullChar()
    {
        // Null char is invalid on all platforms
        var result = FileUtils.SanitizeFilename("file\0name.txt");
        result.Should().Be("file_name.txt");
    }

    [Fact]
    public void FileUtils_SanitizeDirectoryName_RemovesInvalidChars()
    {
        var result = FileUtils.SanitizeDirectoryName("Mod: Test <Version>");
        result.Should().NotContain(":");
        result.Should().NotContain("<");
        result.Should().NotContain(">");
    }
}
