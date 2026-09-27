using FluentAssertions;
using Modular.Core.GameDetection;
using Modular.Core.Modules;
using Modular.Sdk;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// Resolution of a user-supplied game reference against a fake Steam library:
/// AppID, slug, display name and path, plus the walk-up to the real game root and
/// the named roots that land inside a Proton prefix.
/// </summary>
public sealed class GameInstallationResolverTests : IDisposable
{
    private readonly string _steamRoot;
    private readonly List<string> _tempDirs = new();

    /// <summary>Builds a fake Steam library holding Cyberpunk 2077 and FF7 Remake.</summary>
    public GameInstallationResolverTests()
    {
        _steamRoot = NewTempDir("steam");

        Directory.CreateDirectory(Path.Combine(_steamRoot, "steamapps"));

        WriteGame(
            appId: 1091500,
            name: "Cyberpunk 2077",
            installDir: "Cyberpunk 2077",
            buildId: "17624596",
            files: ["bin/x64/Cyberpunk2077.exe"],
            dirs: ["archive/pc/content", "r6/scripts", "engine"],
            withProtonPrefix: true);

        WriteGame(
            appId: 1462040,
            name: "FINAL FANTASY VII REMAKE INTERGRADE",
            installDir: "FINAL FANTASY VII REMAKE INTERGRADE",
            buildId: "11223344",
            files: ["End/Binaries/Win64/ff7remake_.exe"],
            dirs: ["End/Content/Paks/~mods"],
            withProtonPrefix: true);
    }

    private GameInstallationResolver CreateResolver() =>
        new(
            registry: new GameModuleRegistry(),
            steamScanner: new SteamGameScanner(
                new SteamLibraryScanner(new FakeSteamLocator(_steamRoot))),
            database: null);

    // ── identifier lookup ───────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_AppId_ResolvesFullInstallation()
    {
        var installation = await CreateResolver().ResolveAsync("1091500", persist: false);

        installation.Should().NotBeNull();
        installation!.SteamAppId.Should().Be(1091500);
        installation.GameId.Should().Be(GameIds.Cyberpunk2077);
        installation.ModuleId.Should().Be("cyberpunk2077");
        installation.Store.Should().Be(GameStore.Steam);
        installation.BuildId.Should().Be("17624596");
        installation.DisplayName.Should().Be("Cyberpunk 2077");
        installation.LibraryRoot.Should().Be(_steamRoot);
        installation.GameKey.Should().Be("1091500");
    }

    [Fact]
    public async Task ResolveAsync_Slug_ResolvesThroughTheModulesAppIds()
    {
        var installation = await CreateResolver()
            .ResolveAsync(GameIds.Cyberpunk2077, persist: false);

        installation!.SteamAppId.Should().Be(1091500);
        installation.ModuleId.Should().Be("cyberpunk2077");
    }

    [Fact]
    public async Task ResolveAsync_DisplayNameSubstring_Resolves()
    {
        var installation = await CreateResolver().ResolveAsync("FINAL FANTASY", persist: false);

        installation!.SteamAppId.Should().Be(1462040);
        installation.ModuleId.Should().Be("ff7remake");
    }

    [Fact]
    public async Task ResolveAsync_UnknownReference_ReturnsNull()
    {
        var installation = await CreateResolver()
            .ResolveAsync("no-such-game-anywhere", persist: false);

        installation.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_FindsTheProtonPrefix()
    {
        var installation = await CreateResolver().ResolveAsync("1091500", persist: false);

        installation!.IsProton.Should().BeTrue();
        installation.ProtonPrefixPath.Should().Be(
            Path.Combine(_steamRoot, "steamapps", "compatdata", "1091500", "pfx"));
    }

    [Fact]
    public async Task ResolveAsync_NoCompatdata_ReportsNoProtonPrefix()
    {
        WriteGame(
            appId: 1151640,
            name: "Horizon Zero Dawn",
            installDir: "Horizon Zero Dawn",
            buildId: "5551212",
            files: ["HorizonZeroDawn.exe"],
            dirs: ["Packed_DX12"],
            withProtonPrefix: false);

        var installation = await CreateResolver().ResolveAsync("1151640", persist: false);

        installation!.ModuleId.Should().Be("horizonzerodawn");
        installation.ProtonPrefixPath.Should().BeNull();
        installation.IsProton.Should().BeFalse();
    }

    // ── path lookup and walk-up ─────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_GameRootPath_RecoversTheSteamMetadata()
    {
        var gameDir = GameDir("Cyberpunk 2077");

        var installation = await CreateResolver().ResolveAsync(gameDir, persist: false);

        installation!.InstallPath.Should().Be(gameDir);
        installation.SteamAppId.Should().Be(1091500);
        installation.BuildId.Should().Be("17624596");
        installation.Store.Should().Be(GameStore.Steam);
        installation.ModuleId.Should().Be("cyberpunk2077");
    }

    [Fact]
    public async Task ResolveAsync_SubdirectoryPath_WalksUpToTheGameRoot()
    {
        // Pointing at bin/x64 is a common mistake and must still resolve.
        var gameDir = GameDir("Cyberpunk 2077");
        var subDir = Path.Combine(gameDir, "bin", "x64");

        var installation = await CreateResolver().ResolveAsync(subDir, persist: false);

        installation!.InstallPath.Should().Be(gameDir);
        installation.ModuleId.Should().Be("cyberpunk2077");
        installation.SteamAppId.Should().Be(1091500);
    }

    [Fact]
    public void WalkUpToGameRoot_FromFF7RBinariesWin64_FindsTheGameRoot()
    {
        var gameDir = GameDir("FINAL FANTASY VII REMAKE INTERGRADE");
        var subDir = Path.Combine(gameDir, "End", "Binaries", "Win64");

        var (root, resolution) = CreateResolver().WalkUpToGameRoot(subDir);

        root.Should().Be(gameDir);
        resolution!.Module.ModuleId.Should().Be("ff7remake");
    }

    [Fact]
    public void WalkUpToGameRoot_BeyondTheWalkUpBudget_StopsAtTheStartingDirectory()
    {
        var gameDir = GameDir("Cyberpunk 2077");
        var deep = Path.Combine(gameDir, "a", "b", "c", "d");
        Directory.CreateDirectory(deep);

        var (root, resolution) = CreateResolver().WalkUpToGameRoot(deep);

        root.Should().Be(deep);
        resolution!.Module.ModuleId.Should().Be(GenericGameModule.Id);
    }

    [Fact]
    public async Task ResolveAsync_UnknownDirectory_FallsBackToTheGenericModule()
    {
        var dir = NewTempDir("plain-game");
        File.WriteAllText(Path.Combine(dir, "Game.exe"), string.Empty);

        var installation = await CreateResolver().ResolveAsync(dir, persist: false);

        installation!.ModuleId.Should().Be(GenericGameModule.Id);
        installation.Store.Should().Be(GameStore.Manual);
        installation.SteamAppId.Should().BeNull();
        installation.DisplayName.Should().Be(new DirectoryInfo(dir).Name);

        // Nothing stable to key on: no module slug and no AppID.
        installation.GameId.Should().BeEmpty();
        installation.GameKey.Should().Be("unknown");
    }

    // ── named roots ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveModPaths_UnderProton_ResolvesRootsInsideThePrefix()
    {
        var resolver = CreateResolver();
        var installation = await resolver.ResolveAsync("1091500", persist: false);
        var module = resolver.GetModule(installation!);

        var paths = module.ResolveModPaths(installation!);
        var prefix = installation!.ProtonPrefixPath!;

        paths.GetRoot(ModRoots.Game).Should().Be(installation.InstallPath);
        paths.GetRoot(ModRoots.LocalAppData).Should().Be(
            Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local"));
        paths.GetRoot(ModRoots.Documents).Should().Be(
            Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents"));
        paths.DefaultRoot.Should().Be(ModRoots.Game);
    }

    [Fact]
    public async Task ResolveModPaths_FF7Remake_AddsTheUserConfigRootUnderDocuments()
    {
        // The FF7R analyzer routes Engine.ini into <game>/Config/, where UE4 never
        // reads it. The real destination is this root.
        var resolver = CreateResolver();
        var installation = await resolver.ResolveAsync("1462040", persist: false);
        var module = resolver.GetModule(installation!);

        var paths = module.ResolveModPaths(installation!);
        var documents = paths.GetRoot(ModRoots.Documents)!;

        paths.GetRoot(FF7RemakeGameModule.UserConfigRoot).Should().Be(
            Path.Combine(
                documents,
                FF7RemakeGameModule.UserConfigRelativeToDocuments
                    .Replace('/', Path.DirectorySeparatorChar)));

        paths.Resolve(FF7RemakeGameModule.UserConfigRoot, "Engine.ini")
            .Should().EndWith(Path.Combine("WindowsNoEditor", "Engine.ini"));
    }

    [Fact]
    public async Task ResolveModPaths_UnknownRoot_Throws()
    {
        var resolver = CreateResolver();
        var installation = await resolver.ResolveAsync("1091500", persist: false);
        var paths = resolver.GetModule(installation!).ResolveModPaths(installation!);

        paths.GetRoot("nope").Should().BeNull();
        FluentActions.Invoking(() => paths.Resolve("nope", "x.txt"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task GetModule_ReturnsTheModuleNamedByTheInstallation()
    {
        var resolver = CreateResolver();
        var installation = await resolver.ResolveAsync("1462040", persist: false);

        resolver.GetModule(installation!).ModuleId.Should().Be("ff7remake");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string GameDir(string installDir) =>
        Path.Combine(_steamRoot, "steamapps", "common", installDir);

    private void WriteGame(
        int appId,
        string name,
        string installDir,
        string buildId,
        IEnumerable<string> files,
        IEnumerable<string> dirs,
        bool withProtonPrefix)
    {
        var manifest = Path.Combine(_steamRoot, "steamapps", $"appmanifest_{appId}.acf");
        File.WriteAllText(manifest, $$"""
            "AppState"
            {
            	"appid"		"{{appId}}"
            	"name"		"{{name}}"
            	"installdir"		"{{installDir}}"
            	"buildid"		"{{buildId}}"
            	"SizeOnDisk"		"1024"
            	"StateFlags"		"4"
            }
            """);

        var gameDir = GameDir(installDir);
        Directory.CreateDirectory(gameDir);

        foreach (var dir in dirs)
            Directory.CreateDirectory(Path.Combine(gameDir, dir.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var file in files)
        {
            var full = Path.Combine(gameDir, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, string.Empty);
        }

        if (withProtonPrefix)
        {
            Directory.CreateDirectory(Path.Combine(
                _steamRoot, "steamapps", "compatdata", appId.ToString(), "pfx",
                "drive_c", "users", "steamuser", "Documents"));
        }
    }

    private string NewTempDir(string label)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), $"modular-{label}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
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

    private sealed class FakeSteamLocator(string root) : ISteamLocator
    {
        public string? FindSteamRoot() => root;
    }
}
