using FluentAssertions;
using Modular.Core.Backends.ModIo;
using Modular.Core.Configuration;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Services;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests.Utilities;

/// <summary>
/// Sorting GameBanana and mod.io game folders into <c>&lt;game&gt;/&lt;category&gt;/&lt;mod&gt;/</c>,
/// and the category each backend gives a mod.
/// </summary>
public sealed class ModLibrarySorterTests : IDisposable
{
    private readonly string _game = Path.Combine(
        Directory.CreateTempSubdirectory("modular-sort").FullName, "modio", "baldursgate3");
    private readonly List<(string From, string To)> _moves = [];

    [Fact]
    public async Task Mods_MoveIntoTheirCategoryFolder()
    {
        WriteFile("Arcane Charmer", "charmer.zip");
        WriteFile("Dominatrix Class", "class.zip");

        var categories = new FakeCategories
        {
            ["Arcane Charmer"] = "Customisation",
            ["Dominatrix Class"] = "Classes"
        };

        var result = await Sorter(categories).SortAsync(BackendIds.ModIo, _game, byCategory: true);

        result.Moved.Should().Be(2);
        File.Exists(At("Customisation", "Arcane Charmer", "charmer.zip")).Should().BeTrue();
        File.Exists(At("Classes", "Dominatrix Class", "class.zip")).Should().BeTrue();
        _moves.Should().Contain((At("Arcane Charmer"), At("Customisation", "Arcane Charmer")));
    }

    [Fact]
    public async Task AModWhoseCategoryChanged_MovesToTheNewOne_AndTheEmptiedFolderGoes()
    {
        WriteFile("Old", "Hug", "hug.zip");

        await Sorter(new FakeCategories { ["Hug"] = "Characters" }).SortAsync(BackendIds.ModIo, _game, byCategory: true);

        File.Exists(At("Characters", "Hug", "hug.zip")).Should().BeTrue();
        Directory.Exists(At("Old")).Should().BeFalse();
    }

    [Fact]
    public async Task AModAlreadyInItsCategory_StaysPut()
    {
        WriteFile("Characters", "Hug", "hug.zip");

        var result = await Sorter(new FakeCategories { ["Hug"] = "Characters" })
            .SortAsync(BackendIds.ModIo, _game, byCategory: true);

        result.Moved.Should().Be(0);
        _moves.Should().BeEmpty();
    }

    [Fact]
    public async Task AModWhoseCategoryIsUnknown_IsLeftWhereItIs()
    {
        // Including one sorted earlier: a run that cannot reach the site never undoes a sort.
        WriteFile("Mystery", "m.zip");
        WriteFile("Characters", "Hug", "hug.zip");

        var result = await Sorter(new FakeCategories()).SortAsync(BackendIds.ModIo, _game, byCategory: true);

        result.Uncategorized.Should().BeEquivalentTo("Mystery", "Hug");
        File.Exists(At("Mystery", "m.zip")).Should().BeTrue();
        File.Exists(At("Characters", "Hug", "hug.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task WithCategoriesOff_ModsComeOutOfCategoryFolders()
    {
        WriteFile("Characters", "Hug", "hug.zip");
        WriteFile("Loose", "l.zip");

        var result = await Sorter(null).SortAsync(BackendIds.ModIo, _game, byCategory: false);

        result.Moved.Should().Be(1);
        File.Exists(At("Hug", "hug.zip")).Should().BeTrue();
        File.Exists(At("Loose", "l.zip")).Should().BeTrue();
        Directory.Exists(At("Characters")).Should().BeFalse();
    }

    [Fact]
    public async Task TheModIdInADownloadRecord_IsPassedToTheLookup()
    {
        var archive = WriteFile("Renamed By Hand", "a.zip");
        DependencyConventions.WriteDownloadRecord(archive, new DeclaredSource(BackendIds.ModIo, "42", "6715", "1"));

        var categories = new FakeCategories { ["#42"] = "Races" };

        await Sorter(categories).SortAsync(BackendIds.ModIo, _game, byCategory: true);

        File.Exists(At("Races", "Renamed By Hand", "a.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task ACategoryNamedLikeAMod_IsNotMergedIntoTheMod()
    {
        WriteFile("UI", "ui-mod.zip");
        WriteFile("Better Menus", "menus.zip");

        var result = await Sorter(new FakeCategories { ["Better Menus"] = "UI" })
            .SortAsync(BackendIds.GameBanana, _game, byCategory: true);

        result.Errors.Should().ContainSingle();
        File.Exists(At("Better Menus", "menus.zip")).Should().BeTrue();
        File.Exists(At("UI", "ui-mod.zip")).Should().BeTrue();
    }

    // ── after a download ────────────────────────────────────────────────────

    [Fact]
    public async Task ACategoryFromTheDownloadsListing_IsUsedBeforeSubscriptions()
    {
        // A mod found by search is not among the subscriptions; its listing said "Races".
        WriteFile("Searched Mod", "s.zip");
        WriteFile("Subscribed Mod", "t.zip");

        var lookup = new KnownCategoryLookup(
            new Dictionary<string, string> { ["Searched Mod"] = "Races" },
            new FakeCategories { ["Subscribed Mod"] = "Classes", ["Searched Mod"] = "Wrong" });

        await Sorter(lookup).SortAsync(BackendIds.ModIo, _game, byCategory: true);

        File.Exists(At("Races", "Searched Mod", "s.zip")).Should().BeTrue();
        File.Exists(At("Classes", "Subscribed Mod", "t.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task SortBackend_SortsEveryGameOfThatBackendOnly()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(_game))!;
        WriteFile("Hug", "hug.zip");
        var other = Path.Combine(root, "gamebanana", "tearsofthekingdom", "Better Link House", "a.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "x");

        var backend = new SubscriptionsBackend(BackendIds.ModIo)
        {
            Mods = { new BackendMod { ModId = "1", Name = "Hug", CategoryName = "Characters" } }
        };

        var moved = await ModLibrarySorter.SortBackendAsync(
            backend, new AppSettings { OrganizeByCategory = true }, root,
            (from, to, _) =>
            {
                _moves.Add((from, to));
                return Task.CompletedTask;
            });

        moved.Should().Be(1);
        File.Exists(At("Characters", "Hug", "hug.zip")).Should().BeTrue();
        File.Exists(other).Should().BeTrue();
        _moves.Should().ContainSingle();
    }

    // ── categories from the backends ────────────────────────────────────────

    [Theory]
    [InlineData(new[] { "Customisation", "Patch 8 Tested", "Wizard" }, "Customisation")]
    [InlineData(new[] { "Subclasses", "Classes", "English" }, "Classes")]   // the game's order wins
    [InlineData(new[] { "English", "For Fun" }, null)]
    public void ModIo_TheCategoryIsTheFirstTagOfTheGamesFirstGroup(string[] modTags, string? expected)
    {
        string[] type = ["Cheats", "Classes", "Customisation", "Races", "Subclasses"];

        ModIoBackend.PickCategory(modTags, type).Should().Be(expected);
    }

    [Fact]
    public void GameBanana_ListingRecordsCarryTheRootCategory()
    {
        // Trimmed from a Member/{id}/Subscriptions response.
        const string json = """
            {"_aSubscription": {"_idRow": 1, "_sName": "Better Link House",
              "_aRootCategory": {"_sName": "Maps", "_sProfileUrl": "https://gamebanana.com/mods/cats/1"}}}
            """;

        var record = System.Text.Json.JsonSerializer.Deserialize<Modular.Core.Backends.GameBanana.GameBananaV11Record>(json);

        record!.Subscription!.RootCategory!.Name.Should().Be("Maps");
    }

    [Fact]
    public async Task Lookup_FindsTheCategoryByModIdOrFolderName()
    {
        var backend = new SubscriptionsBackend(BackendIds.GameBanana)
        {
            Mods =
            {
                new BackendMod { ModId = "1", Name = "Better: Link House", CategoryName = "Maps" },
                new BackendMod { ModId = "2", Name = "Other", CategoryName = "Skins" }
            }
        };

        var lookup = new BackendLibraryGameLookup([backend], new AppSettings());

        (await lookup.FindCategoryAsync(BackendIds.GameBanana, "Better_ Link House", null, default)).Should().Be("Maps");
        (await lookup.FindCategoryAsync(BackendIds.GameBanana, "Renamed", "2", default)).Should().Be("Skins");
        (await lookup.FindCategoryAsync(BackendIds.GameBanana, "Unknown", null, default)).Should().BeNull();
        backend.Calls.Should().Be(1);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ModLibrarySorter Sorter(ILibraryCategoryLookup? categories) =>
        new(categories, (from, to, _) =>
        {
            _moves.Add((from, to));
            return Task.CompletedTask;
        });

    private string At(params string[] parts) => Path.Combine([_game, .. parts]);

    private string WriteFile(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_game))!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Mod folder name → category; "#<id>" keys answer by recorded mod id.
    private sealed class FakeCategories : Dictionary<string, string>, ILibraryCategoryLookup
    {
        public Task<string?> FindCategoryAsync(string backendId, string modFolder, string? recordedModId, CancellationToken ct)
        {
            if (recordedModId != null && TryGetValue("#" + recordedModId, out var byId))
                return Task.FromResult<string?>(byId);

            return Task.FromResult(TryGetValue(modFolder, out var category) ? category : null);
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
