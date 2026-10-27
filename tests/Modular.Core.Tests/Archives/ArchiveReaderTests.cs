using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Archives;
using Xunit;

namespace Modular.Core.Tests.Archives;

/// <summary>
/// Archive readers report folder entries as folders, however the archive spells them.
/// </summary>
public sealed class ArchiveReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modular-archives").FullName;

    [Fact]
    public void AFolderEntryWithABackslash_IsAFolder_NotAFileToExtract()
    {
        // As in "AMM 4K Texture for Song 'Songbird'", zipped on Windows.
        var path = Path.Combine(_dir, "songbird.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("archive\\pc\\");
            using var writer = new StreamWriter(zip.CreateEntry("archive\\pc\\mod\\AMM_4K_Songbird.archive").Open());
            writer.Write("x");
        }

        using var reader = new ArchiveReaderFactory().Open(path)!;

        reader.Entries.Where(e => !e.IsDirectory).Select(e => e.FullName)
            .Should().Equal("archive\\pc\\mod\\AMM_4K_Songbird.archive");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
