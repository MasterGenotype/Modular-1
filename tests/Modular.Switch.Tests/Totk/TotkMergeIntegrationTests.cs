using FluentAssertions;
using MessageStudio.Formats.BinaryText;
using Modular.Core.Configuration;
using Modular.Switch.Totk;
using SarcLibrary;
using TkSharp.Core;
using Xunit;

namespace Modular.Switch.Tests.Totk;

/// <summary>Runs only when a TotK dump is configured through the environment.</summary>
public sealed class TotkIntegrationFactAttribute : FactAttribute
{
    public TotkIntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOTK_KEYS_DIR"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOTK_BASE_GAME")))
        {
            Skip = "Set TOTK_KEYS_DIR and TOTK_BASE_GAME (and TOTK_UPDATE or TOTK_NAND) to run.";
        }
    }
}

/// <summary>
/// Merges two real mods against the real game: both change the same file (the language's
/// MALS archive), each a different message, and the merged output must carry both.
/// </summary>
/// <remarks>
/// Environment: <c>TOTK_KEYS_DIR</c> (folder with prod.keys/title.keys),
/// <c>TOTK_BASE_GAME</c> (base game .xci/.nsp), and an update from <c>TOTK_UPDATE</c>
/// (update .nsp) or <c>TOTK_NAND</c> (emulator NAND folder with the update installed).
/// </remarks>
[Collection(TkSharpCollection.Name)]
public sealed class TotkMergeIntegrationTests : IDisposable
{
    private const string MalsCanonical = "Mals/USen.Product.sarc";
    private const TkFileAttributes MalsAttributes = TkFileAttributes.HasZsExtension | TkFileAttributes.IsProductFile;

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [TotkIntegrationFact]
    public async Task TwoModsEditingTheSameFile_AreBothInTheMergedOutput_WithARegeneratedRestbl()
    {
        var settings = new TotkSettings
        {
            KeysFolderPath = Environment.GetEnvironmentVariable("TOTK_KEYS_DIR"),
            PackagedBaseGamePaths = [Environment.GetEnvironmentVariable("TOTK_BASE_GAME")!],
            PackagedUpdatePaths = EnvList("TOTK_UPDATE"),
            NandFolderPaths = EnvList("TOTK_NAND"),
            MergeOutput = _temp.Combine("merged")
        };
        var service = new TotkMergeService(settings, new TotkPaths(_temp.Combine("totk")));

        string relativePath, messageFile, keyA, keyB;
        int gameVersion;
        using (var rom = service.GetTkRom())
        {
            gameVersion = rom.GameVersion;
            relativePath = rom.CanonicalToRelativePath(MalsCanonical, MalsAttributes);
            byte[] vanilla;
            using (var buffer = rom.GetVanilla(relativePath))
                vanilla = buffer.Span.ToArray();

            var sarc = Sarc.FromBinary(vanilla);
            (messageFile, var msbt) = sarc
                .Where(file => file.Key.EndsWith(".msbt", StringComparison.Ordinal))
                .Select(file => (file.Key, Msbt: Msbt.FromBinary(file.Value)))
                .First(file => file.Msbt.Count >= 2);
            keyA = msbt.Keys.First();
            keyB = msbt.Keys.Skip(1).First();

            WriteMod(rom, "ModA", vanilla, messageFile, keyA, "Edited by Modular mod A");
            WriteMod(rom, "ModB", vanilla, messageFile, keyB, "Edited by Modular mod B");
        }

        (await service.InstallAsync(_temp.Combine("ModA"))).Should().NotBeNull();
        (await service.InstallAsync(_temp.Combine("ModB"))).Should().NotBeNull();
        await service.MergeAsync();

        var mergedPath = Path.Combine(settings.MergeOutput, "romfs", relativePath);
        File.Exists(mergedPath).Should().BeTrue();
        using (var rom = service.GetTkRom())
        {
            var merged = Sarc.FromBinary(rom.Zstd.Decompress(File.ReadAllBytes(mergedPath)));
            var mergedMsbt = Msbt.FromBinary(merged[messageFile]);
            mergedMsbt[keyA].Text.Should().Be("Edited by Modular mod A");
            mergedMsbt[keyB].Text.Should().Be("Edited by Modular mod B");
        }

        var restbl = Path.Combine(settings.MergeOutput, "romfs", "System", "Resource",
            $"ResourceSizeTable.Product.{gameVersion}.rsizetable.zs");
        File.Exists(restbl).Should().BeTrue("the merger regenerates the RESTBL");
        new FileInfo(restbl).LastWriteTimeUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(-10));
    }

    [TotkIntegrationFact]
    public async Task ExportToSdCard_ReplacesTheGamesMergeTargets_AfterConfirming()
    {
        var settings = new TotkSettings
        {
            KeysFolderPath = Environment.GetEnvironmentVariable("TOTK_KEYS_DIR"),
            PackagedBaseGamePaths = [Environment.GetEnvironmentVariable("TOTK_BASE_GAME")!],
            PackagedUpdatePaths = EnvList("TOTK_UPDATE"),
            NandFolderPaths = EnvList("TOTK_NAND"),
            MergeOutput = _temp.Combine("merged")
        };
        var service = new TotkMergeService(settings, new TotkPaths(_temp.Combine("totk")));
        var drive = _temp.Combine("sd");
        var stale = _temp.File("sd/atmosphere/contents/0100F2C0115B6000/romfs/stale.bin");
        var userFile = _temp.File("sd/atmosphere/contents/0100F2C0115B6000/user.txt", "keep");

        var exported = await service.ExportToSdCardAsync(drive, (_, _) => Task.FromResult(true));

        exported.Should().BeTrue();
        File.Exists(stale).Should().BeFalse();
        File.ReadAllText(userFile).Should().Be("keep");
        Directory.EnumerateFiles(Path.Combine(drive, "atmosphere", "contents", "0100F2C0115B6000", "romfs", "Mals"))
            .Should().NotBeEmpty("the \"Mods Enabled\" title text is always merged");
    }

    private void WriteMod(ITkRom rom, string name, byte[] vanillaMals, string messageFile, string key, string text)
    {
        var sarc = Sarc.FromBinary(vanillaMals.ToArray());
        var msbt = Msbt.FromBinary(sarc[messageFile]);
        msbt[key] = new MsbtEntry { Text = text, Attribute = msbt[key].Attribute };

        using (var ms = new MemoryStream())
        {
            msbt.WriteBinary(ms);
            sarc[messageFile] = ms.ToArray();
        }

        using var sarcStream = new MemoryStream();
        sarc.Write(sarcStream);
        using var compressed = rom.Zstd.Compress(sarcStream.ToArray(), zsDictionaryId: 1);

        var path = _temp.Combine(name, "romfs", rom.CanonicalToRelativePath(MalsCanonical, MalsAttributes));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, compressed.Span.ToArray());
    }

    private static List<string> EnvList(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? [value] : [];
}
