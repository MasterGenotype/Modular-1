using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Installers;
using Modular.Core.Modules;
using Modular.Sdk;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// Staging redirection and the one-step path normalisation that every later stage
/// depends on: a plan's destinations become <c>(root, root-relative path)</c> pairs
/// regardless of which subdirectory of the game the installer happened to target.
/// </summary>
public sealed class StagingRedirectionTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ── plan offset ─────────────────────────────────────────────────────────

    [Fact]
    public void GetPlanOffset_PlanTargetsTheGameRoot_IsEmpty()
    {
        var game = NewDir("game");

        StagingManager.GetPlanOffset(game, game).Should().BeEmpty();
    }

    [Fact]
    public void GetPlanOffset_PlanTargetsASubdirectory_IsTheGameRelativePath()
    {
        var game = NewDir("game");
        var target = Path.Combine(game, "End", "Content", "Paks", "~mods");

        StagingManager.GetPlanOffset(game, target)
            .Should().Be("End/Content/Paks/~mods");
    }

    [Fact]
    public void GetPlanOffset_PlanTargetsOutsideTheGame_IsEmpty()
    {
        // Nothing is silently written above the staging root.
        var game = NewDir("game");
        var elsewhere = NewDir("elsewhere");

        StagingManager.GetPlanOffset(game, elsewhere).Should().BeEmpty();
    }

    // ── redirection ─────────────────────────────────────────────────────────

    [Fact]
    public void RedirectPlan_CyberpunkShapedPlan_StagesUnderTheGameRoot()
    {
        var manager = NewManager();
        var game = NewDir("game");
        var staging = manager.GetModStagingDirectory("1091500", "mod-a");

        var plan = new InstallPlan
        {
            InstallerId = "cyberpunk2077",
            TargetDirectory = game,
            Operations =
            [
                Extract("archive/pc/mod/thing.archive"),
                Extract("r6/scripts/thing/thing.reds"),
                Extract("bin/x64/plugins/cyber_engine_tweaks/mods/thing/init.lua")
            ]
        };

        manager.RedirectPlan(plan, game, staging);

        plan.TargetDirectory.Should().Be(staging);
        plan.Operations.Select(o => o.DestinationPath).Should().BeEquivalentTo(
        [
            "game/archive/pc/mod/thing.archive",
            "game/r6/scripts/thing/thing.reds",
            "game/bin/x64/plugins/cyber_engine_tweaks/mods/thing/init.lua"
        ]);
    }

    [Fact]
    public void RedirectPlan_UnrealPakShapedPlan_PrefixesThePlanOffset()
    {
        // UnrealPakInstaller points the plan at ~mods, so its raw routes are
        // ~mods-relative. They must come out game-root-relative.
        var manager = NewManager();
        var game = NewDir("game");
        var paksMods = Path.Combine(game, "End", "Content", "Paks", "~mods");
        var staging = manager.GetModStagingDirectory("1462040", "pak-mod");

        var plan = new InstallPlan
        {
            InstallerId = "unreal-pak",
            TargetDirectory = paksMods,
            Operations = [Extract("CoolMod_P.pak")]
        };

        manager.RedirectPlan(plan, game, staging);

        plan.TargetDirectory.Should().Be(staging);
        plan.Operations[0].DestinationPath
            .Should().Be("game/End/Content/Paks/~mods/CoolMod_P.pak");
    }

    [Fact]
    public void RedirectPlan_OperationWithAnExplicitRoot_IsNotOffset()
    {
        // A destination that names its own root is already relative to that root.
        var manager = NewManager();
        var game = NewDir("game");
        var paksMods = Path.Combine(game, "End", "Content", "Paks", "~mods");
        var staging = manager.GetModStagingDirectory("1462040", "ini-mod");

        var plan = new InstallPlan
        {
            InstallerId = "ff7remake",
            TargetDirectory = paksMods,
            Operations =
            [
                new FileOperation
                {
                    Type = FileOperationType.Extract,
                    SourcePath = "Engine.ini",
                    DestinationPath = "Engine.ini",
                    TargetRoot = FF7RemakeGameModule.UserConfigRoot
                }
            ]
        };

        manager.RedirectPlan(plan, game, staging);

        plan.Operations[0].DestinationPath.Should().Be("userconfig/Engine.ini");
    }

    [Fact]
    public void RedirectPlan_DirectoryOperationAtThePlanRoot_BecomesTheOffsetItself()
    {
        var manager = NewManager();
        var game = NewDir("game");
        var paksMods = Path.Combine(game, "End", "Content", "Paks", "~mods");
        var staging = manager.GetModStagingDirectory("1462040", "dir-mod");

        var plan = new InstallPlan
        {
            InstallerId = "unreal-pak",
            TargetDirectory = paksMods,
            Operations =
            [
                new FileOperation
                {
                    Type = FileOperationType.CreateDirectory,
                    DestinationPath = string.Empty
                }
            ]
        };

        manager.RedirectPlan(plan, game, staging);

        plan.Operations[0].DestinationPath.Should().Be("game/End/Content/Paks/~mods");
    }

    [Fact]
    public void RedirectPlan_CarriesRoutingLabelsOntoTheNormalisedPair()
    {
        var manager = NewManager();
        var game = NewDir("game");
        var staging = manager.GetModStagingDirectory("1091500", "labelled");

        var operation = new FileOperation
        {
            Type = FileOperationType.Extract,
            SourcePath = "x.archive",
            DestinationPath = "archive/pc/mod/x.archive",
            PackageType = "cp77.legacy_archive",
            RouteSource = RouteSource.Declared
        };

        var plan = new InstallPlan
        {
            InstallerId = "cyberpunk2077",
            TargetDirectory = game,
            Operations = [operation]
        };

        var redirection = manager.RedirectPlan(plan, game, staging);
        var normalised = redirection.Normalize(new FileOperation
        {
            DestinationPath = "archive/pc/mod/x.archive",
            PackageType = "cp77.legacy_archive",
            RouteSource = RouteSource.Declared
        });

        normalised.RootName.Should().Be(ModRoots.Game);
        normalised.PackageType.Should().Be("cp77.legacy_archive");
        normalised.RouteSource.Should().Be(RouteSource.Declared);
    }

    // ── sessions ────────────────────────────────────────────────────────────

    [Fact]
    public void CreateSession_UsesAPerGamePerModDirectory()
    {
        var manager = NewManager();

        using var session = manager.CreateSession("cs1", "1091500", "my-mod");

        session.StagingDirectory.Should().Be(
            Path.Combine(manager.BasePath, "1091500", "my-mod"));
        Directory.Exists(session.StagingDirectory).Should().BeTrue();
        session.IsPersistent.Should().BeTrue();
    }

    [Fact]
    public void CreateSession_Restaging_ClearsThePreviousContent()
    {
        var manager = NewManager();

        using (var first = manager.CreateSession("cs1", "1091500", "my-mod"))
        {
            File.WriteAllText(first.GetStagedPath("game/stale.txt"), "stale");
        }

        using var second = manager.CreateSession("cs2", "1091500", "my-mod");

        Directory.EnumerateFileSystemEntries(second.StagingDirectory).Should().BeEmpty();
    }

    [Fact]
    public void PersistentSession_SurvivesDisposalWithoutACommit()
    {
        // The staged tree is the deployable copy, so a failed deploy must leave it intact.
        var manager = NewManager();
        string directory;

        using (var session = manager.CreateSession("cs1", "1091500", "keep-me"))
        {
            directory = session.StagingDirectory;
            File.WriteAllText(session.GetStagedPath("game/a.txt"), "a");
        }

        Directory.Exists(directory).Should().BeTrue();
        File.Exists(Path.Combine(directory, "game", "a.txt")).Should().BeTrue();
    }

    [Fact]
    public void TransientSession_IsDeletedWhenDisposedUncommitted()
    {
        var manager = NewManager();
        string directory;

        using (var session = manager.CreateTransientSession("cs1"))
        {
            directory = session.StagingDirectory;
            session.IsPersistent.Should().BeFalse();
            File.WriteAllText(session.GetStagedPath("x.txt"), "x");
        }

        Directory.Exists(directory).Should().BeFalse();
    }

    [Fact]
    public void Commit_WithKeepStagedCopy_LeavesTheStagedFileInPlace()
    {
        var manager = NewManager();
        var target = NewDir("target");

        using var session = manager.CreateSession("cs1", "1091500", "mod");
        var staged = session.GetStagedPath("a.txt");
        File.WriteAllText(staged, "payload");
        session.RecordStagedFile("a.txt", staged);

        var result = session.Commit(target, createBackups: true, keepStagedCopy: true);

        result.Success.Should().BeTrue();
        File.Exists(staged).Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "a.txt")).Should().Be("payload");
    }

    [Fact]
    public void DeleteModStaging_RemovesTheTree()
    {
        var manager = NewManager();
        using (manager.CreateSession("cs1", "1091500", "gone")) { }

        manager.DeleteModStaging("1091500", "gone").Should().BeTrue();
        manager.DeleteModStaging("1091500", "gone").Should().BeFalse();
    }

    // ── enumeration ─────────────────────────────────────────────────────────

    [Fact]
    public void EnumerateStagedFiles_ReadsBackRootAndRootRelativePath()
    {
        var manager = NewManager();
        using var session = manager.CreateSession("cs1", "1462040", "multi");

        File.WriteAllText(session.GetStagedPath("game/End/Content/Paks/~mods/a_P.pak"), "pak");
        File.WriteAllText(session.GetStagedPath("userconfig/Engine.ini"), "ini");

        var files = StagingManager.EnumerateStagedFiles(session.StagingDirectory);

        files.Should().BeEquivalentTo(new[]
        {
            new StagedFile("game", "End/Content/Paks/~mods/a_P.pak"),
            new StagedFile("userconfig", "Engine.ini")
        });
    }

    [Fact]
    public void EnumerateStagedFiles_IgnoresUnderscorePrefixedDirectories()
    {
        // _overwrite holds files displaced by a user edit; they are not part of the mod.
        var manager = NewManager();
        using var session = manager.CreateSession("cs1", "1091500", "mod");

        File.WriteAllText(session.GetStagedPath("game/a.txt"), "a");
        File.WriteAllText(session.GetStagedPath("_overwrite/game/b.txt"), "b");

        StagingManager.EnumerateStagedFiles(session.StagingDirectory)
            .Should().ContainSingle().Which.RootName.Should().Be("game");
    }

    [Fact]
    public void BuildStagedMod_AppliesRoutingLabels()
    {
        var manager = NewManager();
        using var session = manager.CreateSession("cs1", "1091500", "mod");
        File.WriteAllText(session.GetStagedPath("game/archive/pc/mod/x.archive"), "a");

        var stagedMod = StagingManager.BuildStagedMod(
            "mod", session.StagingDirectory,
            new Dictionary<string, StagedFile>
            {
                ["game/archive/pc/mod/x.archive"] = new(
                    "game", "archive/pc/mod/x.archive",
                    "cp77.legacy_archive", RouteSource.Declared)
            });

        var file = stagedMod.Files.Should().ContainSingle().Subject;
        file.PackageType.Should().Be("cp77.legacy_archive");
        file.RouteSource.Should().Be(RouteSource.Declared);
    }

    // ── end-to-end through a real installer ─────────────────────────────────

    [Fact]
    public async Task RedirectedPlan_CyberpunkZip_StagesTheWholeTreeUnderGame()
    {
        var manager = NewManager();
        var game = NewDir("game");
        var archive = CreateZip("cp77",
            ("Wrapper/archive/pc/mod/cool.archive", "archive-bytes"),
            ("Wrapper/r6/scripts/cool/cool.reds", "reds"),
            ("Wrapper/bin/x64/plugins/cyber_engine_tweaks/mods/cool/init.lua", "lua"));

        var staging = manager.GetModStagingDirectory("1091500", "cool");
        var staged = await StageThroughInstallerAsync(manager, archive, game, staging, "1091500", "cool");

        staged.Should().OnlyContain(f => f.RootName == "game");
        staged.Select(f => f.RelativePath).Should().Contain("archive/pc/mod/cool.archive");

        // Nothing reached the game folder.
        Directory.EnumerateFileSystemEntries(game).Should().BeEmpty();
    }

    [Fact]
    public async Task RedirectedPlan_FF7RPakOnlyZip_StagesThePakUnderMods()
    {
        var manager = NewManager();
        var game = NewDir("game");
        Directory.CreateDirectory(Path.Combine(game, "End", "Content", "Paks"));

        var archive = CreateZip("ff7r-pak", ("CoolFF7Mod_P.pak", "pak-bytes"));
        var staging = manager.GetModStagingDirectory("1462040", "ff7r-pak");

        var staged = await StageThroughInstallerAsync(
            manager, archive, game, staging, "1462040", "ff7r-pak",
            GameIds.FinalFantasy7Remake);

        staged.Select(f => $"{f.RootName}/{f.RelativePath}")
            .Should().Contain("game/End/Content/Paks/~mods/CoolFF7Mod_P.pak");
    }

    [Fact]
    public async Task RedirectedPlan_FF7RMultiTypeZip_StillStagesThePakUnderMods()
    {
        // The regression this exists for: a pak shipped alongside an INI or a hook DLL
        // used to land in the game root, where UE4 never mounts it.
        var manager = NewManager();
        var game = NewDir("game");
        Directory.CreateDirectory(Path.Combine(game, "End", "Content", "Paks"));

        var archive = CreateZip("ff7r-multi",
            ("CoolFF7Mod_P.pak", "pak-bytes"),
            ("Engine.ini", "[Core]"),
            ("dxgi.dll", "hook"));

        var staging = manager.GetModStagingDirectory("1462040", "ff7r-multi");

        var staged = await StageThroughInstallerAsync(
            manager, archive, game, staging, "1462040", "ff7r-multi",
            GameIds.FinalFantasy7Remake);

        var paths = staged.Select(f => $"{f.RootName}/{f.RelativePath}").ToList();

        paths.Should().Contain("game/End/Content/Paks/~mods/CoolFF7Mod_P.pak");
        paths.Should().NotContain("game/CoolFF7Mod_P.pak");
        paths.Should().Contain("game/End/Binaries/Win64/dxgi.dll");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<StagedFile>> StageThroughInstallerAsync(
        StagingManager manager,
        string archivePath,
        string gameDirectory,
        string stagingRoot,
        string gameKey,
        string modId,
        string? gameId = null)
    {
        var installerManager = new InstallerManager();
        var module = new GameModuleRegistry().ResolveByGameId(gameId);

        var selection = await installerManager.SelectInstallerAsync(archivePath, gameId, module);
        selection.Should().NotBeNull();

        var plan = await selection!.Installer.AnalyzeAsync(archivePath, new InstallContext
        {
            GameDirectory = gameDirectory,
            GameId = gameId,
            ModId = modId,
            AllowOverwrite = true
        });

        using var session = manager.CreateSession("cs-" + modId, gameKey, modId);
        manager.RedirectPlan(plan, gameDirectory, session.StagingDirectory);

        var result = await installerManager.ExecuteInstallAsync(plan);
        result.Success.Should().BeTrue(result.Error);

        return StagingManager.EnumerateStagedFiles(session.StagingDirectory);
    }

    private static FileOperation Extract(string destination) => new()
    {
        Type = FileOperationType.Extract,
        SourcePath = destination,
        DestinationPath = destination
    };

    private StagingManager NewManager() => new(NewDir("staging"));

    private string NewDir(string label)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), $"modular-{label}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateZip(string label, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(NewDir("zip"), label + ".zip");

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
            writer.Write(content);
        }

        return path;
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
}
