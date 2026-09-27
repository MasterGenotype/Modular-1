using FluentAssertions;
using Modular.Core.Modules;
using Modular.Sdk;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// Registry resolution by slug, AppID and path detection, plus the anchor rules that
/// keep sibling games (FF7 Rebirth, HZD Remastered) out of the wrong module.
/// </summary>
public sealed class GameModuleRegistryTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ── identifier resolution ───────────────────────────────────────────────

    [Fact]
    public void BuiltInModules_AreRegistered()
    {
        var registry = new GameModuleRegistry();

        registry.Modules.Select(m => m.ModuleId)
            .Should().BeEquivalentTo(["cyberpunk2077", "ff7remake", "horizonzerodawn"]);
    }

    [Theory]
    [InlineData(GameIds.Cyberpunk2077, "cyberpunk2077")]
    [InlineData(GameIds.FinalFantasy7Remake, "ff7remake")]
    [InlineData(GameIds.HorizonZeroDawn, "horizonzerodawn")]
    public void ResolveByGameId_Slug_FindsModule(string slug, string expectedModuleId)
    {
        new GameModuleRegistry().ResolveByGameId(slug)!.ModuleId.Should().Be(expectedModuleId);
    }

    [Theory]
    [InlineData("CYBERPUNK2077", "cyberpunk2077")]
    [InlineData("FinalFantasy7REMAKE", "ff7remake")]
    public void ResolveByGameId_IsCaseInsensitive(string slug, string expectedModuleId)
    {
        new GameModuleRegistry().ResolveByGameId(slug)!.ModuleId.Should().Be(expectedModuleId);
    }

    [Theory]
    [InlineData("1091500", "cyberpunk2077")]
    [InlineData("1462040", "ff7remake")]
    [InlineData("1151640", "horizonzerodawn")]
    public void ResolveByGameId_AppIdString_FindsModule(string appId, string expectedModuleId)
    {
        new GameModuleRegistry().ResolveByGameId(appId)!.ModuleId.Should().Be(expectedModuleId);
    }

    [Theory]
    [InlineData(1091500, "cyberpunk2077")]
    [InlineData(1462040, "ff7remake")]
    [InlineData(1151640, "horizonzerodawn")]
    public void ResolveByAppId_FindsModule(int appId, string expectedModuleId)
    {
        new GameModuleRegistry().ResolveByAppId(appId)!.ModuleId.Should().Be(expectedModuleId);
    }

    [Fact]
    public void ResolveByGameId_GameBananaNumericId_DoesNotMatchAnUnrelatedModule()
    {
        // GameBanana ids share the numeric namespace with Steam AppIDs, so an id no
        // module declares must not be silently attached to one.
        new GameModuleRegistry().ResolveByGameId("9999999").Should().BeNull();
    }

    [Fact]
    public void ResolveByGameId_Unknown_ReturnsNull()
    {
        new GameModuleRegistry().ResolveByGameId("not-a-game").Should().BeNull();
    }

    [Fact]
    public void Resolve_Unknown_FallsBackToGenericModule()
    {
        new GameModuleRegistry()
            .Resolve("not-a-game", null, null)
            .ModuleId.Should().Be(GenericGameModule.Id);
    }

    [Fact]
    public void GetByModuleId_Generic_ReturnsFallback()
    {
        var registry = new GameModuleRegistry();

        registry.GetByModuleId(GenericGameModule.Id).Should().BeSameAs(registry.Fallback);
    }

    // ── plugin registration ─────────────────────────────────────────────────

    [Fact]
    public void Register_PluginModule_IsResolvableByItsGameIds()
    {
        var registry = new GameModuleRegistry();
        registry.Register(new FakePluginModule());

        registry.ResolveByGameId("fakegame")!.ModuleId.Should().Be("fake");
        registry.ResolveByAppId(424242)!.ModuleId.Should().Be("fake");
    }

    [Fact]
    public void Register_SameModuleIdTwice_Replaces()
    {
        var registry = new GameModuleRegistry();
        var before = registry.Modules.Count;

        registry.Register(new FakePluginModule());
        registry.Register(new FakePluginModule());

        registry.Modules.Should().HaveCount(before + 1);
    }

    [Fact]
    public void Register_ModuleOverridingABuiltIn_TakesPrecedence()
    {
        var registry = new GameModuleRegistry();
        registry.Register(new OverridingCyberpunkModule());

        registry.ResolveByAppId(1091500)!.DisplayName.Should().Be("Overridden Cyberpunk");
        registry.Modules.Count(m => m.ModuleId == "cyberpunk2077").Should().Be(1);
    }

    // ── path detection ──────────────────────────────────────────────────────

    [Fact]
    public void ResolveByPath_CyberpunkShapedDirectory_MatchesCyberpunk()
    {
        var dir = CreateGameDir(
            files: ["bin/x64/Cyberpunk2077.exe"],
            dirs: ["archive/pc/content", "r6", "engine"]);

        var resolution = new GameModuleRegistry().ResolveByPath(dir);

        resolution.Should().NotBeNull();
        resolution!.Module.ModuleId.Should().Be("cyberpunk2077");
        resolution.Match.Confidence.Should().BeGreaterThan(0.9);
        resolution.Match.Evidence.Should().Contain("bin/x64/Cyberpunk2077.exe");
    }

    [Fact]
    public void ResolveByPath_MissingAnExeAnchor_DoesNotMatchCyberpunk()
    {
        // archive/pc/content alone is not enough; the executable carries the match.
        var dir = CreateGameDir(dirs: ["archive/pc/content"]);

        new GameModuleRegistry().ResolveByPath(dir)!
            .Module.ModuleId.Should().Be(GenericGameModule.Id);
    }

    [Fact]
    public void ResolveByPath_FF7RemakeShapedDirectory_MatchesFF7Remake()
    {
        var dir = CreateGameDir(
            files: ["End/Binaries/Win64/ff7remake_.exe"],
            dirs: ["End/Content/Paks/~mods"]);

        new GameModuleRegistry().ResolveByPath(dir)!
            .Module.ModuleId.Should().Be("ff7remake");
    }

    [Fact]
    public void ResolveByPath_RebirthShapedDirectory_DoesNotMatchFF7Remake()
    {
        // FF7 Rebirth ships the same End/ project layout but a completely different
        // mod ecosystem, so it must fall through to the generic module.
        var dir = CreateGameDir(
            files: ["End/Binaries/Win64/ff7rebirth_.exe"],
            dirs: ["End/Content/Paks/~mods"]);

        new GameModuleRegistry().ResolveByPath(dir)!
            .Module.ModuleId.Should().Be(GenericGameModule.Id);
    }

    [Fact]
    public void ResolveByPath_DirectoryWithBothExecutables_RejectsFF7Remake()
    {
        // A rejecting anchor vetoes the match even when every primary anchor is present.
        var dir = CreateGameDir(
            files:
            [
                "End/Binaries/Win64/ff7remake_.exe",
                "End/Binaries/Win64/ff7rebirth_.exe"
            ],
            dirs: ["End/Content/Paks"]);

        new GameModuleRegistry().ResolveByPath(dir)!
            .Module.ModuleId.Should().Be(GenericGameModule.Id);
    }

    [Fact]
    public void ResolveByPath_HzdCompleteEditionShapedDirectory_MatchesHzd()
    {
        var dir = CreateGameDir(files: ["HorizonZeroDawn.exe"], dirs: ["Packed_DX12"]);

        new GameModuleRegistry().ResolveByPath(dir)!
            .Module.ModuleId.Should().Be("horizonzerodawn");
    }

    [Fact]
    public void ResolveByPath_HzdRemasteredShapedDirectory_DoesNotMatchHzd()
    {
        // Remastered has no Packed_DX12 workflow at all — its mods are root-level
        // DLL loaders, which the loose-file installer handles under the generic module.
        var dir = CreateGameDir(
            files: ["HorizonZeroDawnRemastered.exe", "winhttp.dll", "mod_config.ini"]);

        new GameModuleRegistry().ResolveByPath(dir)!
            .Module.ModuleId.Should().Be(GenericGameModule.Id);
    }

    [Fact]
    public void ResolveByPath_NonExistentDirectory_ReturnsNull()
    {
        new GameModuleRegistry()
            .ResolveByPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
            .Should().BeNull();
    }

    [Fact]
    public void Detect_IsCaseInsensitivePerSegment()
    {
        // Proton-hosted Windows games are routinely installed with unpredictable casing.
        var dir = CreateGameDir(
            files: ["BIN/X64/cyberpunk2077.EXE"],
            dirs: ["Archive/PC/Content"]);

        new GameModuleRegistry().ResolveByPath(dir)!
            .Module.ModuleId.Should().Be("cyberpunk2077");
    }

    // ── module contributions ────────────────────────────────────────────────

    [Fact]
    public void BuiltInModules_ContributeTheirGameSpecificInstaller()
    {
        var registry = new GameModuleRegistry();

        registry.ResolveByGameId(GameIds.Cyberpunk2077)!.GetInstallers()
            .Should().ContainSingle().Which.InstallerId.Should().Be("cyberpunk2077");
        registry.ResolveByGameId(GameIds.FinalFantasy7Remake)!.GetInstallers()
            .Should().ContainSingle().Which.InstallerId.Should().Be("ff7remake");
        registry.ResolveByGameId(GameIds.HorizonZeroDawn)!.GetInstallers()
            .Should().ContainSingle().Which.InstallerId.Should().Be("horizon-zero-dawn");
    }

    [Fact]
    public void GenericModule_ContributesNoInstallers()
    {
        new GameModuleRegistry().Fallback.GetInstallers().Should().BeEmpty();
    }

    [Fact]
    public void OptionalHooks_OnTheGenericModule_AreNoOps()
    {
        var module = new GameModuleRegistry().Fallback;

        module.GetGeneratedPaths().Should().BeEmpty();
        module.GetPackageRoutes().Should().BeEmpty();
        module.GetDeployConstraints().Should().BeEmpty();
        module.GetLaunchRequirements(new GameInstallation(), []).IsEmpty.Should().BeTrue();
        module.ValidateStaged(new GameInstallation(), new StagedMod()).Should().BeEmpty();
        module.DescribeEngineOrder(new GameInstallation(), []).Should().BeNull();

        // The default mapping is the identity.
        var mapped = module.MapDeployPath(
            new DeployPathContext(ModRoots.Game, "archive/pc/mod/a.archive", 0, 1, "mod"));
        mapped.RootName.Should().Be(ModRoots.Game);
        mapped.RelativePath.Should().Be("archive/pc/mod/a.archive");
        mapped.PinnedLast.Should().BeFalse();
    }

    [Fact]
    public void BuiltInModules_DeclareNoPackageRoutesOrDeployConstraints()
    {
        // The three built-in modules route through their analyzers and have no verified
        // link problem, so the declarative table and the constraint hook stay empty.
        foreach (var module in new GameModuleRegistry().Modules)
        {
            module.GetPackageRoutes().Should().BeEmpty(module.ModuleId);
            module.GetDeployConstraints().Should().BeEmpty(module.ModuleId);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string CreateGameDir(
        IEnumerable<string>? files = null,
        IEnumerable<string>? dirs = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "modular-module-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        foreach (var dir in dirs ?? [])
            Directory.CreateDirectory(Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var file in files ?? [])
        {
            var full = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, string.Empty);
        }

        return root;
    }

    /// <inheritdoc />
    public void Dispose()
    {
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

    private sealed class FakePluginModule : IGameModule
    {
        public string ModuleId => "fake";
        public string DisplayName => "Fake Plugin Game";
        public IReadOnlyList<string> GameIds { get; } = ["fakegame", "424242"];
        public IReadOnlyList<int> SteamAppIds { get; } = [424242];
        public GameModuleMatch? Detect(string installPath) => null;
        public GameModPaths ResolveModPaths(GameInstallation installation) =>
            GameModPaths.ForInstallation(installation);
        public IReadOnlyList<IModInstaller> GetInstallers() => Array.Empty<IModInstaller>();
    }

    private sealed class OverridingCyberpunkModule : IGameModule
    {
        public string ModuleId => "cyberpunk2077";
        public string DisplayName => "Overridden Cyberpunk";
        public IReadOnlyList<string> GameIds { get; } = [Sdk.GameIds.Cyberpunk2077, "1091500"];
        public IReadOnlyList<int> SteamAppIds { get; } = [1091500];
        public GameModuleMatch? Detect(string installPath) => null;
        public GameModPaths ResolveModPaths(GameInstallation installation) =>
            GameModPaths.ForInstallation(installation);
        public IReadOnlyList<IModInstaller> GetInstallers() => Array.Empty<IModInstaller>();
    }
}
