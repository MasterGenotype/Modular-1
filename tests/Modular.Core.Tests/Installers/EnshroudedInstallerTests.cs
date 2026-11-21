using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Core.Installers.Enshrouded;
using Modular.Core.Modules;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// Enshrouded: where EML mods and loaders land, EML installed alongside the first mod, and
/// the game's data index restored once EML is gone.
/// </summary>
public sealed class EnshroudedInstallerTests : IAsyncLifetime
{
    private const string VanillaTag = "1265863|^/game38/branches/release_1.0|2026-10-14T14:03:32Z";

    private readonly List<string> _tempDirs = new();
    private ModularDatabase _database = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = new ModularDatabase(Path.Combine(NewDir("db"), "modular.db"));
        await _database.InitializeAsync();
    }

    // ── analyzer ────────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_WrappedModFolder_GoesToModsUnderItsId()
    {
        var layout = Analyze(
            ("Better Torches v1.2/mod.json", Manifest("better-torches")),
            ("Better Torches v1.2/src/mod.lua", "-- lua"),
            ("Better Torches v1.2/README.md", "docs"));

        layout.CanHandle.Should().BeTrue();
        layout.Mods.Should().ContainSingle().Which.Folder.Should().Be("better-torches");
        Destination(layout, "Better Torches v1.2/src/mod.lua").Should().Be("mods/better-torches/src/mod.lua");
        Destination(layout, "Better Torches v1.2/mod.json").Should().Be("mods/better-torches/mod.json");
    }

    [Fact]
    public void Analyze_ManifestAtArchiveRoot_UsesTheManifestId()
    {
        var layout = Analyze(("mod.json", Manifest("stack-size")), ("src/mod.lua", "-- lua"));

        Destination(layout, "src/mod.lua").Should().Be("mods/stack-size/src/mod.lua");
    }

    [Fact]
    public void Analyze_ModsWrapperWithTwoMods_InstallsBoth()
    {
        var layout = Analyze(
            ("mods/a-mod/mod.json", Manifest("a-mod")),
            ("mods/a-mod/src/mod.lua", "a"),
            ("mods/b-mod/mod.json", Manifest("b-mod")),
            ("mods/b-mod/src/mod.lua", "b"));

        layout.Mods.Select(m => m.Folder).Should().BeEquivalentTo("a-mod", "b-mod");
        Destination(layout, "mods/b-mod/src/mod.lua").Should().Be("mods/b-mod/src/mod.lua");
    }

    [Fact]
    public void Analyze_ManifestWithoutUsableId_FallsBackToItsFolderName()
    {
        var layout = Analyze(("MyMod/mod.json", """{ "name": "My Mod", "version": "1.0.0" }"""), ("MyMod/src/mod.lua", ""));

        layout.Mods.Single().Folder.Should().Be("MyMod");
    }

    [Fact]
    public void Analyze_NestedExampleManifest_BelongsToTheOuterMod()
    {
        var layout = Analyze(
            ("outer/mod.json", Manifest("outer")),
            ("outer/examples/inner/mod.json", Manifest("inner")));

        layout.Mods.Should().ContainSingle().Which.Folder.Should().Be("outer");
        Destination(layout, "outer/examples/inner/mod.json").Should().Be("mods/outer/examples/inner/mod.json");
    }

    [Fact]
    public void Analyze_LoaderArchive_GoesToTheGameRoot()
    {
        var layout = Analyze(("EML v0.2.0/dbghelp.dll", "dll"), ("EML v0.2.0/emm.exe", "exe"));

        layout.CanHandle.Should().BeTrue();
        layout.Mods.Should().BeEmpty();
        Destination(layout, "EML v0.2.0/dbghelp.dll").Should().Be("dbghelp.dll");
        Destination(layout, "EML v0.2.0/emm.exe").Should().Be("emm.exe");
    }

    [Fact]
    public void Analyze_StandalonePatcher_IsNotHandled()
    {
        var layout = Analyze(("enshrouded-patcher.exe", "exe"), ("config.json", "{}"));

        layout.CanHandle.Should().BeFalse();
        layout.Reason.Should().Contain("Patcher");
    }

    [Fact]
    public void Analyze_NoManifestAndNoLoader_IsNotHandled()
    {
        Analyze(("textures/thing.dds", "x")).CanHandle.Should().BeFalse();
    }

    [Fact]
    public void Analyze_StrayNonDocumentFile_LowersConfidence()
    {
        Analyze(("m/mod.json", Manifest("m")), ("readme.txt", "x")).Confidence.Should().Be(0.95);
        Analyze(("m/mod.json", Manifest("m")), ("stray.dll", "x")).Confidence.Should().BeLessThan(0.95);
    }

    [Fact]
    public void Analyze_NativeModWithPluginLoader_GoesToTheGameRoot()
    {
        var layout = Analyze(
            ("powrprof.dll", "dll"), ("plugins/CombatRebalanced.dll", "dll"),
            ("mods/combat-rebalanced/mod.json", Manifest("combat-rebalanced")), ("README.txt", "x"));

        Destination(layout, "powrprof.dll").Should().Be("powrprof.dll");
        Destination(layout, "plugins/CombatRebalanced.dll").Should().Be("plugins/CombatRebalanced.dll");
        Destination(layout, "mods/combat-rebalanced/mod.json").Should().Be("mods/combat-rebalanced/mod.json");
    }

    [Fact]
    public void Analyze_PowrprofWithShroudedEngineTweaksInstalled_LoadsThroughSet()
    {
        var game = NewDir("game");
        Directory.CreateDirectory(Path.Combine(game, EnshroudedArchiveAnalyzer.SetFolder));

        var entries = new[] { ("powrprof.dll", "dll"), ("plugins/CombatRebalanced.dll", "dll") }
            .Select(f => new ArchiveEntry { FullName = f.Item1, Name = Path.GetFileName(f.Item1), Length = 3 })
            .ToList();

        var layout = EnshroudedArchiveAnalyzer.Analyze(entries, _ => null, "CombatRebalanced-1.2.0.zip", game);

        layout.HostedInSet.Should().BeTrue();
        Destination(layout, "powrprof.dll")
            .Should().Be("shrouded_engine_tweaks/plugins/CombatRebalanced-1-2-0-powrprof.dll");
        Destination(layout, "plugins/CombatRebalanced.dll").Should().Be("plugins/CombatRebalanced.dll");
    }

    [Fact]
    public void Analyze_ShroudedEngineTweaksItself_KeepsItsPowrprofAtTheRoot()
    {
        var game = NewDir("game");
        Directory.CreateDirectory(Path.Combine(game, EnshroudedArchiveAnalyzer.SetFolder));

        var entries = new[] { "powrprof.dll", "shrouded_engine_tweaks/README.txt", "Tweaks.txt" }
            .Select(f => new ArchiveEntry { FullName = f, Name = Path.GetFileName(f), Length = 3 })
            .ToList();

        var layout = EnshroudedArchiveAnalyzer.Analyze(entries, _ => null, "SET.zip", game);

        layout.HostedInSet.Should().BeFalse();
        Destination(layout, "powrprof.dll").Should().Be("powrprof.dll");
    }

    // ── data index restore ──────────────────────────────────────────────────

    [Fact]
    public void ReadVersionTag_ReadsTheHeaderTag()
    {
        var game = NewDir("game");
        WriteKfc(Path.Combine(game, EnshroudedKfc.IndexFile), VanillaTag, "body");

        EnshroudedKfc.ReadVersionTag(Path.Combine(game, EnshroudedKfc.IndexFile)).Should().Be(VanillaTag);
    }

    [Fact]
    public void RestoreIfLoaderGone_MatchingBackup_RestoresTheOriginal()
    {
        var game = NewDir("game");
        WriteKfc(Path.Combine(game, EnshroudedKfc.BackupFile), VanillaTag, "vanilla");
        WriteKfc(Path.Combine(game, EnshroudedKfc.IndexFile), VanillaTag, "patched");

        EnshroudedKfc.RestoreIfLoaderGone(game).Should().Contain("restored");

        File.ReadAllBytes(Path.Combine(game, EnshroudedKfc.IndexFile))
            .Should().Equal(KfcBytes(VanillaTag, "vanilla"));
        File.Exists(Path.Combine(game, EnshroudedKfc.BackupFile)).Should().BeFalse();
    }

    [Fact]
    public void RestoreIfLoaderGone_LoaderStillPresent_LeavesEverything()
    {
        var game = NewDir("game");
        File.WriteAllText(Path.Combine(game, "dbghelp.dll"), "eml");
        WriteKfc(Path.Combine(game, EnshroudedKfc.BackupFile), VanillaTag, "vanilla");
        WriteKfc(Path.Combine(game, EnshroudedKfc.IndexFile), VanillaTag, "patched");

        EnshroudedKfc.RestoreIfLoaderGone(game).Should().BeNull();
        File.Exists(Path.Combine(game, EnshroudedKfc.BackupFile)).Should().BeTrue();
    }

    [Fact]
    public void RestoreIfLoaderGone_BackupFromAnOlderBuild_IsLeftAlone()
    {
        var game = NewDir("game");
        WriteKfc(Path.Combine(game, EnshroudedKfc.BackupFile), "old-build", "old");
        WriteKfc(Path.Combine(game, EnshroudedKfc.IndexFile), VanillaTag, "new vanilla");

        EnshroudedKfc.RestoreIfLoaderGone(game).Should().Contain("different game version");
        File.ReadAllBytes(Path.Combine(game, EnshroudedKfc.IndexFile))
            .Should().Equal(KfcBytes(VanillaTag, "new vanilla"));
    }

    // ── end to end ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallingAMod_InstallsEmlOnce_AndUninstallingEmlRestoresTheIndex()
    {
        var game = NewGame();
        var (service, downloads) = NewService();

        var first = await service.InstallAsync(
            ModZip("Better Torches", "better-torches"), game,
            new ModInstallationOptions { ModId = "Better Torches", GameId = "enshrouded", AutoSnapshot = false });

        first.Success.Should().BeTrue(first.Error);
        first.ModuleId.Should().Be("enshrouded");
        File.Exists(Path.Combine(game, "mods", "better-torches", "mod.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(game, "dbghelp.dll")).Should().Be(FakeLoader);
        first.Warnings.Should().Contain(w => w.Contains("Installed Test EML"));
        first.Deployment!.WineDllOverrides.Should().Contain("dbghelp");

        var second = await service.InstallAsync(
            ModZip("Stack Size", "stack-size"), game,
            new ModInstallationOptions { ModId = "Stack Size", GameId = "enshrouded", AutoSnapshot = false });

        second.Success.Should().BeTrue(second.Error);
        File.Exists(Path.Combine(game, "mods", "stack-size", "mod.json")).Should().BeTrue();
        downloads().Should().Be(1, "EML is already in place for the second mod");

        // What EML does on launch: back up the vanilla index, then patch it.
        File.Copy(Path.Combine(game, EnshroudedKfc.IndexFile), Path.Combine(game, EnshroudedKfc.BackupFile));
        WriteKfc(Path.Combine(game, EnshroudedKfc.IndexFile), VanillaTag, "patched by EML");

        var uninstall = await service.UninstallModAsync(game, "Test EML", autoSnapshot: false);

        uninstall.Success.Should().BeTrue(uninstall.Error);
        File.Exists(Path.Combine(game, "dbghelp.dll")).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(game, EnshroudedKfc.IndexFile))
            .Should().Equal(KfcBytes(VanillaTag, "vanilla"));
        File.Exists(Path.Combine(game, EnshroudedKfc.BackupFile)).Should().BeFalse();
    }

    [Fact]
    public async Task LoaderDownloadWithTheWrongHash_IsRefused_AndTheModStillInstalls()
    {
        var game = NewGame();
        var (service, _) = NewService(served: "tampered");

        var result = await service.InstallAsync(
            ModZip("Better Torches", "better-torches"), game,
            new ModInstallationOptions { ModId = "Better Torches", GameId = "enshrouded", AutoSnapshot = false });

        result.Success.Should().BeTrue(result.Error);
        File.Exists(Path.Combine(game, "mods", "better-torches", "mod.json")).Should().BeTrue();
        File.Exists(Path.Combine(game, "dbghelp.dll")).Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("does not match its pinned SHA-256"));
    }

    [Theory]
    [InlineData("""{ "id": "a", "name": "A", "version": "1.0.0", }""", "strict JSON")]
    [InlineData("""{ "name": "A", "version": "1.0.0" }""", "\"id\"")]
    [InlineData("""{ "id": "a", "name": "A", "version": "1.0" }""", "semantic version")]
    [InlineData("""{ "id": "a", "name": "A", "version": "1.0.0", "capabilities": ["flying"] }""", "unknown capability")]
    [InlineData("""{ "id": "a", "name": "A", "version": "1.0.0", "dependencies": ["b"] }""", "dependency")]
    public void FindEmlManifestProblems_FlagsWhatEmlRejects(string json, string expected)
    {
        EnshroudedArchiveAnalyzer.FindEmlManifestProblems(json).Should().Contain(p => p.Contains(expected));
    }

    [Fact]
    public void FindEmlManifestProblems_AcceptsAFullManifest()
    {
        const string json = """
            { "id": "a", "name": "A", "version": "1.2.3-beta.1", "capabilities": ["patch", "runtime-register-dll"],
              "dependencies": [{ "id": "b", "version": "^1.0.0", "optional": true }], "authors": ["x"] }
            """;

        EnshroudedArchiveAnalyzer.FindEmlManifestProblems(json).Should().BeEmpty();
    }

    [Fact]
    public async Task AManifestEmlWouldReject_IsKeptOutOfTheGame()
    {
        var game = NewGame();
        var (service, _) = NewService();

        var result = await service.InstallAsync(
            ModZip("Broken", "broken", """{ "id": "broken", "name": "Broken", "version": "1.0.0", }"""), game,
            new ModInstallationOptions { ModId = "Broken", GameId = "enshrouded", AutoSnapshot = false });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("enshrouded.bad_manifest");
        Directory.Exists(Path.Combine(game, "mods", "broken")).Should().BeFalse();
    }

    [Fact]
    public async Task AModWhoseIdIsTakenByAHandPlacedFolder_IsKeptOutOfTheGame()
    {
        var game = NewGame();
        var (service, _) = NewService();

        var handPlaced = Path.Combine(game, "mods", "Torches (old)");
        Directory.CreateDirectory(handPlaced);
        File.WriteAllText(Path.Combine(handPlaced, "mod.json"), Manifest("torches"));

        var result = await service.InstallAsync(
            ModZip("Torches", "torches"), game,
            new ModInstallationOptions { ModId = "Torches", GameId = "enshrouded", AutoSnapshot = false });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("enshrouded.duplicate_id").And.Contain("Torches (old)");
        Directory.Exists(Path.Combine(game, "mods", "torches")).Should().BeFalse();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private const string FakeLoader = "fake eml dll";

    private (ModInstallationService Service, Func<int> Downloads) NewService(string served = FakeLoader)
    {
        var loader = new RequiredFramework(
            "eml", "Test EML", "0.0.1", "https://example.invalid/dbghelp.dll",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FakeLoader))).ToLowerInvariant(),
            "dbghelp.dll", "dbghelp.dll");

        var registry = new GameModuleRegistry();
        registry.Register(new EnshroudedGameModule(loader: loader));

        var config = NewDir("config");
        var service = new ModInstallationService(
            _database, moduleRegistry: registry, stagingBasePath: Path.Combine(config, "staging"));

        var count = 0;
        service.Frameworks = new FrameworkProvisioner(
            Path.Combine(config, "archives"),
            (_, _) =>
            {
                count++;
                return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(served)));
            });

        return (service, () => count);
    }

    private string NewGame()
    {
        var game = NewDir("enshrouded");
        File.WriteAllText(Path.Combine(game, "enshrouded.exe"), "exe");
        File.WriteAllText(Path.Combine(game, "enshrouded.kfc_resources"), "resources");
        WriteKfc(Path.Combine(game, EnshroudedKfc.IndexFile), VanillaTag, "vanilla");
        return game;
    }

    private string ModZip(string folder, string id, string? manifest = null)
    {
        var path = Path.Combine(NewDir("zip"), folder.Replace(' ', '-') + ".zip");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (entry, content) in new[] { ($"{folder}/mod.json", manifest ?? Manifest(id)), ($"{folder}/src/mod.lua", "-- lua") })
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }

    private static string Manifest(string id) =>
        $$"""{ "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "capabilities": ["patch"] }""";

    private static EnshroudedInstallLayout Analyze(params (string Path, string Content)[] files)
    {
        var entries = files
            .Select(f => new ArchiveEntry { FullName = f.Path, Name = System.IO.Path.GetFileName(f.Path), Length = f.Content.Length })
            .ToList();
        var contents = files.ToDictionary(f => f.Path, f => f.Content);

        return EnshroudedArchiveAnalyzer.Analyze(entries, e => contents[e.FullName], "archive.zip");
    }

    private static string? Destination(EnshroudedInstallLayout layout, string entry) =>
        layout.FileRoutes.TryGetValue(entry, out var route) ? route.Destination : null;

    private static void WriteKfc(string path, string tag, string body) =>
        File.WriteAllBytes(path, KfcBytes(tag, body));

    /// <summary>A minimal KFC4 header: magic, size, 12, padding, then the version location.</summary>
    private static byte[] KfcBytes(string tag, string body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var tagBytes = Encoding.UTF8.GetBytes(tag);

        writer.Write(0x3443464Bu);
        writer.Write(0u);
        writer.Write(12u);
        writer.Write(0u);
        writer.Write(16u); // relative to offset 16: the tag starts at 32
        writer.Write((uint)tagBytes.Length);
        writer.Write(new byte[8]);
        writer.Write(tagBytes);
        writer.Write(Encoding.UTF8.GetBytes(body));

        return stream.ToArray();
    }

    private string NewDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"modular-{label}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();

        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup only.
            }
        }
    }
}
