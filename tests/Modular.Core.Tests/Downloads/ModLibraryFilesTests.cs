using FluentAssertions;
using Modular.Core.Downloads;
using Xunit;

namespace Modular.Core.Tests.Downloads;

/// <summary>
/// The downloads in a mod folder, as the Library sends them to the installer: the
/// folder's own files, without the records kept beside them or unfinished downloads.
/// </summary>
public sealed class ModLibraryFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modular-libfiles-" + Guid.NewGuid().ToString("N"));

    public ModLibraryFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Downloads_AreTheArchives_WithoutSidecarsOrReadmes()
    {
        Write("MCM 9162 1.42.0.zip", "MCM 9162 1.42.0.zip.modular.json", "Old.7z", "Old.7z.meta",
            "Half.zip.part", "Half.zip.part.json", "readme.txt", "preview.png", "skin.bnp", "Mod.pak");

        ModLibraryFiles.Downloads(_dir).Select(Path.GetFileName)
            .Should().Equal("MCM 9162 1.42.0.zip", "Mod.pak", "Old.7z", "skin.bnp");
    }

    [Fact]
    public void Downloads_KeepAFileSavedWithoutItsExtension()
    {
        Write("Mod Configuration Menu 1.40.1", "Mod Configuration Menu 1.40.1.modular.json");

        ModLibraryFiles.Downloads(_dir).Select(Path.GetFileName).Should().Equal("Mod Configuration Menu 1.40.1");
    }

    [Fact]
    public void Downloads_SkipSubfoldersAndHiddenFiles()
    {
        Write("Mod.zip", ".directory");
        Directory.CreateDirectory(Path.Combine(_dir, "extracted"));
        File.WriteAllText(Path.Combine(_dir, "extracted", "Inner.pak"), "x");

        ModLibraryFiles.Downloads(_dir).Select(Path.GetFileName).Should().Equal("Mod.zip");
    }

    [Fact]
    public void Downloads_OfAMissingFolder_AreNone()
    {
        ModLibraryFiles.Downloads(Path.Combine(_dir, "gone")).Should().BeEmpty();
    }

    private void Write(params string[] names)
    {
        foreach (var name in names)
            File.WriteAllText(Path.Combine(_dir, name), "x");
    }
}
