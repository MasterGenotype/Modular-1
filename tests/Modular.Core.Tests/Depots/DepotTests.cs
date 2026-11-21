using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Modular.Core.Depots;
using Xunit;

namespace Modular.Core.Tests.Depots;

internal static class Manifests
{
    public static string Sha1(string content) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>A manifest whose files have the given contents; each file is one chunk named after its content.</summary>
    public static ManifestView Make(ulong id, params (string Path, string Content)[] files) => Make(id, DateTime.UnixEpoch.AddDays(id), files);

    public static ManifestView Make(ulong id, DateTime created, params (string Path, string Content)[] files) =>
        new(100, id, created, files.Select(f => new ManifestFile(
            f.Path, (ulong)Encoding.UTF8.GetByteCount(f.Content), Sha1(f.Content), false,
            new[] { new ManifestChunk(Sha1("chunk:" + f.Content), (uint)f.Content.Length / 2 + 1, (uint)f.Content.Length) })));
}

public sealed class DepotDiffTests
{
    [Fact]
    public void Compare_ReportsAddedChangedRemovedAndOnlyNewChunks()
    {
        var from = Manifests.Make(1, ("bin/game.exe", "v1-exe"), ("data/a.archive", "aaaa"), ("data/old.archive", "old"));
        var to = Manifests.Make(2, ("bin/game.exe", "v2-exe"), ("data/a.archive", "aaaa"), ("data/new.archive", "new!"));

        var diff = DepotDiff.Compare(from, to);

        diff.Added.Should().Equal("data/new.archive");
        diff.Changed.Should().Equal("bin/game.exe");
        diff.Removed.Should().Equal("data/old.archive");
        diff.NewChunkCount.Should().Be(2, "the unchanged archive's chunk is already present");
        diff.IsIdentical.Should().BeFalse();
    }

    [Fact]
    public void Compare_SameContentUnderDifferentIds_IsIdentical()
    {
        var a = Manifests.Make(1, ("x", "same"));
        var b = Manifests.Make(2, ("x", "same"));

        DepotDiff.Compare(a, b).IsIdentical.Should().BeTrue();
    }

    [Fact]
    public void Compare_RejectsDifferentDepots()
    {
        var a = Manifests.Make(1, ("x", "1"));
        var b = new ManifestView(200, 2, DateTime.UnixEpoch, a.Files);

        FluentActions.Invoking(() => DepotDiff.Compare(a, b)).Should().Throw<ArgumentException>();
    }
}

public sealed class BuildIdentifierTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modular-depot-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    [Fact]
    public async Task SameSizeBuilds_AreToldApartByHashingOnlyTheDifferingFile()
    {
        // Like Cyberpunk's 2_31_old branch vs public 2.31: one same-size DLL differs.
        var branch = Manifests.Make(1, ("bin/services.dll", "AAAA"), ("data/big.archive", "payload"));
        var public231 = Manifests.Make(2, ("bin/services.dll", "BBBB"), ("data/big.archive", "payload"));
        Write(("bin/services.dll", "BBBB"), ("data/big.archive", "payload"));
        var hasher = new CountingHasher();

        var id = await new BuildIdentifier(hasher).IdentifyAsync(_dir, 100, new[] { branch, public231 });

        id.Match.Should().Be(DepotMatch.Exact);
        id.ManifestId.Should().Be(2UL);
        hasher.Paths.Should().ContainSingle().Which.Should().EndWith("services.dll");
    }

    [Fact]
    public async Task SingleCandidate_WithMatchingSizes_IsSizesOnlyAndHashesNothing()
    {
        var m = Manifests.Make(1, ("a", "123"), ("b", "4567"));
        Write(("a", "123"), ("b", "4567"));
        var hasher = new CountingHasher();

        var id = await new BuildIdentifier(hasher).IdentifyAsync(_dir, 100, new[] { m });

        id.Match.Should().Be(DepotMatch.SizesOnly);
        hasher.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task FullVerify_FindsSameSizeContentChange()
    {
        var m = Manifests.Make(1, ("a", "123"), ("b", "4567"));
        Write(("a", "123"), ("b", "XXXX"));

        var id = await new BuildIdentifier().IdentifyAsync(_dir, 100, new[] { m }, fullVerify: true);

        id.Match.Should().Be(DepotMatch.Modified);
        id.HashMismatch.Should().Equal("b");
    }

    [Fact]
    public async Task MissingAndResizedFiles_AreModified_AgainstTheClosestBuild()
    {
        var old = Manifests.Make(1, ("a", "1"), ("b", "22"), ("c", "333"));
        var current = Manifests.Make(2, ("a", "1111"), ("b", "2222"), ("c", "3333"));
        Write(("a", "1111"), ("b", "2222-modded"));

        var id = await new BuildIdentifier().IdentifyAsync(_dir, 100, new[] { old, current });

        id.ManifestId.Should().Be(2UL);
        id.Match.Should().Be(DepotMatch.Modified);
        id.Missing.Should().Equal("c");
        id.SizeMismatch.Should().Equal("b");
    }

    [Fact]
    public async Task IdenticalContentUnderTwoIds_PicksNewestAndListsTheOtherAsEquivalent()
    {
        // Like Phantom Liberty's 2.5 preload and release manifests.
        var preload = Manifests.Make(5, new DateTime(2026, 10, 19), ("a", "same"));
        var release = Manifests.Make(6, new DateTime(2026, 10, 22), ("a", "same"));
        Write(("a", "same"));

        var id = await new BuildIdentifier().IdentifyAsync(_dir, 100, new[] { preload, release });

        id.ManifestId.Should().Be(6UL);
        id.EquivalentManifests.Should().Equal(5UL);
        id.Match.Should().Be(DepotMatch.SizesOnly);
    }

    [Fact]
    public async Task NothingFits_IsUnknown_AndNoCandidatesIsReported()
    {
        var m = Manifests.Make(1, ("a", "1"));
        Write(("other", "x"));

        (await new BuildIdentifier().IdentifyAsync(_dir, 100, new[] { m })).Match.Should().Be(DepotMatch.Unknown);
        (await new BuildIdentifier().IdentifyAsync(_dir, 100, Array.Empty<ManifestView>())).Match.Should().Be(DepotMatch.NoCandidates);
    }

    private sealed class CountingHasher : IFileHasher
    {
        private readonly Sha1FileHasher _inner = new();
        public List<string> Paths { get; } = new();

        public Task<string> Sha1Async(string path, CancellationToken ct)
        {
            Paths.Add(path);
            return _inner.Sha1Async(path, ct);
        }
    }
}
