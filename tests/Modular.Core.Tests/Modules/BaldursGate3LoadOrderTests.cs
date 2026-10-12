using System.Xml.Linq;
using FluentAssertions;
using Modular.Core.Installers.BaldursGate3;
using Modular.Core.Modules;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// Baldur's Gate 3: load-order bands from metadata, the <c>modsettings.lsx</c> writer,
/// Script Extender, and archives that offer a pak in several versions.
/// </summary>
public sealed class BaldursGate3LoadOrderTests : IDisposable
{
    private const string A = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string B = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string C = "cccccccc-0000-0000-0000-000000000003";
    private const string D = "dddddddd-0000-0000-0000-000000000004";

    private readonly List<string> _tempDirs = new();

    // ── bands ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Races", 30)]
    [InlineData("Classes", 40)]
    [InlineData("Spells", 40)]
    [InlineData("User Interface", 20)]
    [InlineData("Utilities", 10)]
    [InlineData("Armor", 60)]
    [InlineData("Character Customisation", 70)]
    [InlineData("character customisation", 70)]
    [InlineData("Gameplay", 50)]
    public void Classifier_ANexusCategory_DecidesTheBand(string category, int rank)
    {
        // Contents that would say "race" lose to what the author filed the mod under.
        var profile = Profile("Public/X/Races/Races.lsx");

        BG3LoadOrderClassifier.Classify(profile, category).Rank.Should().Be(rank);
    }

    [Theory]
    [InlineData(30, "Public/X/Races/Races.lsx", "Public/X/CharacterCreation/Visuals.lsx")]
    [InlineData(40, "Public/X/ClassDescriptions/ClassDescriptions.lsx", "Public/X/Stats/Generated/Data/Armor.txt")]
    [InlineData(70, "Public/X/CharacterCreation/CharacterCreationAppearanceVisuals.lsx")]
    [InlineData(60, "Public/X/Stats/Generated/Data/Armor.txt", "Public/X/Content/Assets/a.lsf")]
    [InlineData(60, "Public/X/Stats/Generated/TreasureTable.txt")]
    [InlineData(40, "Public/X/Stats/Generated/Data/Spell_Target.txt")]
    [InlineData(70, "Public/X/Content/Heads/head.lsf", "Generated/Public/X/head.GR2")]
    [InlineData(20, "Public/Game/GUI/Assets/a.dds", "Mods/X/Localization/English/x.loca")]
    [InlineData(50, "Public/X/Progressions/Progressions.lsx")]
    [InlineData(50, "Public/X/Stats/Generated/Data/Passive.txt", "Public/Game/GUI/Assets/thumb.dds")]
    public void Classifier_WithoutACategory_GoesByWhatThePakPacks(int rank, params string[] contents)
    {
        BG3LoadOrderClassifier.Classify(Profile(contents)).Rank.Should().Be(rank);
    }

    [Fact]
    public void Classifier_AnOtherwisePlainModThatDependsOnAMod_IsAPatch()
    {
        var plain = new BG3PakProfile(Meta(A, "Patch", B), ["Public/X/Progressions/Progressions.lsx"]);
        var race = new BG3PakProfile(Meta(A, "Race", B), ["Public/X/Races/Races.lsx"]);

        BG3LoadOrderClassifier.Classify(plain).Should().Be(BG3LoadOrderClassifier.Patch);

        // A race that builds on a framework is still a race.
        BG3LoadOrderClassifier.Classify(race).Should().Be(BG3LoadOrderClassifier.Race);
    }

    [Fact]
    public void Classifier_NothingKnown_IsGameplay()
    {
        BG3LoadOrderClassifier.Classify(null).Should().Be(BG3LoadOrderClassifier.Gameplay);
        BG3LoadOrderClassifier.Classify(null, "A category that does not exist")
            .Should().Be(BG3LoadOrderClassifier.Gameplay);
    }

    // ── analyzer ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Public/MyMod/Stats/Generated/Data/Armor.txt", "Data/Public/MyMod/Stats/Generated/Data/Armor.txt")]
    [InlineData("MyMod/Public/MyMod/Stats/Generated/Data/Armor.txt", "Data/Public/MyMod/Stats/Generated/Data/Armor.txt")]
    [InlineData("Data/Public/MyMod/Stats/Generated/Data/Armor.txt", "Data/Public/MyMod/Stats/Generated/Data/Armor.txt")]
    [InlineData("Game/GUI/Data/a.dds", "Data/Public/Game/GUI/Data/a.dds")]
    public void Analyzer_AFolderCalledDataInsideAModTree_IsNotTheGamesDataFolder(string entry, string expected)
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries(entry));

        layout.FileRoutes[entry].Destination.Should().Be(expected);
    }

    [Theory]
    [InlineData("DWrite.dll", "bin/DWrite.dll")]
    [InlineData("dwrite.dll", "bin/DWrite.dll")]
    [InlineData("ScriptExtenderSettings.json", "bin/ScriptExtenderSettings.json")]
    [InlineData("bin/DWrite.dll", "bin/DWrite.dll")]
    public void Analyzer_ScriptExtenderFiles_GoBesideTheExecutable(string entry, string expected)
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries(entry, "README.txt"));

        layout.IsRecognized.Should().BeTrue();
        layout.FileRoutes[entry].Destination.Should().Be(expected);
        layout.FileRoutes[entry].PackageType.Should().Be(PackageTypes.Bg3ScriptExtender);
        layout.FileRoutes.Should().NotContainKey("README.txt");
    }

    [Fact]
    public void Analyzer_TheSamePakInTwoFolders_IsAChoice_AndOnlyOneIsRouted()
    {
        var entries = Entries(
            "Mod/Option A/Mod.pak", "Mod/Option A/notes.txt",
            "Mod/Option B/Mod.pak",
            "Mod/Shared.pak");

        var layout = BG3ArchiveAnalyzer.Analyze(entries);

        var choice = layout.Choices.Should().ContainSingle().Subject;
        choice.Id.Should().Be("Mod.pak");
        choice.IsResolved.Should().BeFalse();
        choice.Options.Select(o => o.Label).Should().Equal("Option A", "Option B");
        choice.Options.Select(o => o.Id).Should().Equal("Mod/Option A", "Mod/Option B");
        choice.Options[0].FileCount.Should().Be(2);

        // The first option stands until someone chooses; the pak that is not part of the
        // choice is installed either way.
        choice.SelectedOptionId.Should().Be("Mod/Option A");
        layout.FileRoutes.Keys.Should().BeEquivalentTo("Mod/Option A/Mod.pak", "Mod/Shared.pak");
    }

    [Fact]
    public void Analyzer_AGivenSelection_RoutesThatOption()
    {
        var entries = Entries("Option A/Mod.pak", "Option B/Mod.pak", "Option B/Extra.pak");

        var layout = BG3ArchiveAnalyzer.Analyze(
            entries, new Dictionary<string, string> { ["Mod.pak"] = "option b" });

        layout.Choices.Single().IsResolved.Should().BeTrue();
        layout.Choices.Single().SelectedOptionId.Should().Be("Option B");
        layout.FileRoutes.Keys.Should().BeEquivalentTo("Option B/Mod.pak", "Option B/Extra.pak");
    }

    [Fact]
    public void Analyzer_SeveralPaksVaryingAcrossTheSameFolders_AreOneChoice()
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries(
            "Red/Body.pak", "Red/Head.pak", "Blue/Body.pak", "Blue/Head.pak"));

        var choice = layout.Choices.Should().ContainSingle().Subject;
        choice.Options.Select(o => o.Label).Should().Equal("Red", "Blue");
        layout.FileRoutes.Keys.Should().BeEquivalentTo("Red/Body.pak", "Red/Head.pak");
    }

    [Fact]
    public void Analyzer_DifferentPaksInDifferentFolders_AreNotAChoice()
    {
        var layout = BG3ArchiveAnalyzer.Analyze(Entries("Main/A.pak", "Optional/B.pak"));

        layout.Choices.Should().BeEmpty();
        layout.FileRoutes.Should().HaveCount(2);
    }

    // ── modsettings.lsx ─────────────────────────────────────────────────────

    [Fact]
    public void ModSettings_AddsModularsPaksByBand_AndLeavesEveryOtherEntryInPlace()
    {
        var path = NewModSettings(ExistingFile(("GustavX", BG3ModSettings.GustavXUuid), ("In-game B", B), ("In-game A", A)));
        var before = File.ReadAllText(path);

        var result = BG3ModSettings.Update(
            path,
            [Meta(A, "In-game A"), Meta(B, "In-game B"), Meta(C, "Appearance"), Meta(D, "Race")],
            [
                // Installed first, but belongs in a later band.
                new BG3ManagedPak(Meta(C, "Appearance").Module, BG3LoadOrderClassifier.Appearance, 0),
                new BG3ManagedPak(Meta(D, "Race").Module, BG3LoadOrderClassifier.Race, 1)
            ]);

        result.Written.Should().BeTrue();
        result.Added.Should().Equal("Race", "Appearance");
        result.Order.Should().Equal("GustavX", "In-game B", "In-game A", "Race", "Appearance");

        // The entries that were already there come back byte for byte, hash and all.
        var written = XDocument.Parse(File.ReadAllText(path));
        Entry(written, B).ToString().Should().Be(Entry(XDocument.Parse(before), B).ToString());
        Attribute(Entry(written, B), "MD5").Should().Be("existing-hash");

        // A new entry carries what the pak's meta says, in the game's attribute order.
        Entry(written, D).Elements("attribute").Select(a => (string)a.Attribute("id")!)
            .Should().Equal("Folder", "MD5", "Name", "PublishHandle", "UUID", "Version64");
        Attribute(Entry(written, D), "PublishHandle").Should().Be("4321");
    }

    [Fact]
    public void ModSettings_AModInstalledLater_ThatBelongsEarlier_IsPutAheadAmongModularsOwn()
    {
        var path = NewModSettings(ExistingFile(("GustavX", BG3ModSettings.GustavXUuid), ("In-game", A)));
        var heads = new BG3ManagedPak(Meta(C, "Heads").Module, BG3LoadOrderClassifier.Appearance, 0);
        var race = new BG3ManagedPak(Meta(D, "Race").Module, BG3LoadOrderClassifier.Race, 1);

        // Two separate installs: the appearance mod first, the race it is for afterwards.
        BG3ModSettings.Update(path, [Meta(A, "In-game"), Meta(C, "Heads")], [heads]);
        var result = BG3ModSettings.Update(
            path, [Meta(A, "In-game"), Meta(C, "Heads"), Meta(D, "Race")], [heads, race]);

        result.Order.Should().Equal("GustavX", "In-game", "Race", "Heads");
    }

    [Fact]
    public void ModSettings_KeepsTheFilesOwnVersionAndBacksUpWhatItReplaces()
    {
        var path = NewModSettings(ExistingFile(("GustavX", BG3ModSettings.GustavXUuid), ("In-game A", A)));
        var before = File.ReadAllText(path);

        BG3ModSettings.Update(
            path, [Meta(A, "In-game A"), Meta(B, "Mine")],
            [new BG3ManagedPak(Meta(B, "Mine").Module, BG3LoadOrderClassifier.Gameplay, 0)]);

        var written = File.ReadAllText(path);
        written.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        written.Should().Contain("build=\"700\"");

        File.ReadAllText(path + ".modular.bak").Should().Be(before);
        File.ReadAllText(path + ".modular.orig").Should().Be(before);
    }

    [Fact]
    public void ModSettings_ANewModSomethingListedDependsOn_GoesInAboveIt_SoNothingListedMoves()
    {
        // "Needs C" is already in the list; C is only now being installed.
        var path = NewModSettings(ExistingFile(
            ("GustavX", BG3ModSettings.GustavXUuid), ("Needs C", A), ("Bystander", B)));

        var result = BG3ModSettings.Update(
            path,
            [Meta(A, "Needs C", C), Meta(B, "Bystander"), Meta(C, "Library")],
            [new BG3ManagedPak(Meta(C, "Library").Module, BG3LoadOrderClassifier.Framework, 0)]);

        result.Moved.Should().BeEmpty();
        result.Order.Should().Equal("GustavX", "Library", "Needs C", "Bystander");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void ModSettings_AListedModAboveAListedDependency_IsMovedBelowIt_AndNothingElseMoves()
    {
        // The list as found is already wrong: "Needs C" loads before C.
        var path = NewModSettings(ExistingFile(
            ("GustavX", BG3ModSettings.GustavXUuid), ("Needs C", A), ("Bystander", B), ("Library", C), ("Last", D)));

        var result = BG3ModSettings.Update(
            path,
            [Meta(A, "Needs C", C), Meta(B, "Bystander"), Meta(C, "Library"), Meta(D, "Last")],
            [new BG3ManagedPak(Meta(D, "Last").Module, BG3LoadOrderClassifier.Gameplay, 0)]);

        result.Moved.Should().Equal("Needs C");
        result.Order.Should().Equal("GustavX", "Bystander", "Library", "Needs C", "Last");
    }

    [Fact]
    public void ModSettings_AnOrderThatIsAlreadyRight_IsNotRewritten()
    {
        var path = NewModSettings(ExistingFile(
            ("GustavX", BG3ModSettings.GustavXUuid), ("Library", C), ("Needs C", A)));
        var stamp = File.GetLastWriteTimeUtc(path);

        var result = BG3ModSettings.Update(
            path,
            [Meta(A, "Needs C", C), Meta(C, "Library")],
            [new BG3ManagedPak(Meta(C, "Library").Module, BG3LoadOrderClassifier.Framework, 0)],
            mayWrite: () => throw new InvalidOperationException("nothing should need writing"));

        result.Written.Should().BeFalse();
        result.Moved.Should().BeEmpty();
        File.GetLastWriteTimeUtc(path).Should().Be(stamp);
        File.Exists(path + ".modular.bak").Should().BeFalse();
    }

    [Fact]
    public void ModSettings_UninstallingAMod_RemovesItsEntry_AndOnlyEntriesModularAdded()
    {
        var path = NewModSettings(ExistingFile(("GustavX", BG3ModSettings.GustavXUuid), ("In-game A", A)));
        var mine = new BG3ManagedPak(Meta(B, "Mine").Module, BG3LoadOrderClassifier.Gameplay, 0);

        BG3ModSettings.Update(path, [Meta(A, "In-game A"), Meta(B, "Mine")], [mine]);

        // Mine is uninstalled. In-game A's pak has gone missing too, but Modular never
        // added it, so its entry is not Modular's to remove.
        var result = BG3ModSettings.Update(path, [], []);

        result.Removed.Should().Equal("Mine");
        result.Order.Should().Equal("GustavX", "In-game A");
    }

    [Fact]
    public void ModSettings_AModAlreadyListedByTheGame_IsNotDuplicatedOrMoved()
    {
        var path = NewModSettings(ExistingFile(
            ("GustavX", BG3ModSettings.GustavXUuid), ("Shared", B), ("In-game A", A)));

        var result = BG3ModSettings.Update(
            path, [Meta(A, "In-game A"), Meta(B, "Shared")],
            [new BG3ManagedPak(Meta(B, "Shared").Module, BG3LoadOrderClassifier.Patch, 0)]);

        result.Written.Should().BeFalse();
        result.Order.Should().Equal("GustavX", "Shared", "In-game A");
    }

    [Fact]
    public void ModSettings_NoFileYet_CreatesOneLedByTheBaseCampaign()
    {
        var path = Path.Combine(NewTempDir(), "PlayerProfiles", "Public", BG3ModSettings.FileName);

        var result = BG3ModSettings.Update(
            path, [Meta(A, "Mine")],
            [new BG3ManagedPak(Meta(A, "Mine").Module, BG3LoadOrderClassifier.Gameplay, 0)]);

        result.Written.Should().BeTrue();
        result.Order.Should().Equal("GustavX", "Mine");
        BG3ModSettings.ReadOrder(path).Should().Equal("GustavX", "Mine");
    }

    [Fact]
    public void ModSettings_AFileItCannotUnderstand_IsLeftUntouched()
    {
        var path = NewModSettings("<save><region id=\"Something else\"/></save>");
        var before = File.ReadAllText(path);

        var act = () => BG3ModSettings.Update(
            path, [Meta(A, "Mine")],
            [new BG3ManagedPak(Meta(A, "Mine").Module, BG3LoadOrderClassifier.Gameplay, 0)]);

        act.Should().Throw<InvalidDataException>();
        File.ReadAllText(path).Should().Be(before);
    }

    [Fact]
    public void ModSettings_AWriteThatIsRefused_ChangesNothing()
    {
        var path = NewModSettings(ExistingFile(("GustavX", BG3ModSettings.GustavXUuid)));
        var before = File.ReadAllText(path);

        var act = () => BG3ModSettings.Update(
            path, [Meta(A, "Mine")],
            [new BG3ManagedPak(Meta(A, "Mine").Module, BG3LoadOrderClassifier.Gameplay, 0)],
            mayWrite: () => false);

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(path).Should().Be(before);
    }

    [Fact]
    public void ModSettings_ADependencyLoop_IsReportedAndDoesNotHang()
    {
        var path = NewModSettings(ExistingFile(("GustavX", BG3ModSettings.GustavXUuid)));

        var result = BG3ModSettings.Update(
            path,
            [Meta(A, "One", B), Meta(B, "Two", A)],
            [
                new BG3ManagedPak(Meta(A, "One", B).Module, BG3LoadOrderClassifier.Gameplay, 0),
                new BG3ManagedPak(Meta(B, "Two", A).Module, BG3LoadOrderClassifier.Gameplay, 1)
            ]);

        result.Order.Should().HaveCount(3);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("loop");
    }

    [Fact]
    public void ModSettings_AMissingDependencyOfModularsOwnMod_IsAWarning()
    {
        var path = NewModSettings(ExistingFile(("GustavX", BG3ModSettings.GustavXUuid), ("Theirs", C)));

        var result = BG3ModSettings.Update(
            path,
            // "Theirs" has a gap as well, which is not Modular's to report.
            [Meta(A, "Mine", B), Meta(C, "Theirs", D)],
            [new BG3ManagedPak(Meta(A, "Mine", B).Module, BG3LoadOrderClassifier.Gameplay, 0)]);

        result.Warnings.Should().ContainSingle().Which.Should().Contain("'Mine'");
    }

    // ── the module, end to end ──────────────────────────────────────────────

    [Fact]
    public async Task WriteLoadOrder_OrdersDeployedPaksByBand_AroundTheGamesOwnEntries()
    {
        var installation = Installation();
        var module = new BaldursGate3GameModule { IsGameRunning = () => false };
        var mods = ModsFolder(module, installation);
        var settingsPath = BaldursGate3GameModule.ResolveModSettingsPath(installation)!;

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, ExistingFile(("GustavX", BG3ModSettings.GustavXUuid), ("In-game", A)));

        Bg3TestPak.WriteMod(Path.Combine(mods, "ingame.pak"), A, "In-game");
        Bg3TestPak.WriteModWith(
            Path.Combine(mods, "70-000_Heads.pak"), C, "Heads", ["Public/Heads/Content/head.lsf"]);
        Bg3TestPak.WriteModWith(
            Path.Combine(mods, "30-001_Race.pak"), D, "Race", ["Public/Race/Races/Races.lsx"]);

        await module.WriteLoadOrderAsync(installation,
        [
            Deployed("heads", 0, "70-000_Heads.pak"),
            Deployed("race", 1, "30-001_Race.pak")
        ]);

        BG3ModSettings.ReadOrder(settingsPath).Should().Equal("GustavX", "In-game", "Race", "Heads");

        module.DescribeEngineOrder(installation, [])!.Value.Entries
            .Should().Equal("GustavX", "In-game", "Race", "Heads");
    }

    [Fact]
    public async Task WriteLoadOrder_WhileTheGameIsRunning_RefusesAndLeavesTheFile()
    {
        var installation = Installation();
        var module = new BaldursGate3GameModule { IsGameRunning = () => true };
        var mods = ModsFolder(module, installation);
        var settingsPath = BaldursGate3GameModule.ResolveModSettingsPath(installation)!;

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, ExistingFile(("GustavX", BG3ModSettings.GustavXUuid)));
        var before = File.ReadAllText(settingsPath);

        Bg3TestPak.WriteMod(Path.Combine(mods, "50-000_Mine.pak"), A, "Mine");

        var act = () => module.WriteLoadOrderAsync(installation, [Deployed("mine", 0, "50-000_Mine.pak")]);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*is running*");
        File.ReadAllText(settingsPath).Should().Be(before);
    }

    [Fact]
    public async Task WriteLoadOrder_WithNothingOfModulars_DoesNotTouchTheGamesFile()
    {
        var installation = Installation();
        var module = new BaldursGate3GameModule { IsGameRunning = () => true };
        var settingsPath = BaldursGate3GameModule.ResolveModSettingsPath(installation)!;

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "not even xml");

        // Loose files only: no pak, so no business with the load order at all.
        await module.WriteLoadOrderAsync(installation,
        [
            new DeployedMod
            {
                ModId = "screens",
                DeployedFiles = [new StagedFile(ModRoots.Game, "Data/Public/Game/GUI/a.dds")]
            }
        ]);

        File.ReadAllText(settingsPath).Should().Be("not even xml");
    }

    // ── Script Extender ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateStaged_AModWithScriptExtenderScripts_IsRefusedUntilItIsInstalled()
    {
        var installation = Installation(scriptExtender: false);
        var module = new BaldursGate3GameModule();
        var staged = Staged(("bg3mods", "Mod.pak", ["Mods/Mod/ScriptExtender/Lua/BootstrapServer.lua"]));

        var diagnostics = module.ValidateStaged(installation, staged);

        var error = diagnostics.Should().ContainSingle().Subject;
        error.Code.Should().Be("bg3.script_extender_required");
        error.Severity.Should().Be(DiagnosticSeverity.Error);
        error.Message.Should().Contain(BaldursGate3GameModule.ScriptExtenderUrl);

        // With the loader in place, under whatever casing, the same mod is fine.
        File.WriteAllText(Path.Combine(installation.InstallPath, "bin", "dwrite.dll"), string.Empty);
        module.ValidateStaged(installation, staged).Should().BeEmpty();
    }

    [Fact]
    public void ValidateStaged_AnyModWithoutScriptExtender_IsFlaggedButNotRefused()
    {
        var installation = Installation(scriptExtender: false);
        var staged = Staged(("bg3mods", "Mod.pak", ["Public/Mod/Progressions/Progressions.lsx"]));

        var diagnostics = new BaldursGate3GameModule().ValidateStaged(installation, staged);

        var warning = diagnostics.Should().ContainSingle().Subject;
        warning.Code.Should().Be("bg3.script_extender_missing");
        warning.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [Fact]
    public void ValidateStaged_TheScriptExtenderArchiveItself_IsNotFlagged()
    {
        var installation = Installation(scriptExtender: false);
        var staged = new StagedMod
        {
            ModId = "bg3se",
            StagingPath = NewTempDir(),
            Files = [new StagedFile(ModRoots.Game, "bin/DWrite.dll", PackageTypes.Bg3ScriptExtender)]
        };

        new BaldursGate3GameModule().ValidateStaged(installation, staged).Should().BeEmpty();
    }

    [Fact]
    public void LaunchRequirements_ScriptExtenderUnderProton_NeedsTheDllOverride()
    {
        var module = new BaldursGate3GameModule();

        module.GetLaunchRequirements(Installation(), []).WineDllOverrides.Should().Equal("dwrite");
        module.GetLaunchRequirements(Installation(scriptExtender: false), []).IsEmpty.Should().BeTrue();

        // Deployed by Modular in this run, so not on disk before but needed all the same.
        module.GetLaunchRequirements(
                Installation(scriptExtender: false),
                [new DeployedMod { ModId = "bg3se", DeployedFiles = [new StagedFile(ModRoots.Game, "bin/DWrite.dll")] }])
            .WineDllOverrides.Should().Equal("dwrite");
    }

    [Fact]
    public void ValidateStaged_ShadowingIsFoundWhateverTheCaseOnDisk()
    {
        var installation = Installation();
        Directory.CreateDirectory(Path.Combine(installation.InstallPath, "Data", "public", "game", "gui"));
        File.WriteAllText(Path.Combine(installation.InstallPath, "Data", "public", "game", "gui", "A.DDS"), "x");

        var diagnostics = new BaldursGate3GameModule().ValidateStaged(
            installation, Staged(("bg3mods", "Screens.pak", ["Public/Game/GUI/a.dds"])));

        diagnostics.Should().ContainSingle().Which.Code.Should().Be("bg3.pak_shadowed_by_loose_file");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static BG3PakProfile Profile(params string[] contents) => new(null, contents);

    private static BG3PakMeta Meta(string uuid, string name, params string[] dependsOn) => new(
        new BG3ModuleRef(uuid, name, name, 4321, "meta-hash"),
        dependsOn.Select(d => new BG3ModuleRef(d, d, d, 0)).ToList(),
        []);

    private static string ExistingFile(params (string Name, string Uuid)[] mods) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <save>
            <version major="4" minor="8" revision="0" build="700"/>
            <region id="ModuleSettings">
                <node id="root">
                    <children>
                        <node id="Mods">
                            <children>
        {string.Concat(mods.Select(m => $"""
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="{m.Name}"/>
                                    <attribute id="MD5" type="LSString" value="existing-hash"/>
                                    <attribute id="Name" type="LSString" value="{m.Name}"/>
                                    <attribute id="PublishHandle" type="uint64" value="0"/>
                                    <attribute id="UUID" type="guid" value="{m.Uuid}"/>
                                    <attribute id="Version64" type="int64" value="36028797018963968"/>
                                </node>

            """))}
                            </children>
                        </node>
                    </children>
                </node>
            </region>
        </save>
        """;

    private static XElement Entry(XDocument document, string uuid) =>
        document.Descendants("node")
            .Where(n => (string?)n.Attribute("id") == "ModuleShortDesc")
            .Single(n => Attribute(n, "UUID") == uuid);

    private static string Attribute(XElement node, string id) =>
        (string)node.Elements("attribute").Single(a => (string?)a.Attribute("id") == id).Attribute("value")!;

    private static IReadOnlyList<ArchiveEntry> Entries(params string[] paths) =>
        paths.Select(p => new ArchiveEntry
        {
            FullName = p,
            Name = Path.GetFileName(p),
            Length = 16,
            IsDirectory = false
        }).ToList();

    private static DeployedMod Deployed(string modId, int order, string pak) => new()
    {
        ModId = modId,
        OrderIndex = order,
        DeployedFiles = [new StagedFile(BaldursGate3GameModule.ModsRoot, pak, PackageTypes.Bg3Pak)]
    };

    private StagedMod Staged(params (string Root, string Pak, string[] Contents)[] paks)
    {
        var staging = NewTempDir();

        foreach (var (root, pak, contents) in paks)
        {
            Directory.CreateDirectory(Path.Combine(staging, root));
            Bg3TestPak.WriteModWith(Path.Combine(staging, root, pak), A, "Mod", contents);
        }

        return new StagedMod
        {
            ModId = "mod",
            StagingPath = staging,
            Files = paks.Select(p => new StagedFile(p.Root, p.Pak, PackageTypes.Bg3Pak)).ToList()
        };
    }

    private string NewModSettings(string contents)
    {
        var path = Path.Combine(NewTempDir(), BG3ModSettings.FileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string ModsFolder(BaldursGate3GameModule module, GameInstallation installation)
    {
        var mods = module.ResolveModPaths(installation).GetRoot(BaldursGate3GameModule.ModsRoot)!;
        Directory.CreateDirectory(mods);
        return mods;
    }

    private GameInstallation Installation(bool scriptExtender = true)
    {
        var gameDir = NewTempDir();
        Directory.CreateDirectory(Path.Combine(gameDir, "bin"));
        Directory.CreateDirectory(Path.Combine(gameDir, "Data"));
        File.WriteAllText(Path.Combine(gameDir, "Data", "Gustav.pak"), string.Empty);

        if (scriptExtender)
            File.WriteAllText(Path.Combine(gameDir, "bin", "DWrite.dll"), string.Empty);

        return new GameInstallation
        {
            GameId = "baldursgate3",
            SteamAppId = 1086940,
            InstallPath = gameDir,
            ProtonPrefixPath = NewTempDir()
        };
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
