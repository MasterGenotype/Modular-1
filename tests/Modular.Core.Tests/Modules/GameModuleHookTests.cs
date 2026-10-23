using FluentAssertions;
using Modular.Core.Installers;
using Modular.Core.Installers.Cyberpunk;
using Modular.Core.Installers.FF7Remake;
using Modular.Core.Modules;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// The per-game hooks: load-order translation, deploy-path mapping, launch requirements and
/// staged-layout validation.
/// </summary>
public sealed class GameModuleHookTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ── Cyberpunk: modlist.txt ──────────────────────────────────────────────

    [Fact]
    public async Task Cyberpunk_ModListIsWrittenInReverseModularOrder()
    {
        // The first legacy archive loaded wins, which is the opposite of Modular's rule, so
        // the mod the user put last has to be listed first.
        var game = NewGameDir();
        var module = new CyberpunkGameModule();

        await module.WriteLoadOrderAsync(
            Installation(game),
            [
                DeployedArchive("mod-a", 0, "a.archive"),
                DeployedArchive("mod-b", 1, "b.archive")
            ]);

        var lines = ModListLines(game);

        lines.Should().Equal("b.archive", "a.archive");
    }

    [Fact]
    public async Task Cyberpunk_ModListIncludesUnmanagedArchivesAfterTheManagedOnes()
    {
        // A partial modlist.txt has ambiguous semantics: anything omitted is left to the
        // engine's default ordering relative to the listed entries.
        var game = NewGameDir();
        WriteGameFile(game, "archive/pc/mod/zzz_unmanaged.archive", "z");
        WriteGameFile(game, "archive/pc/mod/aaa_unmanaged.archive", "a");
        WriteGameFile(game, "archive/pc/mod/managed.archive", "m");

        var module = new CyberpunkGameModule();

        await module.WriteLoadOrderAsync(
            Installation(game), [DeployedArchive("mod-a", 0, "managed.archive")]);

        ModListLines(game).Should().Equal(
            "managed.archive", "aaa_unmanaged.archive", "zzz_unmanaged.archive");
    }

    [Fact]
    public async Task Cyberpunk_NoArchivesLeft_RemovesItsOwnModList()
    {
        // After the last mod is uninstalled an empty list would be all that is left.
        var game = NewGameDir();
        var module = new CyberpunkGameModule();
        WriteGameFile(game, "archive/pc/mod/a.archive", "a");
        await module.WriteLoadOrderAsync(Installation(game), [DeployedArchive("mod-a", 0, "a.archive")]);
        File.Delete(Path.Combine(game, "archive", "pc", "mod", "a.archive"));

        await module.WriteLoadOrderAsync(Installation(game), []);

        File.Exists(Path.Combine(game, "archive", "pc", "mod", "modlist.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Cyberpunk_NoArchivesLeft_KeepsAModListTheUserWrote()
    {
        var game = NewGameDir();
        WriteGameFile(game, "archive/pc/mod/modlist.txt", "mine.archive");

        await new CyberpunkGameModule().WriteLoadOrderAsync(Installation(game), []);

        File.ReadAllText(Path.Combine(game, "archive", "pc", "mod", "modlist.txt")).Should().Be("mine.archive");
    }

    [Fact]
    public async Task Cyberpunk_NoArchiveFolder_WritesNothing()
    {
        var game = NewGameDir(createArchiveFolder: false);
        var module = new CyberpunkGameModule();

        await module.WriteLoadOrderAsync(
            Installation(game), [DeployedArchive("mod-a", 0, "a.archive")]);

        File.Exists(Path.Combine(game, "archive", "pc", "mod", "modlist.txt"))
            .Should().BeFalse();
    }

    [Fact]
    public void Cyberpunk_DescribeEngineOrder_ShowsTheReversedList()
    {
        var module = new CyberpunkGameModule();

        var description = module.DescribeEngineOrder(
            Installation(NewGameDir()),
            [
                DeployedArchive("mod-a", 0, "a.archive"),
                DeployedArchive("mod-b", 1, "b.archive")
            ]);

        description.Should().NotBeNull();
        description!.Value.Entries.Should().Equal("b.archive", "a.archive");
        description.Value.Rule.Should().Contain("first archive loaded wins");
        description.Value.Mechanism.Should().Be(CyberpunkGameModule.ModListPath);
    }

    // ── Cyberpunk: launch requirements ──────────────────────────────────────

    [Fact]
    public void Cyberpunk_RedMod_RequiresModdedAndAnOrderedDeployCommand()
    {
        var game = NewGameDir();
        var module = new CyberpunkGameModule();

        var requirements = module.GetLaunchRequirements(
            Installation(game),
            [
                DeployedFile("first", 0, "mods/FirstMod/info.json"),
                DeployedFile("second", 1, "mods/SecondMod/info.json")
            ]);

        requirements.LaunchArguments.Should().Contain("-modded");

        var action = requirements.PostDeployActions.Should().ContainSingle().Subject;
        action.Executable.Should().EndWith("redMod.exe");
        action.Arguments.Should().ContainInOrder("-mod=FirstMod", "-mod=SecondMod");
        action.Arguments.Should().Contain($"-root={game}");
    }

    [Fact]
    public void Cyberpunk_NoRedMod_NeedsNoLaunchArgument()
    {
        var module = new CyberpunkGameModule();

        var requirements = module.GetLaunchRequirements(
            Installation(NewGameDir()),
            [DeployedArchive("mod-a", 0, "a.archive")]);

        requirements.LaunchArguments.Should().BeEmpty();
        requirements.PostDeployActions.Should().BeEmpty();
    }

    [Fact]
    public void Cyberpunk_UnderProton_Red4ExtLoaderNeedsANativeFirstOverride()
    {
        // RED4ext installs itself as bin/x64/winmm.dll, which Proton ignores unless it is
        // overridden native-first — the single most common "mod does nothing" cause.
        var module = new CyberpunkGameModule();

        var requirements = module.GetLaunchRequirements(
            Installation(NewGameDir(), proton: true),
            [DeployedFile("red4ext", 0, "bin/x64/winmm.dll")]);

        requirements.WineDllOverrides.Should().Contain("winmm");
    }

    [Fact]
    public void Cyberpunk_OnWindows_NoWineOverridesAreReported()
    {
        var module = new CyberpunkGameModule();

        var requirements = module.GetLaunchRequirements(
            Installation(NewGameDir(), proton: false),
            [DeployedFile("red4ext", 0, "bin/x64/winmm.dll")]);

        requirements.WineDllOverrides.Should().BeEmpty();
    }

    // ── Cyberpunk: validation ───────────────────────────────────────────────

    [Fact]
    public void Cyberpunk_NestedArchive_IsAnError()
    {
        // The game only loads archives placed directly in archive/pc/mod.
        var module = new CyberpunkGameModule();

        var diagnostics = module.ValidateStaged(
            Installation(NewGameDir()),
            Staged("archive/pc/mod/sub/x.archive"));

        diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be("cp77.nested_archive");
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public void Cyberpunk_ArchiveDirectlyInModFolder_IsFine()
    {
        var module = new CyberpunkGameModule();

        module.ValidateStaged(
                Installation(NewGameDir()),
                Staged("archive/pc/mod/x.archive"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Cyberpunk_RedModWithoutInfoJson_IsWarned()
    {
        var module = new CyberpunkGameModule();

        var diagnostics = module.ValidateStaged(
            Installation(NewGameDir()),
            Staged("mods/MyMod/archives/thing.archive"));

        diagnostics.Should().Contain(d => d.Code == "cp77.redmod_missing_info");
    }

    [Fact]
    public void Cyberpunk_CetModWithoutInitLua_IsWarned()
    {
        var module = new CyberpunkGameModule();

        var diagnostics = module.ValidateStaged(
            Installation(NewGameDir()),
            Staged("bin/x64/plugins/cyber_engine_tweaks/mods/MyMod/main.lua"));

        diagnostics.Should().Contain(d => d.Code == "cp77.cet_missing_init");
    }

    [Fact]
    public void Cyberpunk_CetModWithInitLua_IsFine()
    {
        var module = new CyberpunkGameModule();

        module.ValidateStaged(
                Installation(NewGameDir()),
                Staged("bin/x64/plugins/cyber_engine_tweaks/mods/MyMod/init.lua"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Cyberpunk_GeneratedPaths_CoverTheToolWrittenFiles()
    {
        var generated = new CyberpunkGameModule().GetGeneratedPaths();

        Core.Deployment.DeploymentPlanner
            .MatchesAnyGlob("r6/cache/final.redscripts", generated).Should().BeTrue();
        Core.Deployment.DeploymentPlanner
            .MatchesAnyGlob("r6/cache/modded/thing.redscripts", generated).Should().BeTrue();
        Core.Deployment.DeploymentPlanner
            .MatchesAnyGlob(
                "bin/x64/plugins/cyber_engine_tweaks/mods/MyMod/db.sqlite3", generated)
            .Should().BeTrue();

        // A mod's own files are not generated.
        Core.Deployment.DeploymentPlanner
            .MatchesAnyGlob("archive/pc/mod/x.archive", generated).Should().BeFalse();
        Core.Deployment.DeploymentPlanner
            .MatchesAnyGlob("r6/scripts/MyMod/main.reds", generated).Should().BeFalse();
    }

    // ── FF7R: user config root ──────────────────────────────────────────────

    [Fact]
    public void FF7R_UserConfigIni_IsRoutedToTheDocumentsRoot()
    {
        // The analyzer sends Engine.ini to a Config/ folder in the game directory, where UE4
        // never reads it. The module is what redirects it to the real location.
        var module = new FF7RemakeGameModule();

        var target = module.MapDeployPath(new DeployPathContext(
            ModRoots.Game, "Config/Engine.ini", 0, 1, "mod",
            PackageTypes.UeUserConfig));

        target.RootName.Should().Be(FF7RemakeGameModule.UserConfigRoot);
        target.RelativePath.Should().Be("Engine.ini");
    }

    [Fact]
    public void FF7R_UserConfigRoot_ResolvesUnderTheProtonDocumentsFolder()
    {
        var module = new FF7RemakeGameModule();
        var installation = Installation(NewGameDir(), proton: true);

        var paths = module.ResolveModPaths(installation);
        var documents = paths.GetRoot(ModRoots.Documents)!;

        documents.Should().StartWith(installation.ProtonPrefixPath);
        paths.GetRoot(FF7RemakeGameModule.UserConfigRoot)
            .Should().Be(Path.Combine(
                documents,
                FF7RemakeGameModule.UserConfigRelativeToDocuments
                    .Replace('/', Path.DirectorySeparatorChar)));
    }

    // ── FF7R: pak order prefixes ────────────────────────────────────────────

    [Fact]
    public void FF7R_PaksAreOrderPrefixed()
    {
        var module = new FF7RemakeGameModule();

        Map(module, "a_P.pak", order: 0, count: 2).RelativePath
            .Should().Be($"{FF7RemakeGameModule.PaksModsFolder}/000_a_P.pak");
        Map(module, "b_P.pak", order: 1, count: 2).RelativePath
            .Should().Be($"{FF7RemakeGameModule.PaksModsFolder}/001_b_P.pak");
    }

    [Fact]
    public void FF7R_AReorderSwapsThePrefixes()
    {
        var module = new FF7RemakeGameModule();

        Map(module, "a_P.pak", order: 1, count: 2).RelativePath
            .Should().EndWith("001_a_P.pak");
        Map(module, "b_P.pak", order: 0, count: 2).RelativePath
            .Should().EndWith("000_b_P.pak");
    }

    [Fact]
    public void FF7R_IoStoreSiblingsAreRenamedWithTheirPak()
    {
        // A .pak, .utoc and .ucas trio must keep a shared stem, so they are prefixed together.
        var module = new FF7RemakeGameModule();

        foreach (var extension in new[] { ".pak", ".utoc", ".ucas", ".sig" })
        {
            Map(module, "bundle_P" + extension, order: 3, count: 10).RelativePath
                .Should().EndWith("003_bundle_P" + extension);
        }
    }

    [Fact]
    public void FF7R_NonPakFilesInModsAreLeftAlone()
    {
        var module = new FF7RemakeGameModule();

        Map(module, "readme.txt", order: 0, count: 1).RelativePath
            .Should().Be($"{FF7RemakeGameModule.PaksModsFolder}/readme.txt");
    }

    [Fact]
    public void FF7R_PaksOutsideModsAreNotPrefixed()
    {
        var module = new FF7RemakeGameModule();

        var target = module.MapDeployPath(new DeployPathContext(
            ModRoots.Game, "End/Content/Paks/base_P.pak", 0, 1, "mod"));

        target.RelativePath.Should().Be("End/Content/Paks/base_P.pak");
    }

    [Fact]
    public void FF7R_PrefixWidthGrowsWithTheModCount()
    {
        var module = new FF7RemakeGameModule();

        // Three digits is the floor; a four-digit list needs four, or the filenames would
        // stop sorting the way the numbers do.
        Map(module, "a_P.pak", order: 7, count: 12).RelativePath.Should().EndWith("007_a_P.pak");
        Map(module, "a_P.pak", order: 7, count: 1200).RelativePath.Should().EndWith("0007_a_P.pak");
    }

    [Fact]
    public void FF7R_PinnedLastListIsEmptyButTheMechanismExists()
    {
        // No FF7 Remake loader pak is known; the list is the mechanism, not the data.
        FF7RemakeGameModule.PinnedLastPaks.Should().BeEmpty();
    }

    [Fact]
    public void FF7R_ASelfSortingLastPak_IsWarnedAboutRatherThanSilentlyReordered()
    {
        var module = new FF7RemakeGameModule();

        var diagnostics = module.ValidateStaged(
            Installation(NewGameDir()),
            Staged($"{FF7RemakeGameModule.PaksModsFolder}/ZGameInstanceLoader_P.pak"));

        diagnostics.Should().Contain(d => d.Code == "ff7r.pak_self_sorting_last");
    }

    // ── FF7R: validation ────────────────────────────────────────────────────

    [Fact]
    public void FF7R_PakWithoutThePSuffix_IsWarned()
    {
        var module = new FF7RemakeGameModule();

        var diagnostics = module.ValidateStaged(
            Installation(NewGameDir()),
            Staged($"{FF7RemakeGameModule.PaksModsFolder}/PlainMod.pak"));

        diagnostics.Should().Contain(d => d.Code == "ff7r.pak_missing_p_suffix");
    }

    [Fact]
    public void FF7R_PakOutsideMods_IsWarned()
    {
        var module = new FF7RemakeGameModule();

        var diagnostics = module.ValidateStaged(
            Installation(NewGameDir()),
            Staged("End/Content/Paks/Stray_P.pak"));

        diagnostics.Should().Contain(d => d.Code == "ff7r.pak_outside_mods");
    }

    [Fact]
    public void FF7R_WellFormedPak_IsFine()
    {
        var module = new FF7RemakeGameModule();

        module.ValidateStaged(
                Installation(NewGameDir()),
                Staged($"{FF7RemakeGameModule.PaksModsFolder}/CoolMod_P.pak"))
            .Should().BeEmpty();
    }

    [Fact]
    public void FF7R_MigotoOrDxvk_NotesTheDx11Requirement()
    {
        var module = new FF7RemakeGameModule();

        module.ValidateStaged(
                Installation(NewGameDir()),
                Staged("End/Binaries/Win64/d3d11.dll"))
            .Should().Contain(d => d.Code == "ff7r.requires_dx11");
    }

    [Fact]
    public void FF7R_UnderProton_HookDllsNeedNativeFirstOverrides()
    {
        var module = new FF7RemakeGameModule();

        var requirements = module.GetLaunchRequirements(
            Installation(NewGameDir(), proton: true),
            [
                DeployedFile("hook", 0, "End/Binaries/Win64/dxgi.dll"),
                DeployedFile("hook", 0, "End/Binaries/Win64/xinput1_3.dll")
            ]);

        requirements.WineDllOverrides.Should().BeEquivalentTo(["dxgi", "xinput1_3"]);
    }

    // ── HZD ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Hzd_OrderPrefixing_IsOffByDefault()
    {
        // It is unverified whether the engine requires the literal Patch_ prefix, and getting
        // that wrong would silently stop every patch loading.
        var module = new HorizonZeroDawnGameModule();

        module.OrderPrefixEnabled.Should().BeFalse();

        module.MapDeployPath(new DeployPathContext(
                ModRoots.Game, "Packed_DX12/Patch_Cool.bin", 3, 10, "mod"))
            .RelativePath.Should().Be("Packed_DX12/Patch_Cool.bin");
    }

    [Fact]
    public void Hzd_OrderPrefixing_WhenEnabled_KeepsThePatchPrefix()
    {
        var module = new HorizonZeroDawnGameModule(orderPrefixEnabled: true);

        module.MapDeployPath(new DeployPathContext(
                ModRoots.Game, "Packed_DX12/Patch_Cool.bin", 3, 10, "mod"))
            .RelativePath.Should().Be("Packed_DX12/Patch_003_Cool.bin");
    }

    [Fact]
    public void Hzd_OrderPrefixing_LeavesNonPatchFilesAlone()
    {
        var module = new HorizonZeroDawnGameModule(orderPrefixEnabled: true);

        module.MapDeployPath(new DeployPathContext(
                ModRoots.Game, "Packed_DX12/Other.bin", 3, 10, "mod"))
            .RelativePath.Should().Be("Packed_DX12/Other.bin");

        module.MapDeployPath(new DeployPathContext(
                ModRoots.Game, "winhttp.dll", 3, 10, "mod"))
            .RelativePath.Should().Be("winhttp.dll");
    }

    [Fact]
    public void Hzd_PrefetchPatch_ProducesAWarning()
    {
        // Several mods ship this file and tools regenerate it; the last mod wins and the user
        // needs to know to regenerate it afterwards.
        var module = new HorizonZeroDawnGameModule();

        var diagnostics = module.ValidateStaged(
            Installation(NewGameDir()),
            Staged($"Packed_DX12/{HorizonZeroDawnGameModule.PrefetchPatch}"));

        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Code.Should().Be("hzd.prefetch_conflict");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Message.Should().Contain("regenerate");
    }

    [Fact]
    public void Hzd_AnOrdinaryPatch_IsFine()
    {
        new HorizonZeroDawnGameModule()
            .ValidateStaged(Installation(NewGameDir()), Staged("Packed_DX12/Patch_Cool.bin"))
            .Should().BeEmpty();
    }

    // ── route labelling ─────────────────────────────────────────────────────

    [Fact]
    public void CyberpunkAnalyzer_LabelsDeclaredPathsAsDeclared()
    {
        var layout = CyberpunkArchiveAnalyzer.Analyze(Entries(
            "Wrapper/archive/pc/mod/cool.archive",
            "Wrapper/r6/scripts/cool/cool.reds",
            "Wrapper/red4ext/plugins/Cool/Cool.dll"));

        layout.FileRoutes.Values.Should().OnlyContain(r => r.RouteSource == RouteSource.Declared);

        Route(layout.FileRoutes, "Wrapper/archive/pc/mod/cool.archive")
            .PackageType.Should().Be(PackageTypes.Cp77LegacyArchive);
        Route(layout.FileRoutes, "Wrapper/r6/scripts/cool/cool.reds")
            .PackageType.Should().Be(PackageTypes.Cp77Redscript);
        Route(layout.FileRoutes, "Wrapper/red4ext/plugins/Cool/Cool.dll")
            .PackageType.Should().Be(PackageTypes.Cp77Red4ExtPlugin);
    }

    [Fact]
    public void CyberpunkAnalyzer_LabelsLooseFilesAsHeuristic()
    {
        // The wrapper folder gives the archive its Cyberpunk anchor; the loose files beside
        // it are routed by extension.
        var layout = CyberpunkArchiveAnalyzer.Analyze(Entries(
            "archive/pc/mod/anchor.archive",
            "loose.reds",
            "tweaks.yaml",
            "settings.ini"));

        Route(layout.FileRoutes, "loose.reds").RouteSource.Should().Be(RouteSource.Heuristic);
        Route(layout.FileRoutes, "loose.reds").Destination.Should().Be("r6/scripts/loose.reds");
        Route(layout.FileRoutes, "settings.ini").RouteSource.Should().Be(RouteSource.Heuristic);
    }

    [Fact]
    public void CyberpunkAnalyzer_LabelsARedModAsMetadata()
    {
        // A REDmod is identified by its info.json manifest, not by its extension.
        var layout = CyberpunkArchiveAnalyzer.Analyze(Entries(
            "mods/CoolMod/info.json",
            "mods/CoolMod/archives/cool.archive"));

        var route = Route(layout.FileRoutes, "mods/CoolMod/info.json");
        route.RouteSource.Should().Be(RouteSource.Metadata);
        route.PackageType.Should().Be(PackageTypes.Cp77RedMod);
        route.Destination.Should().Be("mods/CoolMod/info.json");
    }

    [Fact]
    public void FF7RAnalyzer_LabelsPaksAndHooks()
    {
        var layout = FF7RArchiveAnalyzer.Analyze(Entries(
            "End/Content/Paks/~mods/CoolMod_P.pak",
            "End/Binaries/Win64/dxgi.dll"));

        Route(layout.FileRoutes, "End/Content/Paks/~mods/CoolMod_P.pak")
            .PackageType.Should().Be(PackageTypes.UePak);
        Route(layout.FileRoutes, "End/Binaries/Win64/dxgi.dll")
            .PackageType.Should().Be(PackageTypes.UeHook);
    }

    [Fact]
    public void FF7RAnalyzer_LabelsAUserConfigIniSoTheModuleCanRerouteIt()
    {
        var layout = FF7RArchiveAnalyzer.Analyze(Entries(
            "End/Content/Paks/~mods/anchor_P.pak",
            "Engine.ini"));

        Route(layout.FileRoutes, "Engine.ini")
            .PackageType.Should().Be(PackageTypes.UeUserConfig);
    }

    [Fact]
    public void FF7RAnalyzer_ARebirthShapedArchive_DoesNotRaiseTheRemakeAnchor()
    {
        // Rebirth ships the same End/ layout, so the structural anchors cannot discriminate.
        // Without an explicit exclusion a Rebirth archive would be claimed as Remake.
        var layout = FF7RArchiveAnalyzer.Analyze(Entries(
            "End/Binaries/Win64/ff7rebirth_.exe",
            "End/Mods/Reunion/plugin.dll",
            "ZGameInstanceLoader.pak"));

        // The loader pak is the tell: with the Remake anchor raised it would have been
        // routed into ~mods as an FF7R pak.
        Route(layout.FileRoutes, "ZGameInstanceLoader.pak")
            .Destination.Should().NotStartWith("End/Content/Paks/~mods");
    }

    [Fact]
    public void FF7RAnalyzer_ARemakeNamedMod_IsStillRecognised()
    {
        // Narrowing the "ff7" hint must not cost a legitimately named Remake mod.
        var layout = FF7RArchiveAnalyzer.Analyze(Entries("CoolFF7Mod_P.pak"));

        Route(layout.FileRoutes, "CoolFF7Mod_P.pak")
            .Destination.Should().Be("End/Content/Paks/~mods/CoolFF7Mod_P.pak");
    }

    // ── declarative routes ──────────────────────────────────────────────────

    [Fact]
    public void PackageRouteValidator_FlatLayout_RejectsAnInventedModFolder()
    {
        var module = new FlatRouteModule();

        var diagnostics = PackageRouteValidator.Validate(module, new StagedMod
        {
            ModId = "mod",
            Files =
            [
                new StagedFile(ModRoots.LocalAppData, "Mods/MyMod/thing.pak", "bg3.pak")
            ]
        });

        diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be("route.flat_layout_extra_folder");
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public void PackageRouteValidator_FlatLayout_AcceptsAFileDirectlyInTheDestination()
    {
        var module = new FlatRouteModule();

        PackageRouteValidator.Validate(module, new StagedMod
        {
            ModId = "mod",
            Files = [new StagedFile(ModRoots.LocalAppData, "Mods/thing.pak", "bg3.pak")]
        }).Should().BeEmpty();
    }

    [Fact]
    public void PackageRouteValidator_PreserveLayout_AllowsSubfolders()
    {
        var module = new FlatRouteModule();

        PackageRouteValidator.Validate(module, new StagedMod
        {
            ModId = "mod",
            Files =
            [
                new StagedFile(ModRoots.Game, "bin/NativeMods/Nested/thing.dll",
                    "bg3.script_extender")
            ]
        }).Should().BeEmpty();
    }

    [Fact]
    public void PackageRouteValidator_ModuleWithNoRoutes_ReportsNothing()
    {
        PackageRouteValidator.Validate(
                new CyberpunkGameModule(),
                new StagedMod { ModId = "mod", Files = [new StagedFile("game", "a/b/c.pak")] })
            .Should().BeEmpty();
    }

    [Fact]
    public void RootTokens_ResolveToRootNames()
    {
        ModRoots.ResolveToken(ModRoots.GameToken).Should().Be(ModRoots.Game);
        ModRoots.ResolveToken(ModRoots.LocalAppDataToken).Should().Be(ModRoots.LocalAppData);
        ModRoots.ResolveToken(ModRoots.DocumentsToken).Should().Be(ModRoots.Documents);

        // A module-defined token resolves to its own root name.
        ModRoots.ResolveToken("$USERCONFIG").Should().Be(FF7RemakeGameModule.UserConfigRoot);

        // A plain root name passes through.
        ModRoots.ResolveToken(ModRoots.Game).Should().Be(ModRoots.Game);
    }

    [Fact]
    public void RootTokens_AreAcceptedByGameModPaths()
    {
        var installation = Installation(NewGameDir(), proton: true);
        var paths = new FF7RemakeGameModule().ResolveModPaths(installation);

        paths.GetRoot(ModRoots.GameToken).Should().Be(installation.InstallPath);
        paths.GetRoot("$USERCONFIG").Should().Be(
            paths.GetRoot(FF7RemakeGameModule.UserConfigRoot));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static DeployTarget Map(
        FF7RemakeGameModule module, string fileName, int order, int count) =>
        module.MapDeployPath(new DeployPathContext(
            ModRoots.Game,
            $"{FF7RemakeGameModule.PaksModsFolder}/{fileName}",
            order,
            count,
            "mod"));

    private static FileRoute Route(
        IReadOnlyDictionary<string, FileRoute> routes, string entry)
    {
        routes.Should().ContainKey(entry);
        return routes[entry];
    }

    private static IReadOnlyList<ArchiveEntry> Entries(params string[] paths) =>
        paths.Select(p => new ArchiveEntry
        {
            FullName = p,
            Name = Path.GetFileName(p),
            Length = 16,
            IsDirectory = false
        }).ToList();

    private static StagedMod Staged(params string[] gameRelativePaths) => new()
    {
        ModId = "mod",
        StagingPath = "/staging/mod",
        Files = gameRelativePaths
            .Select(p => new StagedFile(ModRoots.Game, p))
            .ToList()
    };

    private static DeployedMod DeployedArchive(string modId, int order, string fileName) =>
        DeployedFile(modId, order, $"{CyberpunkGameModule.LegacyArchiveFolder}/{fileName}");

    private static DeployedMod DeployedFile(string modId, int order, string relativePath) => new()
    {
        ModId = modId,
        OrderIndex = order,
        Enabled = true,
        DeployedFiles = [new StagedFile(ModRoots.Game, relativePath)]
    };

    private static GameInstallation Installation(string gameDir, bool proton = false) => new()
    {
        GameId = "testgame",
        SteamAppId = 424242,
        InstallPath = gameDir,
        ProtonPrefixPath = proton ? Path.Combine(gameDir, "..", "pfx") : null
    };

    private static List<string> ModListLines(string gameDir) =>
        File.ReadAllLines(Path.Combine(gameDir, "archive", "pc", "mod", "modlist.txt"))
            .Where(line => !line.StartsWith('#'))
            .ToList();

    private static void WriteGameFile(string gameDir, string relativePath, string content)
    {
        var full = Path.Combine(gameDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string NewGameDir(bool createArchiveFolder = true)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "modular-hooks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        if (createArchiveFolder)
            Directory.CreateDirectory(Path.Combine(dir, "archive", "pc", "mod"));

        return dir;
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

    /// <summary>
    /// A module whose destinations are declared as data, to exercise the route table.
    /// </summary>
    /// <remarks>
    /// Shaped after the BG3 routes the design records: ordinary paks go flat into AppData
    /// <c>Mods</c>, and the script extender preserves its tree under <c>$GAME/bin</c>.
    /// </remarks>
    private sealed class FlatRouteModule : GameModuleBase
    {
        public override string ModuleId => "flat-routes";
        public override string DisplayName => "Flat Routes";
        public override IReadOnlyList<string> GameIds { get; } = ["flatgame"];
        public override IReadOnlyList<int> SteamAppIds { get; } = [999999];
        protected override IReadOnlyList<string> PrimaryAnchors { get; } = ["flat.exe"];
        public override IReadOnlyList<IModInstaller> GetInstallers() => Array.Empty<IModInstaller>();

        public override IReadOnlyList<PackageRoute> GetPackageRoutes() =>
        [
            new("bg3.pak", ModRoots.LocalAppDataToken, "Mods", PackageLayout.FlatFiles),
            new("bg3.script_extender", ModRoots.GameToken, "bin", PackageLayout.Preserve)
        ];
    }
}
