using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

public sealed class NestedArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nested-tests-" + Guid.NewGuid().ToString("N"));

    public NestedArchiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ADownloadOfArchivesAndAReadme_OffersEachArchive()
    {
        var outer = Wrapper("shirt.zip", ("shirt_vanilla.zip", "vanilla"), ("shirt_angel.zip", "angel"));

        using var nested = await NestedArchive.OpenIfNeededAsync(outer, Empty);

        nested.Should().NotBeNull();
        nested!.Choice.Id.Should().Be(NestedArchive.ChoiceId);
        nested.Choice.Options.Select(o => o.Label).Should().Equal("shirt_angel", "shirt_vanilla");

        // Nobody chose, so the first is used and the choice stays open.
        nested.Choice.IsResolved.Should().BeFalse();
        nested.Choice.SelectedOptionId.Should().Be("shirt_angel.zip");
        InnerMarker(nested.ArchivePath).Should().Be("angel");
    }

    [Fact]
    public async Task AnAnswer_PicksTheArchiveByName()
    {
        var outer = Wrapper("shirt.zip", ("shirt_vanilla.zip", "vanilla"), ("shirt_angel.zip", "angel"));
        var selections = new Dictionary<string, string> { [NestedArchive.ChoiceId] = "SHIRT_VANILLA" };

        using var nested = await NestedArchive.OpenIfNeededAsync(outer, selections);

        nested!.Choice.IsResolved.Should().BeTrue();
        InnerMarker(nested.ArchivePath).Should().Be("vanilla");
    }

    [Fact]
    public async Task ChangingTheAnswer_ExtractsTheOtherArchive()
    {
        var outer = Wrapper("shirt.zip", ("shirt_vanilla.zip", "vanilla"), ("shirt_angel.zip", "angel"));

        using var nested = await NestedArchive.OpenIfNeededAsync(outer, Empty);
        var first = nested!.ArchivePath;

        (await nested.SelectAsync("shirt_vanilla.zip")).Should().BeTrue();
        (await nested.SelectAsync("shirt_vanilla")).Should().BeFalse();

        File.Exists(first).Should().BeFalse();
        InnerMarker(nested.ArchivePath).Should().Be("vanilla");
    }

    [Fact]
    public async Task ASingleInnerArchive_NeedsNoAnswer()
    {
        var outer = Wrapper("mod.zip", ("mod_main.zip", "main"));

        using var nested = await NestedArchive.OpenIfNeededAsync(outer, Empty);

        nested!.Choice.IsResolved.Should().BeTrue();
    }

    [Fact]
    public async Task AnArchiveWithGameFiles_IsReadAsItIs()
    {
        var path = Path.Combine(_dir, "mod.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "archive/pc/mod/thing.archive", "data");
            Add(zip, "extras/optional.zip", "data");
        }

        (await NestedArchive.OpenIfNeededAsync(path, Empty)).Should().BeNull();
    }

    [Fact]
    public async Task Dispose_RemovesTheExtractedArchive()
    {
        var outer = Wrapper("shirt.zip", ("shirt_angel.zip", "angel"));

        var nested = await NestedArchive.OpenIfNeededAsync(outer, Empty);
        var extracted = nested!.ArchivePath;
        nested.Dispose();

        File.Exists(extracted).Should().BeFalse();
    }

    private static readonly Dictionary<string, string> Empty = new();

    // A download holding one zip per version, each marked with its name, and a readme.
    private string Wrapper(string name, params (string Name, string Marker)[] inner)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (innerName, marker) in inner)
        {
            using var buffer = new MemoryStream();
            using (var innerZip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                Add(innerZip, "archive/pc/mod/marker.txt", marker);

            using var entry = zip.CreateEntry(innerName).Open();
            buffer.Position = 0;
            buffer.CopyTo(entry);
        }

        Add(zip, "README.txt", "read me");
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static string InnerMarker(string innerPath)
    {
        using var zip = ZipFile.OpenRead(innerPath);
        using var reader = new StreamReader(zip.GetEntry("archive/pc/mod/marker.txt")!.Open());
        return reader.ReadToEnd();
    }
}
