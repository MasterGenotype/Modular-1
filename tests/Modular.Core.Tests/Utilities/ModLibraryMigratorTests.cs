using FluentAssertions;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Installers;
using Modular.Core.Services;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests.Utilities;

/// <summary>
/// Moving downloads from the old layouts into <c>&lt;backend&gt;/&lt;game&gt;/&lt;mod&gt;</c>,
/// and keeping installed mods pointed at their archives as they move.
/// </summary>
public sealed class ModLibraryMigratorTests : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, string> DefaultFlat =
        ModLibraryMigrator.LegacyFlatFolders(null);

    private readonly string _root = Directory.CreateTempSubdirectory("modular-migrate").FullName;
    private readonly List<(string From, string To)> _moves = [];
    private ModularDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new ModularDatabase(Path.Combine(Directory.CreateTempSubdirectory("modular-migrate-db").FullName, "modular.db"));
        await _database.InitializeAsync();
    }

    // ── old NexusMods layout ────────────────────────────────────────────────

    [Fact]
    public async Task NexusGameFoldersInTheRoot_MoveUnderNexusmods()
    {
        WriteFile("cyberpunk2077", "Gameplay", "Thing", "thing.zip");
        WriteFile("baldursgate3", "loose-11456-1-4-0-0-1725753424.zip");
        WriteFile(".archives", "1086940", "kept.zip");

        var result = await Migrator().MigrateAsync(_root, DefaultFlat);

        result.Moved.Should().Be(2);
        File.Exists(At("nexusmods", "cyberpunk2077", "Gameplay", "Thing", "thing.zip")).Should().BeTrue();
        File.Exists(At("nexusmods", "baldursgate3", "loose-11456-1-4-0-0-1725753424.zip")).Should().BeTrue();
        Directory.Exists(At("cyberpunk2077")).Should().BeFalse();

        // Dot-folders are Modular's own.
        File.Exists(At(".archives", "1086940", "kept.zip")).Should().BeTrue();
        _moves.Should().Contain((At("cyberpunk2077"), At("nexusmods", "cyberpunk2077")));
    }

    [Fact]
    public async Task ANexusGameAlreadyUnderNexusmods_IsMergedInto()
    {
        WriteFile("nexusmods", "cyberpunk2077", "Gameplay", "New", "new.zip");
        WriteFile("cyberpunk2077", "Gameplay", "Old", "old.zip");

        await Migrator().MigrateAsync(_root, DefaultFlat);

        File.Exists(At("nexusmods", "cyberpunk2077", "Gameplay", "New", "new.zip")).Should().BeTrue();
        File.Exists(At("nexusmods", "cyberpunk2077", "Gameplay", "Old", "old.zip")).Should().BeTrue();
    }

    // ── old flat GameBanana / mod.io layout ─────────────────────────────────

    [Fact]
    public async Task FlatModFolders_MoveIntoTheirGamesFolder()
    {
        WriteFile("gamebanana", "Better Link House", "better_link_house.zip");
        WriteFile("modio", "Aasimar Race Mod", "aasimar.zip");

        var games = new FakeLookup
        {
            ["gamebanana/Better Link House"] = "The Legend of Zelda: Tears of the Kingdom",
            ["modio/Aasimar Race Mod"] = "Baldur's Gate 3"
        };

        var result = await Migrator(games).MigrateAsync(_root, DefaultFlat);

        result.Moved.Should().Be(2);
        result.Unresolved.Should().BeEmpty();
        File.Exists(At("gamebanana", "thelegendofzeldatearsofthekingdom", "Better Link House", "better_link_house.zip"))
            .Should().BeTrue();
        File.Exists(At("modio", "baldursgate3", "Aasimar Race Mod", "aasimar.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task AFlatModWhoseGameIsUnknown_IsLeftWhereItIs()
    {
        WriteFile("gamebanana", "Mystery", "mystery.zip");

        var result = await Migrator(new FakeLookup()).MigrateAsync(_root, DefaultFlat);

        result.Moved.Should().Be(0);
        result.Unresolved.Should().Equal("gamebanana/Mystery");
        File.Exists(At("gamebanana", "Mystery", "mystery.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task TheGameNamedByADownloadRecord_IsPassedToTheLookup()
    {
        var archive = WriteFile("modio", "Hug", "hug.zip");
        DependencyConventions.WriteDownloadRecord(archive, new DeclaredSource(BackendIds.ModIo, "42", "6715", "1"));

        var games = new FakeLookup { ["modio/Hug|6715"] = "Baldur's Gate 3" };

        await Migrator(games).MigrateAsync(_root, DefaultFlat);

        File.Exists(At("modio", "baldursgate3", "Hug", "hug.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task GameFoldersBesideFlatMods_AreLeftAlone()
    {
        WriteFile("modio", "baldursgate3", "Hug", "hug.zip");

        var games = new FakeLookup();
        var result = await Migrator(games).MigrateAsync(_root, DefaultFlat);

        result.Moved.Should().Be(0);
        games.Asked.Should().BeEmpty();
        File.Exists(At("modio", "baldursgate3", "Hug", "hug.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task AModFolderNamedLikeItsGame_StillMovesIntoTheGameFolder()
    {
        // modio/baldursgate3/ holding an archive is a mod; it moves to modio/baldursgate3/baldursgate3/.
        WriteFile("modio", "baldursgate3", "a.zip");

        await Migrator(new FakeLookup { ["modio/baldursgate3"] = "Baldur's Gate 3" })
            .MigrateAsync(_root, DefaultFlat);

        File.Exists(At("modio", "baldursgate3", "baldursgate3", "a.zip")).Should().BeTrue();
        _moves.Should().ContainSingle().Which.Should().Be(
            (At("modio", "baldursgate3"), At("modio", "baldursgate3", "baldursgate3")));
    }

    [Fact]
    public async Task AModWithASlashInItsName_IsGatheredUnderItsWholeName()
    {
        // Saved before slashes were escaped: "Body type 3/4 FleshForge patch" became nested
        // folders. Its name is saved with the slash as an underscore now.
        WriteFile("modio", "Muscular Dragonborn Body type 3", "4 FleshForge patch", "flesh.zip");
        WriteFile("modio", "Muscular Dragonborn Body type 3", "4 Glow ink patch", "glow.zip");
        WriteFile("modio", "Skirbie's Comfy Clothie ", " Underwear", "under.zip");

        var games = new FakeLookup
        {
            ["modio/Muscular Dragonborn Body type 3_4 FleshForge patch"] = "Baldur's Gate 3",
            ["modio/Muscular Dragonborn Body type 3_4 Glow ink patch"] = "Baldur's Gate 3",
            ["modio/Skirbie's Comfy Clothie _ Underwear"] = "Baldur's Gate 3"
        };

        var result = await Migrator(games).MigrateAsync(_root, DefaultFlat);

        result.Moved.Should().Be(3);
        File.Exists(At("modio", "baldursgate3", "Muscular Dragonborn Body type 3_4 FleshForge patch", "flesh.zip")).Should().BeTrue();
        File.Exists(At("modio", "baldursgate3", "Muscular Dragonborn Body type 3_4 Glow ink patch", "glow.zip")).Should().BeTrue();
        File.Exists(At("modio", "baldursgate3", "Skirbie's Comfy Clothie _ Underwear", "under.zip")).Should().BeTrue();

        Directory.Exists(At("modio", "Muscular Dragonborn Body type 3")).Should().BeFalse();
        Directory.Exists(At("modio", "Skirbie's Comfy Clothie ")).Should().BeFalse();
        ModLibraryMigrator.NeedsMigration(_root, DefaultFlat).Should().BeFalse();
    }

    [Fact]
    public async Task AFlatFolderUnderACustomName_IsEmptiedAndRemoved()
    {
        WriteFile("gb", "Better Link House", "a.zip");
        var settings = new AppSettings { GameBananaDownloadDir = "gb" };
        var flat = ModLibraryMigrator.LegacyFlatFolders(settings);

        await Migrator(new FakeLookup { ["gamebanana/Better Link House"] = "Tears of the Kingdom" })
            .MigrateAsync(_root, flat);

        File.Exists(At("gamebanana", "tearsofthekingdom", "Better Link House", "a.zip")).Should().BeTrue();
        Directory.Exists(At("gb")).Should().BeFalse();
    }

    // ── when there is something to do ───────────────────────────────────────

    [Fact]
    public async Task NeedsMigration_UntilEverythingHasMoved()
    {
        ModLibraryMigrator.NeedsMigration(_root, DefaultFlat).Should().BeFalse();

        WriteFile("cyberpunk2077", "Thing", "a.zip");
        WriteFile("gamebanana", "Mod", "b.zip");
        ModLibraryMigrator.NeedsMigration(_root, DefaultFlat).Should().BeTrue();

        await Migrator(new FakeLookup { ["gamebanana/Mod"] = "Game" }).MigrateAsync(_root, DefaultFlat);

        ModLibraryMigrator.NeedsMigration(_root, DefaultFlat).Should().BeFalse();
        (await Migrator().MigrateAsync(_root, DefaultFlat)).Moved.Should().Be(0);
    }

    // ── installed mods follow their archives ────────────────────────────────

    [Fact]
    public async Task InstalledMods_ArePointedAtTheMovedArchive()
    {
        var archive = WriteFile("cyberpunk2077", "Thing_1", "thing.zip");
        var sibling = WriteFile("cyberpunk2077x", "thing.zip");
        var outside = Path.Combine("/elsewhere", "cyberpunk2077", "thing.zip");

        var changesets = new ChangesetManager(_database);
        var moved = await changesets.CreateChangesetAsync("thing", archive);
        var untouched = await changesets.CreateChangesetAsync("sibling", sibling);
        var away = await changesets.CreateChangesetAsync("outside", outside);

        await new ModLibraryMigrator(folderMoved: changesets.RelocateArchiveFolderAsync)
            .MigrateAsync(_root, DefaultFlat);

        (await changesets.GetChangesetAsync(moved))!.ArchivePath
            .Should().Be(At("nexusmods", "cyberpunk2077", "Thing_1", "thing.zip"));

        // cyberpunk2077x is a game folder of its own, moved separately; outside is never moved.
        (await changesets.GetChangesetAsync(untouched))!.ArchivePath
            .Should().Be(At("nexusmods", "cyberpunk2077x", "thing.zip"));
        (await changesets.GetChangesetAsync(away))!.ArchivePath.Should().Be(outside);
    }

    [Fact]
    public async Task RelocateArchiveFolder_OnlyMatchesTheWholeFolderName()
    {
        // "_" and "%" are LIKE wildcards; "mods/a" must not match "mods/ab".
        var changesets = new ChangesetManager(_database);
        var inFolder = await changesets.CreateChangesetAsync("a", "/mods/a_b/x.zip");
        var lookalike = await changesets.CreateChangesetAsync("b", "/mods/aXb/x.zip");
        var longer = await changesets.CreateChangesetAsync("c", "/mods/a_bc/x.zip");

        await changesets.RelocateArchiveFolderAsync("/mods/a_b", "/mods/nexusmods/a_b");

        (await changesets.GetChangesetAsync(inFolder))!.ArchivePath.Should().Be("/mods/nexusmods/a_b/x.zip");
        (await changesets.GetChangesetAsync(lookalike))!.ArchivePath.Should().Be("/mods/aXb/x.zip");
        (await changesets.GetChangesetAsync(longer))!.ArchivePath.Should().Be("/mods/a_bc/x.zip");
    }

    // ── finding a flat mod's game from the backends ─────────────────────────

    [Fact]
    public async Task BackendLookup_MatchesTheFolderToASubscriptionByName()
    {
        var backend = new SubscriptionsBackend(BackendIds.GameBanana)
        {
            Mods =
            {
                new BackendMod { ModId = "1", Name = "Better: Link House", GameDomain = "Tears of the Kingdom" },
                new BackendMod { ModId = "2", Name = "Other", GameDomain = "Other Game" }
            }
        };

        var lookup = new BackendLibraryGameLookup([backend], new AppSettings());

        (await lookup.FindGameAsync(BackendIds.GameBanana, "Better_ Link House", null, default))
            .Should().Be("Tears of the Kingdom");
        (await lookup.FindGameAsync(BackendIds.GameBanana, "Unsubscribed", null, default)).Should().BeNull();
        backend.Calls.Should().Be(1);
    }

    [Fact]
    public async Task BackendLookup_FallsBackToTheOnlyConfiguredModIoGame()
    {
        var lookup = new BackendLibraryGameLookup([], new AppSettings { ModIoGameIds = [6715] });

        // Without the mod.io backend to name it, the game stays a number.
        (await lookup.FindGameAsync(BackendIds.ModIo, "Unsubscribed", null, default)).Should().Be("6715");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ModLibraryMigrator Migrator(ILibraryGameLookup? games = null) =>
        new(games, (from, to, _) =>
        {
            _moves.Add((from, to));
            return Task.CompletedTask;
        });

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string WriteFile(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    // "<backend>/<mod folder>" or "<backend>/<mod folder>|<recorded game>" → game.
    private sealed class FakeLookup : Dictionary<string, string>, ILibraryGameLookup
    {
        public List<string> Asked { get; } = [];

        public Task<string?> FindGameAsync(string backendId, string modFolder, string? recordedGame, CancellationToken ct)
        {
            var key = recordedGame == null ? $"{backendId}/{modFolder}" : $"{backendId}/{modFolder}|{recordedGame}";
            Asked.Add(key);
            return Task.FromResult(TryGetValue(key, out var game) ? game : null);
        }
    }

    private sealed class SubscriptionsBackend(string id) : IModBackend
    {
        public List<BackendMod> Mods { get; } = [];
        public int Calls { get; private set; }

        public string Id => id;
        public string DisplayName => id;
        public BackendCapabilities Capabilities => BackendCapabilities.None;

        public IReadOnlyList<string> ValidateConfiguration() => [];

        public Task<List<BackendMod>> GetUserModsAsync(string? gameDomain = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Mods.ToList());
        }

        public Task<List<BackendModFile>> GetModFilesAsync(
            string modId, string? gameDomain = null, FileFilter? filter = null, CancellationToken ct = default) =>
            Task.FromResult(new List<BackendModFile>());

        public Task<string?> ResolveDownloadUrlAsync(
            string modId, string fileId, string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<BackendMod?> GetModInfoAsync(string modId, string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<BackendMod?>(null);

        public Task DownloadModsAsync(
            string outputDirectory,
            string? gameDomain = null,
            DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
