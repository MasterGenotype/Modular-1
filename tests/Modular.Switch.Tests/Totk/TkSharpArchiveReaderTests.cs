using System.IO.Compression;
using FluentAssertions;
using Moq;
using TkSharp;
using TkSharp.Core;
using TkSharp.Core.IO.Serialization;
using TkSharp.Core.Models;
using Xunit;

namespace Modular.Switch.Tests.Totk;

/// <summary>
/// TkSharp rc1 (797e76ec) was written against SharpCompress 0.40.0; Modular uses 0.50.4,
/// which renamed the archive factory methods, so lib/TkSharp carries a one-commit patch for
/// them. These tests read a mod of each kind through TkSharp's own readers: archives up to
/// the point where the reader asks for the game ROM (the archive was opened and its romfs
/// root located), and a .tkcl pack all the way through.
/// </summary>
[Collection(TkSharpCollection.Name)]
public sealed class TkSharpArchiveReaderTests
{
    private sealed class RomRequestedException : Exception;

    [Theory]
    [InlineData("TestMod.zip")]
    [InlineData("TestMod.7z")]
    [InlineData("TestMod.rar")]
    public async Task ReadMod_OpensTheArchiveAndLocatesItsRoot(string fileName)
    {
        var path = fileName == "TestMod.zip" ? CreateZip() : Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var system = new Mock<ITkSystemProvider>();
        var rom = new Mock<ITkRomProvider>();
        rom.Setup(r => r.GetRom()).Throws<RomRequestedException>();

        var readers = new TkModReaderProvider(system.Object, rom.Object);
        var reader = readers.GetReader(path);
        reader.Should().NotBeNull();

        await using var stream = File.OpenRead(path);
        var read = async () => await reader!.ReadMod(path, stream);

        await read.Should().ThrowAsync<RomRequestedException>();
    }

    [Fact]
    public async Task ReadMod_ReadsTheContentArchiveOfATkclPack()
    {
        var path = Path.Combine(NewTempDir(), "TestPack.tkcl");
        await using (var output = File.Create(path))
        {
            // TkPackWriter.Write, inlined so the tests need no TkSharp.Packaging reference.
            output.Write(BitConverter.GetBytes(TkBinaryWriter.TKPK_MAGIC));
            output.Write(BitConverter.GetBytes(TkBinaryWriter.TKPK_VERSION));
            TkBinaryWriter.WriteTkMod(output, new TkMod { Name = "TestPack", Changelog = new TkChangelog { BuilderVersion = 200, GameVersion = 121 } });
            output.Write(File.ReadAllBytes(CreateZip()));
        }

        var written = new Dictionary<string, MemoryStream>();
        var writer = new Mock<ITkModWriter>();
        writer.Setup(w => w.OpenWrite(It.IsAny<string>()))
            .Returns((string file) => written[file] = new MemoryStream());
        var system = new Mock<ITkSystemProvider>();
        system.Setup(s => s.GetSystemWriter(It.IsAny<TkModContext>())).Returns(writer.Object);

        var readers = new TkModReaderProvider(system.Object, Mock.Of<ITkRomProvider>());
        await using var stream = File.OpenRead(path);
        var mod = await readers.GetReader(path)!.ReadMod(path, stream);

        mod!.Name.Should().Be("TestPack");
        written.Keys.Should().Equal("romfs/Test/hello.txt");
        written["romfs/Test/hello.txt"].ToArray().Should().Equal("hello"u8.ToArray());
    }

    [Fact]
    public async Task ReadMod_ReadsATkclPackInsideAZip()
    {
        // Enough entries that SharpCompress 0.50's entry stream used to fail part-way.
        const int fileCount = 4000;
        var dir = NewTempDir();
        var content = Path.Combine(dir, "content.zip");
        using (var zip = ZipFile.Open(content, ZipArchiveMode.Create))
        {
            for (var i = 0; i < fileCount; i++)
            {
                using var entry = new StreamWriter(zip.CreateEntry($"romfs/Test/{i:D4}.txt").Open());
                entry.Write($"file {i} {Guid.NewGuid()}");
            }
        }

        var pack = Path.Combine(dir, "TestPack.tkcl");
        await using (var output = File.Create(pack))
        {
            output.Write(BitConverter.GetBytes(TkBinaryWriter.TKPK_MAGIC));
            output.Write(BitConverter.GetBytes(TkBinaryWriter.TKPK_VERSION));
            TkBinaryWriter.WriteTkMod(output, new TkMod { Name = "TestPack", Changelog = new TkChangelog { BuilderVersion = 200, GameVersion = 121 } });
            output.Write(File.ReadAllBytes(content));
        }

        var outer = Path.Combine(dir, "TestPack.zip");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(pack, "TestPack.tkcl");

        var written = new HashSet<string>();
        var writer = new Mock<ITkModWriter>();
        writer.Setup(w => w.OpenWrite(It.IsAny<string>()))
            .Returns((string file) =>
            {
                written.Add(file);
                return Stream.Null;
            });
        var system = new Mock<ITkSystemProvider>();
        system.Setup(s => s.GetSystemWriter(It.IsAny<TkModContext>())).Returns(writer.Object);

        var readers = new TkModReaderProvider(system.Object, Mock.Of<ITkRomProvider>());
        await using var stream = File.OpenRead(outer);
        var mod = await readers.GetReader(outer)!.ReadMod(outer, stream);

        mod!.Name.Should().Be("TestPack");
        written.Should().HaveCount(fileCount);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"modular-tk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateZip()
    {
        var path = Path.Combine(NewTempDir(), "TestMod.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("romfs/Test/hello.txt").Open());
        writer.Write("hello");
        return path;
    }
}
