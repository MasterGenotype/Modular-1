using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Modular.Core.Depots;
using Xunit;

namespace Modular.Core.Tests.Depots;

/// <summary>Builds manifests whose chunk ids are real SHA-1s of 4-byte chunks, so local chunk reuse works.</summary>
internal static class ChunkedManifests
{
    public const int ChunkSize = 4;

    public static string Hex(byte[] b) => Convert.ToHexString(SHA1.HashData(b));

    public static ManifestFile File(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var chunks = new List<ManifestChunk>();
        for (var off = 0; off < bytes.Length; off += ChunkSize)
        {
            var part = bytes[off..Math.Min(bytes.Length, off + ChunkSize)];
            chunks.Add(new ManifestChunk(Hex(part), (uint)part.Length, (uint)part.Length, (ulong)off, 0));
        }
        return new ManifestFile(path, (ulong)bytes.Length, Hex(bytes), false, chunks);
    }

    public static ManifestView Make(ulong id, DateTime created, params ManifestFile[] files) => new(100, id, created, files);

    /// <summary>Serves chunks from a content table and counts downloads.</summary>
    public sealed class FakeCdn(params string[] contents) : IChunkSource
    {
        private readonly Dictionary<string, byte[]> _chunks = contents
            .SelectMany(c => Encoding.UTF8.GetBytes(c).Chunk(ChunkSize))
            .DistinctBy(Hex)
            .ToDictionary(Hex, b => b);

        public int Downloads;

        public Task<byte[]> GetChunkAsync(uint depotId, ManifestChunk chunk, CancellationToken ct)
        {
            Interlocked.Increment(ref Downloads);
            return Task.FromResult(_chunks[chunk.ChunkId]);
        }
    }
}

public sealed class DepotStagerAndSwapperTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modular-swap-").FullName;
    private string Install => Path.Combine(_root, "game");
    private string Staging => Path.Combine(_root, "staging");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteInstall(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(Install, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
        }
    }

    private static readonly DateTime Old = new(2026, 9, 9), New = new(2026, 10, 28);

    [Fact]
    public async Task Stage_ReusesLocalChunks_DownloadsTheRest_AndNeverTouchesTheGame()
    {
        // "AAAABBBB" → "AAAACCCC": the AAAA chunk is on disk already, CCCC must be downloaded.
        WriteInstall(("bin/game.exe", "AAAABBBB"), ("keep.txt", "same"));
        var installed = ChunkedManifests.Make(1, New, ChunkedManifests.File("bin/game.exe", "AAAABBBB"), ChunkedManifests.File("keep.txt", "same"));
        var target = ChunkedManifests.Make(2, Old, ChunkedManifests.File("bin/game.exe", "AAAACCCC"), ChunkedManifests.File("keep.txt", "same"));
        var cdn = new ChunkedManifests.FakeCdn("AAAACCCC");

        var plan = DepotStager.Plan(Install, Staging, installed, target);
        plan.LocalChunks.Should().Be(1);
        plan.DownloadChunks.Should().Be(1);

        await new DepotStager(cdn).StageAsync(Install, Staging, installed, target);

        cdn.Downloads.Should().Be(1);
        System.IO.File.ReadAllText(Path.Combine(Staging, "bin/game.exe")).Should().Be("AAAACCCC");
        System.IO.File.ReadAllText(Path.Combine(Install, "bin/game.exe")).Should().Be("AAAABBBB");
        System.IO.File.Exists(Path.Combine(Staging, "keep.txt")).Should().BeFalse("unchanged files are not staged");
    }

    [Fact]
    public async Task Stage_DoesNotTrustAModdedLocalChunk()
    {
        WriteInstall(("a", "XXXXBBBB")); // a mod changed the bytes the manifest says are AAAA
        var installed = ChunkedManifests.Make(1, New, ChunkedManifests.File("a", "AAAABBBB"));
        var target = ChunkedManifests.Make(2, Old, ChunkedManifests.File("a", "AAAACCCC"));
        var cdn = new ChunkedManifests.FakeCdn("AAAACCCC");

        await new DepotStager(cdn).StageAsync(Install, Staging, installed, target);

        cdn.Downloads.Should().Be(2, "the local AAAA chunk fails its SHA-1 check and is downloaded instead");
        System.IO.File.ReadAllText(Path.Combine(Staging, "a")).Should().Be("AAAACCCC");
    }

    [Fact]
    public async Task Stage_Resumes_WithoutDownloadingVerifiedFilesAgain()
    {
        WriteInstall(("a", "1111"), ("b", "2222"));
        var installed = ChunkedManifests.Make(1, New, ChunkedManifests.File("a", "1111"), ChunkedManifests.File("b", "2222"));
        var target = ChunkedManifests.Make(2, Old, ChunkedManifests.File("a", "9999"), ChunkedManifests.File("b", "8888"));
        var cdn = new ChunkedManifests.FakeCdn("9999", "8888");
        await new DepotStager(cdn).StageAsync(Install, Staging, installed, target);

        var again = new ChunkedManifests.FakeCdn("9999", "8888");
        await new DepotStager(again).StageAsync(Install, Staging, installed, target);

        again.Downloads.Should().Be(0);
        DepotStager.Plan(Install, Staging, installed, target).AlreadyStaged.Should().Be(2);
    }

    [Fact]
    public async Task Swap_ReplacesChanged_AddsNew_DeletesDropped_LeavesModsAlone_AndPrunesEmptyDirs()
    {
        WriteInstall(("bin/game.exe", "AAAABBBB"), ("old/only/dropped.dat", "gone"), ("mods/mymod.archive", "mine"));
        var installed = ChunkedManifests.Make(1, New,
            ChunkedManifests.File("bin/game.exe", "AAAABBBB"), ChunkedManifests.File("old/only/dropped.dat", "gone"));
        var target = new ManifestView(100, 2, Old, new[]
        {
            ChunkedManifests.File("bin/game.exe", "AAAACCCC"),
            ChunkedManifests.File("bin/new.dll", "NEW!"),
            new ManifestFile("empty/dir", 0, "", true, Array.Empty<ManifestChunk>()),
        });
        await new DepotStager(new ChunkedManifests.FakeCdn("AAAACCCC", "NEW!")).StageAsync(Install, Staging, installed, target);
        var plan = DepotStager.Plan(Install, Staging, installed, target);

        var result = DepotSwapper.Apply(Install, Staging, plan, installed, target);

        result.FilesMoved.Should().Be(2);
        result.FilesDeleted.Should().Be(1);
        System.IO.File.ReadAllText(Path.Combine(Install, "bin/game.exe")).Should().Be("AAAACCCC");
        System.IO.File.ReadAllText(Path.Combine(Install, "bin/new.dll")).Should().Be("NEW!");
        System.IO.File.Exists(Path.Combine(Install, "old/only/dropped.dat")).Should().BeFalse();
        Directory.Exists(Path.Combine(Install, "old")).Should().BeFalse("directories the swap emptied are pruned");
        Directory.Exists(Path.Combine(Install, "empty/dir")).Should().BeTrue();
        System.IO.File.ReadAllText(Path.Combine(Install, "mods/mymod.archive")).Should().Be("mine");
        DepotSwapper.IsComplete(Staging).Should().BeTrue();

        var id = await new BuildIdentifier().IdentifyAsync(Install, 100, new[] { installed, target });
        id.ManifestId.Should().Be(2UL);
        id.Match.Should().BeOneOf(DepotMatch.Exact, DepotMatch.SizesOnly); // staged files were hash-verified before the rename
    }

    [Fact]
    public async Task Swap_ResumesFromItsJournal()
    {
        WriteInstall(("a", "1111"), ("b", "2222"));
        var installed = ChunkedManifests.Make(1, New, ChunkedManifests.File("a", "1111"), ChunkedManifests.File("b", "2222"));
        var target = ChunkedManifests.Make(2, Old, ChunkedManifests.File("a", "9999"), ChunkedManifests.File("b", "8888"));
        await new DepotStager(new ChunkedManifests.FakeCdn("9999", "8888")).StageAsync(Install, Staging, installed, target);
        var plan = DepotStager.Plan(Install, Staging, installed, target);

        // Simulate a crash after "a" was moved.
        System.IO.File.Move(Path.Combine(Staging, "a"), Path.Combine(Install, "a"), overwrite: true);
        System.IO.File.WriteAllText(Path.Combine(Staging, DepotSwapper.JournalFile), "move a\n");

        var result = DepotSwapper.Apply(Install, Staging, plan, installed, target);

        result.FilesMoved.Should().Be(1);
        System.IO.File.ReadAllText(Path.Combine(Install, "a")).Should().Be("9999");
        System.IO.File.ReadAllText(Path.Combine(Install, "b")).Should().Be("8888");
    }
}

public sealed class BuildPinTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modular-pin-").FullName;
    private string Acf => Path.Combine(_dir, "steamapps", "appmanifest_1857090.acf");

    public BuildPinTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Acf)!);
        File.WriteAllText(Acf, """
            "AppState"
            {
            	"appid"		"1857090"
            	"name"		"Norland"
            	"StateFlags"		"4"
            	"installdir"		"Norland"
            	"buildid"		"26268245"
            	"TargetBuildID"		"0"
            	"AutoUpdateBehavior"		"0"
            	"InstalledDepots"
            	{
            		"1857091"
            		{
            			"manifest"		"6071069523908843722"
            			"size"		"1540000000"
            		}
            	}
            	"UserConfig"
            	{
            		"language"		"english"
            	}
            	"MountedConfig"
            	{
            		"language"		"english"
            	}
            }
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Dictionary<uint, (ulong, ulong)> Depots(ulong manifest) => new() { [1857091] = (manifest, 1_120_000_000) };

    [Fact]
    public void Apply_WritesTrueManifests_UpdateOnLaunch_AndBranch_ThenUnpinRestores()
    {
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(1228477902981854726), "old_branch", 2000, null, () => false);

        var acf = SteamAppManifest.Load(Acf);
        var raw = SteamAcfEditor.Load(Acf);
        acf.InstalledDepots.Single().ManifestId.Should().Be(1228477902981854726UL);
        acf.BuildId.Should().Be(2000u);
        raw.Get("AutoUpdateBehavior").Should().Be("1");
        raw.Get("UserConfig", "BetaKey").Should().Be("old_branch");
        raw.Get("MountedConfig", "BetaKey").Should().Be("old_branch");
        raw.Get("UserConfig", "language").Should().Be("english");
        BuildPinner.CheckDrift(acf, raw, pin).Should().BeEmpty();

        BuildPinner.Remove(SteamAcfEditor.Load(Acf), pin, () => false);

        raw = SteamAcfEditor.Load(Acf);
        raw.Get("AutoUpdateBehavior").Should().Be("0");
        raw.Get("TargetBuildID").Should().Be("0", "it was 0 before the pin");
        raw.Get("UserConfig", "BetaKey").Should().BeNull();
        raw.Get("MountedConfig", "BetaKey").Should().BeNull();
        SteamAppManifest.Load(Acf).InstalledDepots.Single().ManifestId.Should().Be(1228477902981854726UL, "manifest ids stay true after unpin");
    }

    [Fact]
    public void Apply_RefusesWhileSteamRuns()
    {
        FluentActions.Invoking(() => BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(1), null, null, null, () => true))
            .Should().Throw<InvalidOperationException>();
        SteamAcfEditor.Load(Acf).Get("AutoUpdateBehavior").Should().Be("0");
    }

    [Fact]
    public void CheckDrift_ReportsSteamUndoingThePin()
    {
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(1228477902981854726), null, null, null, () => false);

        // Steam wants to update: it flags the app, targets a new build and records the newer manifest.
        var raw = SteamAcfEditor.Load(Acf);
        raw.Set("6", "StateFlags");
        raw.Set("26300000", "TargetBuildID");
        raw.Set("0", "AutoUpdateBehavior");
        raw.Set("1795259262", "ScheduledAutoUpdate"); // what Steam wrote in the Norland test
        raw.Set("6071069523908843722", "InstalledDepots", "1857091", "manifest");
        raw.Save();

        var drift = BuildPinner.CheckDrift(SteamAppManifest.Load(Acf), SteamAcfEditor.Load(Acf), pin);

        drift.Should().HaveCount(5);
        drift.Should().Contain(d => d.Contains("2026-11-21"));
    }

    [Fact]
    public void Unpin_AfterSteamReplacedTheBuild_KeepsSteamsNewerValues()
    {
        // The Bastion case: pinned on an old build without a branch, then the user updated in Steam.
        var raw = SteamAcfEditor.Load(Acf);
        raw.Set("3406487", "TargetBuildID");
        raw.Save();
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(1228477902981854726), null, null, null, () => false);

        raw = SteamAcfEditor.Load(Acf);
        raw.Set("6071069523908843722", "InstalledDepots", "1857091", "manifest");
        raw.Set("3500000", "TargetBuildID"); // Steam moved on past the value the pin saved
        raw.Save();

        BuildPinner.IsSuperseded(SteamAppManifest.Load(Acf), pin).Should().BeTrue();
        var kept = BuildPinner.Remove(SteamAcfEditor.Load(Acf), pin, () => false);

        kept.Should().Equal("TargetBuildID");
        raw = SteamAcfEditor.Load(Acf);
        raw.Get("TargetBuildID").Should().Be("3500000", "Steam's value is newer than the one the pin saved");
        raw.Get("AutoUpdateBehavior").Should().Be("0", "still the pin's 1, so the original comes back");
    }

    [Fact]
    public void IsSuperseded_FalseWhilePinHoldsOrUpdatePending()
    {
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(1228477902981854726), null, null, null, () => false);
        BuildPinner.IsSuperseded(SteamAppManifest.Load(Acf), pin).Should().BeFalse();

        var raw = SteamAcfEditor.Load(Acf);
        raw.Set("6", "StateFlags");
        raw.Set("6071069523908843722", "InstalledDepots", "1857091", "manifest");
        raw.Save();
        BuildPinner.IsSuperseded(SteamAppManifest.Load(Acf), pin).Should().BeFalse("Steam hasn't finished replacing it");
    }

    [Fact]
    public void ReportAsCurrent_ClaimsLatestBuild_KeepsTruth_AndUnpinRestoresTruth()
    {
        const ulong old = 1228477902981854726, latest = 6071069523908843722;
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(old), null, null, null, () => false,
            reportAs: new Dictionary<uint, (ulong, ulong)> { [1857091] = (latest, 1_540_000_000) }, reportBuildId: 26268245);

        pin.ReportsCurrent.Should().BeTrue();
        pin.Depots[1857091].Should().Be(old, "the pin records what is really installed");
        var acf = SteamAppManifest.Load(Acf);
        acf.InstalledDepots.Single().ManifestId.Should().Be(latest, "Steam is told the game is current");
        acf.BuildId.Should().Be(26268245u);
        BuildPinner.CheckDrift(acf, SteamAcfEditor.Load(Acf), pin).Should().BeEmpty();
        BuildPinner.IsSuperseded(acf, pin).Should().BeFalse();

        // A new update ships: Steam records nothing new yet but flags the app.
        var raw = SteamAcfEditor.Load(Acf);
        raw.Set("6", "StateFlags");
        raw.Save();
        BuildPinner.CheckDrift(SteamAppManifest.Load(Acf), SteamAcfEditor.Load(Acf), pin).Should().ContainSingle();

        BuildPinner.Remove(SteamAcfEditor.Load(Acf), pin, () => false);
        acf = SteamAppManifest.Load(Acf);
        acf.InstalledDepots.Single().ManifestId.Should().Be(old, "unpin puts the true manifest back");
        SteamAcfEditor.Load(Acf).Get("InstalledDepots", "1857091", "size").Should().Be("1120000000");
        SteamAcfEditor.Load(Acf).Get("AutoUpdateBehavior").Should().Be("0");
    }

    [Fact]
    public void RePin_KeepsTheOriginalValuesForUnpin()
    {
        var first = BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(1), null, null, null, () => false);
        var second = BuildPinner.Apply(SteamAcfEditor.Load(Acf), 1857090, Depots(2), null, null, first, () => false);

        second.Replaced["AutoUpdateBehavior"].Should().Be("0", "the pre-Modular value, not the pin's own 1");
    }
}

public sealed class BuildLockTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modular-lock-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GetState_ReadsTheAttributeWithoutPrivileges()
    {
        File.WriteAllText(Path.Combine(_dir, "a.bin"), "x");

        var state = BuildLock.GetState(_dir, new[] { "a.bin", "missing.bin" });

        state.Locked.Should().Be(0);
        state.Unlocked.Should().Be(1);
        state.Missing.Should().Be(1);
        state.AnyLocked.Should().BeFalse();
    }

    [Fact]
    public void IsImmutable_DoesNotFollowSymlinks()
    {
        File.WriteAllText(Path.Combine(_dir, "real"), "x");
        File.CreateSymbolicLink(Path.Combine(_dir, "link"), Path.Combine(_dir, "real"));

        BuildLock.IsImmutable(Path.Combine(_dir, "link")).Should().BeNull();
        BuildLock.IsImmutable(Path.Combine(_dir, "real")).Should().BeFalse();
    }
}
