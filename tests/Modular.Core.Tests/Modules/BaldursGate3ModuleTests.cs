using System.Text;
using FluentAssertions;
using Modular.Core.Installers.BaldursGate3;
using Modular.Core.Modules;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// Baldur's Gate 3: the pak / loose-file split, load-order bands, and the check for loose
/// files that shadow a pak.
/// </summary>
public sealed class BaldursGate3ModuleTests : IDisposable
{
    private const string SavagePak = "Dragonborn Unleashed - Savage Resilience.pak";
    private const string DurgePak = "Durges_mangled_head_3060ca1e-6a6a-079e-e999-7398f55bee4b.pak";

    private readonly List<string> _tempDirs = new();

    // ── analyzer: where files go ────────────────────────────────────────────

    [Fact]
    public void Analyzer_APak_GoesFlatIntoTheUserModsRoot()
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries($"Some Folder/{SavagePak}", "info.json"));

        var route = layout.FileRoutes[$"Some Folder/{SavagePak}"];
        route.Root.Should().Be(BaldursGate3GameModule.ModsRoot);
        route.Destination.Should().Be(SavagePak);
        route.PackageType.Should().Be(PackageTypes.Bg3Pak);

        // Mod-manager metadata is not something the game reads.
        layout.FileRoutes.Should().NotContainKey("info.json");
        layout.HasPak.Should().BeTrue();
        layout.HasLooseData.Should().BeFalse();
    }

    [Fact]
    public void Analyzer_ATreeRootedInsidePublic_IsPlacedUnderDataPublic()
    {
        // The game looks loading screens up at Public/Game/GUI/..., so an archive zipped
        // from the Game folder is one level short.
        const string entry = "Game/GUI/Assets/LoadingScreen/loadScreen_BGartwork_1.dds";

        var layout = BG3ArchiveAnalyzer.Analyze(Entries(entry));

        var route = layout.FileRoutes[entry];
        route.Root.Should().BeNull();
        route.Destination.Should().Be(
            "Data/Public/Game/GUI/Assets/LoadingScreen/loadScreen_BGartwork_1.dds");
        route.PackageType.Should().Be(PackageTypes.Bg3LooseData);
        route.RouteSource.Should().Be(RouteSource.Heuristic);
        layout.HasLooseData.Should().BeTrue();
    }

    [Theory]
    [InlineData("Data/Public/Game/x.xaml", "Data/Public/Game/x.xaml")]
    [InlineData("MyMod v2/Data/Generated/Public/a.dds", "Data/Generated/Public/a.dds")]
    [InlineData("Public/Shared/b.lsx", "Data/Public/Shared/b.lsx")]
    [InlineData("wrapper/public/Shared/b.lsx", "Data/Public/Shared/b.lsx")]
    [InlineData("Localization/English/c.loca", "Data/Localization/English/c.loca")]
    public void Analyzer_ADeclaredDataTree_IsAnchoredOnItsDataFolder(string entry, string expected)
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries(entry));

        layout.FileRoutes[entry].Destination.Should().Be(expected);
        layout.FileRoutes[entry].RouteSource.Should().Be(RouteSource.Declared);
    }

    [Fact]
    public void Analyzer_APakInsideADataTree_StillGoesToTheUserModsRoot()
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries("Data/Extra.pak", "Data/Public/Game/x.xaml"));

        layout.FileRoutes["Data/Extra.pak"].Root.Should().Be(BaldursGate3GameModule.ModsRoot);
        layout.FileRoutes["Data/Extra.pak"].Destination.Should().Be("Extra.pak");
        layout.HasPak.Should().BeTrue();
        layout.HasLooseData.Should().BeTrue();
    }

    [Fact]
    public void Analyzer_NothingRecognisable_IsNotClaimed()
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries("readme.txt", "screens/preview.png"));

        layout.IsRecognized.Should().BeFalse();
    }

    // ── deploy mapping ──────────────────────────────────────────────────────

    [Fact]
    public void MapDeployPath_BandOutranksModularOrder_SoSavageLoadsBeforeDurge()
    {
        var module = new BaldursGate3GameModule();
        var staging = NewTempDir();

        // What the two paks really pack: a race's progressions, and head models.
        var savagePath = Path.Combine(staging, SavagePak);
        var durgePath = Path.Combine(staging, DurgePak);
        Bg3TestPak.WriteModWith(
            savagePath, "b1f36451-c34a-4bd7-9776-3ba387576025", "Dragonborn Unleashed",
            ["Public/Dragonborn Unleashed/Progressions/Progressions.lsx"]);
        Bg3TestPak.WriteModWith(
            durgePath, "3060ca1e-6a6a-079e-e999-7398f55bee4b", "Durges mangled head",
            ["Public/Durge/Content/DMH_Heads/head.lsf", "Generated/Public/Durge/DurgeMH/head.GR2"]);

        // Durge was installed first, so Modular's own order alone would load it first.
        var durge = MapPak(module, DurgePak, order: 0, count: 2, durgePath).RelativePath;
        var savage = MapPak(module, SavagePak, order: 1, count: 2, savagePath).RelativePath;

        savage.Should().Be($"50-001_{SavagePak}");
        durge.Should().Be($"70-000_{DurgePak}");
        string.CompareOrdinal(savage, durge).Should().BeNegative();
    }

    [Fact]
    public void MapDeployPath_TheNexusCategoryDecidesWhenItIsKnown()
    {
        var categories = new FakeCategories { ["556"] = "Races", ["9702"] = "Character Customisation" };
        var module = new BaldursGate3GameModule(categories: categories);

        var savage = module.MapDeployPath(new DeployPathContext(
            BaldursGate3GameModule.ModsRoot, SavagePak, 1, 2,
            "Special - Savage Resilience-556-11-2-1733684094", PackageTypes.Bg3Pak)).RelativePath;
        var durge = module.MapDeployPath(new DeployPathContext(
            BaldursGate3GameModule.ModsRoot, DurgePak, 0, 2,
            "Durge's mangled head - Dark Urge Dragonborn CC-9702-1-0-0-16-1774555674", PackageTypes.Bg3Pak)).RelativePath;

        savage.Should().StartWith("30-001_");
        durge.Should().StartWith("70-000_");
        categories.Asked.Should().OnlyContain(domain => domain == "baldursgate3");
    }

    [Fact]
    public void MapDeployPath_NothingInAFileNameDecidesTheBand()
    {
        var module = new BaldursGate3GameModule();

        // Every word here used to pull the pak into a different band.
        MapPak(module, "Dragonborn Wizard Armor Hair Patch.pak", order: 0, count: 1)
            .RelativePath.Should().StartWith("50-000_");
    }

    [Fact]
    public void MapDeployPath_WithinABand_ModularOrderDecides()
    {
        var module = new BaldursGate3GameModule();

        var first = MapPak(module, "A.pak", order: 3, count: 12).RelativePath;
        var second = MapPak(module, "B.pak", order: 11, count: 12).RelativePath;

        string.CompareOrdinal(first, second).Should().BeNegative();
    }

    [Fact]
    public void MapDeployPath_LooseDataFiles_KeepTheirPathAndRoot()
    {
        var module = new BaldursGate3GameModule();
        const string path = "Data/Public/Game/GUI/Assets/LoadingScreen/a.dds";

        var target = module.MapDeployPath(new DeployPathContext(
            ModRoots.Game, path, 5, 9, "mod", PackageTypes.Bg3LooseData));

        target.RootName.Should().Be(ModRoots.Game);
        target.RelativePath.Should().Be(path);
    }

    // ── roots ───────────────────────────────────────────────────────────────

    [Fact]
    public void ModsRoot_UnderProton_IsInsideThePrefix()
    {
        var installation = Installation(NewGameDir(), proton: true);

        new BaldursGate3GameModule().ResolveModPaths(installation)
            .GetRoot(BaldursGate3GameModule.ModsRoot)
            .Should().Be(Path.Combine(
                installation.ProtonPrefixPath!, "drive_c", "users", "steamuser", "AppData",
                "Local", "Larian Studios", "Baldur's Gate 3", "Mods"));
    }

    [Fact]
    public void ModsRoot_NativeBuild_IgnoresALeftoverProtonPrefix()
    {
        var game = NewGameDir();
        File.WriteAllText(Path.Combine(game, "bin", "bg3"), "elf");

        new BaldursGate3GameModule().ResolveModPaths(Installation(game, proton: true))
            .GetRoot(BaldursGate3GameModule.ModsRoot)
            .Should().Be(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Larian Studios", "Baldur's Gate 3", "Mods"));
    }

    [Fact]
    public void Detect_ClaimsABg3Directory()
    {
        new BaldursGate3GameModule().Detect(NewGameDir()).Should().NotBeNull();
        new BaldursGate3GameModule().Detect(NewTempDir()).Should().BeNull();
    }

    // ── pak reader and the loose-vs-pak check ───────────────────────────────

    [Fact]
    public void LspkReader_ListsThePackedPaths()
    {
        var pak = Path.Combine(NewTempDir(), "a.pak");
        WritePak(pak, "Public/Game/GUI/a.dds", "Mods/Thing/meta.lsx");

        LspkReader.ReadFileNames(pak).Should().Equal("Public/Game/GUI/a.dds", "Mods/Thing/meta.lsx");
    }

    [Fact]
    public void LspkReader_AFileThatIsNotAPak_YieldsNothing()
    {
        var path = Path.Combine(NewTempDir(), "junk.pak");
        File.WriteAllText(path, "this is not a package at all, just some text");

        LspkReader.ReadFileNames(path).Should().BeEmpty();
    }

    [Fact]
    public void ValidateStaged_ALooseFileThatAnInstalledPakAlsoPacks_IsWarned()
    {
        var installation = Installation(NewGameDir(), proton: true);
        var module = new BaldursGate3GameModule();

        var mods = module.ResolveModPaths(installation).GetRoot(BaldursGate3GameModule.ModsRoot)!;
        Directory.CreateDirectory(mods);
        WritePak(Path.Combine(mods, "70-000_Screens.pak"), "Public/Game/GUI/a.dds");
        WritePak(Path.Combine(mods, "30-001_Other.pak"), "Public/Other/b.lsx");

        var diagnostics = module.ValidateStaged(installation, new StagedMod
        {
            ModId = "loose",
            StagingPath = NewTempDir(),
            Files = [new StagedFile(ModRoots.Game, "Data/Public/Game/GUI/a.dds")]
        });

        diagnostics.Should().ContainSingle();
        diagnostics[0].Code.Should().Be("bg3.loose_file_shadows_pak");
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostics[0].Message.Should().Contain("70-000_Screens.pak");
    }

    [Fact]
    public void ValidateStaged_APakWhosePathsAlreadyExistLooseInData_IsWarned()
    {
        var installation = Installation(NewGameDir());
        var loose = Path.Combine(installation.InstallPath, "Data", "Public", "Game", "GUI", "a.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllText(loose, "x");

        var staging = NewTempDir();
        Directory.CreateDirectory(Path.Combine(staging, BaldursGate3GameModule.ModsRoot));
        WritePak(
            Path.Combine(staging, BaldursGate3GameModule.ModsRoot, "Screens.pak"),
            "Public/Game/GUI/a.dds", "Public/Game/GUI/unrelated.dds");

        var diagnostics = new BaldursGate3GameModule().ValidateStaged(installation, new StagedMod
        {
            ModId = "pak",
            StagingPath = staging,
            Files = [new StagedFile(BaldursGate3GameModule.ModsRoot, "Screens.pak", PackageTypes.Bg3Pak)]
        });

        diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be("bg3.pak_shadowed_by_loose_file");
    }

    [Fact]
    public void ValidateStaged_NoSharedPaths_IsQuiet()
    {
        var installation = Installation(NewGameDir(), proton: true);
        var module = new BaldursGate3GameModule();

        var mods = module.ResolveModPaths(installation).GetRoot(BaldursGate3GameModule.ModsRoot)!;
        Directory.CreateDirectory(mods);
        WritePak(Path.Combine(mods, "Other.pak"), "Public/Other/b.lsx");

        module.ValidateStaged(installation, new StagedMod
        {
            ModId = "loose",
            StagingPath = NewTempDir(),
            Files = [new StagedFile(ModRoots.Game, "Data/Public/Game/GUI/a.dds")]
        }).Should().BeEmpty();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static DeployTarget MapPak(
        BaldursGate3GameModule module, string fileName, int order, int count, string? stagedPath = null) =>
        module.MapDeployPath(new DeployPathContext(
            BaldursGate3GameModule.ModsRoot, fileName, order, count, "mod", PackageTypes.Bg3Pak, stagedPath));

    private sealed class FakeCategories : Dictionary<string, string>, Modular.Core.Metadata.IModCategorySource
    {
        public List<string> Asked { get; } = [];

        public string? GetCategory(string gameDomain, string modId)
        {
            Asked.Add(gameDomain);
            return TryGetValue(modId, out var category) ? category : null;
        }

        public Task WarmAsync(string gameDomain, IReadOnlyList<string> modIds, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static IReadOnlyList<ArchiveEntry> Entries(params string[] paths) =>
        paths.Select(p => new ArchiveEntry
        {
            FullName = p,
            Name = Path.GetFileName(p),
            Length = 16,
            IsDirectory = false
        }).ToList();

    /// <summary>
    /// Writes a version-18 LSPK holding only a file table. The table is stored as an LZ4
    /// block of literals, which is valid LZ4 and needs no compressor.
    /// </summary>
    private static void WritePak(string path, params string[] names)
    {
        const int entrySize = 272;
        var table = new byte[names.Length * entrySize];

        for (var i = 0; i < names.Length; i++)
            Encoding.UTF8.GetBytes(names[i]).CopyTo(table, i * entrySize);

        var block = new List<byte> { 0xF0 };
        var remaining = table.Length - 15;
        for (; remaining >= 255; remaining -= 255)
            block.Add(255);
        block.Add((byte)remaining);
        block.AddRange(table);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("LSPK"));
        writer.Write(18u);
        writer.Write(40L);              // file list offset: straight after the header
        writer.Write(block.Count + 8);  // file list size
        writer.Write((byte)0);          // flags
        writer.Write((byte)0);          // priority
        writer.Write(new byte[16]);     // md5
        writer.Write((ushort)1);        // parts
        writer.Write(names.Length);
        writer.Write(block.Count);
        writer.Write(block.ToArray());
    }

    private GameInstallation Installation(string gameDir, bool proton = false) => new()
    {
        GameId = "baldursgate3",
        SteamAppId = 1086940,
        InstallPath = gameDir,
        ProtonPrefixPath = proton ? NewTempDir() : null
    };

    private string NewGameDir()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "Data"));
        File.WriteAllText(Path.Combine(dir, "Data", "Gustav.pak"), string.Empty);

        // Script Extender in place, as on a modded install; its absence is tested on its own.
        File.WriteAllText(Path.Combine(dir, "bin", "DWrite.dll"), string.Empty);
        return dir;
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modular-bg3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }
}
