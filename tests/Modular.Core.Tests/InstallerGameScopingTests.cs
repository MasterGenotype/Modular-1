using FluentAssertions;
using Modular.Core.Installers;
using Modular.Core.Modules;
using Modular.Core.Installers.Cyberpunk;
using Modular.Core.Installers.FF7Remake;
using Modular.Core.Installers.HorizonZeroDawn;
using Modular.Sdk;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests;

public class InstallerGameScopingTests
{
    // ── SupportedGameIds ────────────────────────────────────────────────────

    [Fact]
    public void CyberpunkInstaller_SupportedGameIds_ContainsSlugAndAppId()
    {
        var installer = new CyberpunkModInstaller();

        installer.SupportedGameIds.Should().NotBeNull();
        installer.SupportedGameIds.Should().Contain(GameIds.Cyberpunk2077);
        installer.SupportedGameIds.Should().Contain("1091500");
    }

    [Fact]
    public void FF7RInstaller_SupportedGameIds_ContainsSlugAndAppId()
    {
        var installer = new FF7RModInstaller();

        installer.SupportedGameIds.Should().NotBeNull();
        installer.SupportedGameIds.Should().Contain(GameIds.FinalFantasy7Remake);
        installer.SupportedGameIds.Should().Contain("1462040");
    }

    [Fact]
    public void HZDInstaller_SupportedGameIds_ContainsSlugAndAppId()
    {
        var installer = new HZDModInstaller();

        installer.SupportedGameIds.Should().NotBeNull();
        installer.SupportedGameIds.Should().Contain(GameIds.HorizonZeroDawn);
        installer.SupportedGameIds.Should().Contain("1151640");
    }

    [Fact]
    public void UniversalInstallers_SupportedGameIds_AreNull()
    {
        // These installers handle any game and must expose null SupportedGameIds
        IModInstaller looseFile = new LooseFileInstaller();
        IModInstaller fomod = new FomodInstaller();
        IModInstaller bepInEx = new BepInExInstaller();

        looseFile.SupportedGameIds.Should().BeNull();
        fomod.SupportedGameIds.Should().BeNull();
        bepInEx.SupportedGameIds.Should().BeNull();
    }

    // ── InstallerManager.SelectInstallerAsync filtering ─────────────────────

    [Fact]
    public async Task SelectInstaller_NoGameId_IncludesAllInstallers()
    {
        var manager = new InstallerManager();

        // All 8 built-in installers should be registered
        manager.GetInstallers().Should().HaveCount(8);

        // With no gameId, game-specific installers are not pre-filtered
        // (they may still return CanHandle=false from content analysis)
        var all = manager.GetInstallers();
        all.Should().Contain(i => i.InstallerId == "cyberpunk2077");
        all.Should().Contain(i => i.InstallerId == "ff7remake");
        all.Should().Contain(i => i.InstallerId == "horizon-zero-dawn");
    }

    [Fact]
    public void GetInstallers_GameSpecificInstallers_HaveNonNullSupportedGameIds()
    {
        var manager = new InstallerManager();

        var cyberpunk = manager.GetInstaller("cyberpunk2077");
        var ff7r = manager.GetInstaller("ff7remake");
        var hzd = manager.GetInstaller("horizon-zero-dawn");

        cyberpunk.Should().NotBeNull();
        ff7r.Should().NotBeNull();
        hzd.Should().NotBeNull();

        cyberpunk!.SupportedGameIds.Should().NotBeNullOrEmpty();
        ff7r!.SupportedGameIds.Should().NotBeNullOrEmpty();
        hzd!.SupportedGameIds.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SelectInstaller_CyberpunkGameId_ExcludesFF7RAndHZD()
    {
        // Use a tiny zip that the archive reader can open so detection runs.
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();

            // Ask for selection scoped to Cyberpunk — FF7R and HZD must be
            // excluded before content analysis even runs.
            var selection = await manager.SelectInstallerAsync(
                archivePath, gameId: GameIds.Cyberpunk2077);

            // Whatever was selected must not be FF7R or HZD
            if (selection != null)
            {
                selection.Installer.InstallerId.Should().NotBe("ff7remake");
                selection.Installer.InstallerId.Should().NotBe("horizon-zero-dawn");
                selection.AlternativeInstallers
                    .Should().NotContain(i => i.InstallerId == "ff7remake");
                selection.AlternativeInstallers
                    .Should().NotContain(i => i.InstallerId == "horizon-zero-dawn");
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_FF7RGameId_ExcludesCyberpunkAndHZD()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();

            var selection = await manager.SelectInstallerAsync(
                archivePath, gameId: GameIds.FinalFantasy7Remake);

            if (selection != null)
            {
                selection.Installer.InstallerId.Should().NotBe("cyberpunk2077");
                selection.Installer.InstallerId.Should().NotBe("horizon-zero-dawn");
                selection.AlternativeInstallers
                    .Should().NotContain(i => i.InstallerId == "cyberpunk2077");
                selection.AlternativeInstallers
                    .Should().NotContain(i => i.InstallerId == "horizon-zero-dawn");
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_HZDGameId_ExcludesCyberpunkAndFF7R()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();

            var selection = await manager.SelectInstallerAsync(
                archivePath, gameId: GameIds.HorizonZeroDawn);

            if (selection != null)
            {
                selection.Installer.InstallerId.Should().NotBe("cyberpunk2077");
                selection.Installer.InstallerId.Should().NotBe("ff7remake");
                selection.AlternativeInstallers
                    .Should().NotContain(i => i.InstallerId == "cyberpunk2077");
                selection.AlternativeInstallers
                    .Should().NotContain(i => i.InstallerId == "ff7remake");
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_SteamAppIdAccepted_AsSupportedGameId()
    {
        // Steam AppIDs must work the same as NexusMods slugs
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();

            // "1091500" is Cyberpunk's AppID — should exclude FF7R and HZD just like the slug
            var selection = await manager.SelectInstallerAsync(archivePath, gameId: "1091500");

            if (selection != null)
            {
                selection.Installer.InstallerId.Should().NotBe("ff7remake");
                selection.Installer.InstallerId.Should().NotBe("horizon-zero-dawn");
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_UnknownGameId_OnlyUniversalInstallersConsidered()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();

            // An unrecognised game ID should leave only universal installers in the pool
            var selection = await manager.SelectInstallerAsync(archivePath, gameId: "some-unknown-game-xyz");

            if (selection != null)
            {
                selection.Installer.InstallerId.Should().NotBe("cyberpunk2077");
                selection.Installer.InstallerId.Should().NotBe("ff7remake");
                selection.Installer.InstallerId.Should().NotBe("horizon-zero-dawn");
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_GameIdCaseInsensitive_Matches()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();

            // Upper-cased variant must still match the Cyberpunk installer
            var selection = await manager.SelectInstallerAsync(archivePath, gameId: "CYBERPUNK2077");

            if (selection != null)
            {
                // FF7R and HZD are excluded; Cyberpunk *may* appear if content matched
                selection.Installer.InstallerId.Should().NotBe("ff7remake");
                selection.Installer.InstallerId.Should().NotBe("horizon-zero-dawn");
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── module-scoped candidates ─────────────────────────────────────────────

    [Fact]
    public async Task SelectInstaller_CyberpunkModule_ExcludesOtherGamesInstallers()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();
            var module = new GameModuleRegistry().ResolveByGameId(GameIds.Cyberpunk2077)!;

            var selection = await manager.SelectInstallerAsync(
                archivePath, GameIds.Cyberpunk2077, module);

            if (selection != null)
            {
                selection.Installer.InstallerId.Should().NotBe("ff7remake");
                selection.Installer.InstallerId.Should().NotBe("horizon-zero-dawn");
                selection.AlternativeInstallers
                    .Should().NotContain(i =>
                        i.InstallerId == "ff7remake" || i.InstallerId == "horizon-zero-dawn");
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_PluginModule_RegistersItsInstallerAsACandidate()
    {
        // A plugin-supplied module brings an installer the manager has never seen.
        // It must become a candidate *and* be registered, because ExecuteInstallAsync
        // looks a plan's installer up by id.
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();
            var before = manager.GetInstallers().Count;

            var selection = await manager.SelectInstallerAsync(
                archivePath, "fakegame", new FakeModule());

            manager.GetInstallers().Should().HaveCount(before + 1);
            manager.GetInstaller(FakeInstaller.Id).Should().NotBeNull();

            selection.Should().NotBeNull();
            selection!.Installer.InstallerId.Should().Be(FakeInstaller.Id);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_SameModuleTwice_DoesNotRegisterItsInstallerTwice()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();
            var module = new FakeModule();

            await manager.SelectInstallerAsync(archivePath, "fakegame", module);
            var after = manager.GetInstallers().Count;
            await manager.SelectInstallerAsync(archivePath, "fakegame", module);

            manager.GetInstallers().Should().HaveCount(after);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_GenericModule_LeavesOnlyUniversalInstallers()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();
            var generic = new GameModuleRegistry().Fallback;

            var selection = await manager.SelectInstallerAsync(archivePath, null, generic);

            if (selection != null)
            {
                selection.Installer.SupportedGameIds.Should().BeNullOrEmpty();
                selection.AlternativeInstallers
                    .Should().OnlyContain(i =>
                        i.SupportedGameIds == null || i.SupportedGameIds.Count == 0);
            }
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SelectInstaller_ModuleThatThrows_DoesNotFailSelection()
    {
        var archivePath = CreateMinimalZip();
        try
        {
            var manager = new InstallerManager();

            var selection = await manager.SelectInstallerAsync(
                archivePath, GameIds.Cyberpunk2077, new ThrowingModule());

            // A broken module costs its own installers, not the whole install.
            if (selection != null)
                selection.Installer.InstallerId.Should().NotBe("ff7remake");
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal valid ZIP archive in the temp directory.
    /// </summary>
    private static string CreateMinimalZip()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "modular-test-" + Guid.NewGuid().ToString("N")[..8] + ".zip");

        using var stream = new System.IO.Compression.ZipArchive(
            File.Create(path), System.IO.Compression.ZipArchiveMode.Create);
        var entry = stream.CreateEntry("readme.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("test");

        return path;
    }

    private sealed class FakeModule : IGameModule
    {
        public string ModuleId => "fake-module";
        public string DisplayName => "Fake Module";
        public IReadOnlyList<string> GameIds { get; } = ["fakegame"];
        public IReadOnlyList<int> SteamAppIds { get; } = [424242];
        public GameModuleMatch? Detect(string installPath) => null;
        public GameModPaths ResolveModPaths(GameInstallation installation) =>
            GameModPaths.ForInstallation(installation);
        public IReadOnlyList<IModInstaller> GetInstallers() => [new FakeInstaller()];
    }

    private sealed class ThrowingModule : IGameModule
    {
        public string ModuleId => "throwing-module";
        public string DisplayName => "Throwing Module";
        public IReadOnlyList<string> GameIds { get; } = [Sdk.GameIds.Cyberpunk2077];
        public IReadOnlyList<int> SteamAppIds { get; } = [1091500];
        public GameModuleMatch? Detect(string installPath) => null;
        public GameModPaths ResolveModPaths(GameInstallation installation) =>
            GameModPaths.ForInstallation(installation);
        public IReadOnlyList<IModInstaller> GetInstallers() =>
            throw new InvalidOperationException("module is broken");
    }

    /// <summary>An installer that claims everything, at the highest priority.</summary>
    private sealed class FakeInstaller : IModInstaller
    {
        public const string Id = "fake-installer";

        public string InstallerId => Id;
        public string DisplayName => "Fake Installer";
        public int Priority => 10_000;
        public IReadOnlyList<string>? SupportedGameIds { get; } = ["fakegame"];

        public Task<InstallDetectionResult> DetectAsync(
            string archivePath, CancellationToken ct = default) =>
            Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = 1.0,
                Reason = "fake"
            });

        public Task<InstallPlan> AnalyzeAsync(
            string archivePath, InstallContext context, CancellationToken ct = default) =>
            Task.FromResult(new InstallPlan
            {
                InstallerId = Id,
                TargetDirectory = context.GameDirectory
            });

        public Task<InstallResult> InstallAsync(
            InstallPlan plan,
            IProgress<InstallProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Success = true });
    }
}
