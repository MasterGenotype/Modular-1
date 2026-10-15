using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Switch.Totk.Helpers;
using TkSharp.Core;
using TkSharp.Core.IO.Caching;
using TkSharp.Data.Embedded;
using TkSharp.Extensions.LibHac;
using TkSharp.Extensions.LibHac.Util;

namespace Modular.Switch.Totk;

/// <summary>
/// Builds TkSharp's ROM provider from <see cref="TotkSettings"/>. Port of TKMM
/// v2.0.0-rc1 <c>TkConfig.CreateRomProvider</c> (desktop build). Behaviour is unchanged.
/// </summary>
/// <remarks>
/// With an emulator configured and the preferred version left on Auto, the emulator's
/// own game is used: for Ryujinx the update selected for TotK, otherwise the update on
/// the emulator's NAND. When that can't be found, the error is logged and the
/// configured SD card, update files and NAND folders are used instead. The settings are
/// read through delegates, so a provider sees later changes to them.
/// </remarks>
public static class TotkRomProviderFactory
{
    /// <summary>Builds a ROM provider reading <paramref name="settings"/>.</summary>
    public static TkExtensibleRomProvider Create(TotkSettings settings)
    {
        using var checksums = TkEmbeddedDataSource.GetChecksumsBin();
        using var packFileLookup = TkEmbeddedDataSource.GetPackFileLookup();

        var builder = TkExtensibleRomProviderBuilder.Create(
                TkChecksums.FromStream(checksums), new TkPackFileLookup(packFileLookup)
            )
            .WithPreferredVersion(() => settings.PreferredGameVersion is TotkSettings.AutoGameVersion ? null : settings.PreferredGameVersion)
            .WithKeysFolder(() => settings.KeysFolderPath)
            .WithExtractedGameDump(() => settings.GameDumpFolderPaths)
            .WithPackagedBaseGame(() => settings.PackagedBaseGamePaths);

        var emulatorFilePath = settings.EmulatorPath;
        if (string.IsNullOrWhiteSpace(emulatorFilePath) || !settings.PreferredGameVersion.Equals(TotkSettings.AutoGameVersion))
            return Configured(builder, settings);

        if (TotkEmulatorHelper.IsRyujinx(emulatorFilePath))
        {
            try
            {
                if (TotkRyujinxHelper.GetSelectedUpdatePath(emulatorFilePath) is { } updateFilePath)
                {
                    return builder
                        .WithSdCard(() => null)
                        .WithPackagedUpdate(() => [updateFilePath])
                        .WithNand(() => null)
                        .Build();
                }

                throw new Exception("No update is selected in Ryujinx. Right click the game in the emulator, go to 'Manage Title Updates' and select one.");
            }
            catch (Exception ex)
            {
                TkLog.Instance.LogError(ex, "Failed to detect a TotK update");
                return Configured(builder, settings);
            }
        }

        try
        {
            if (TotkEmulatorHelper.GetNandPath(emulatorFilePath) is { } emulatorNandPath && Directory.Exists(emulatorNandPath))
            {
                var keys = TkKeyUtils.GetKeysFromFolder(settings.KeysFolderPath!);
                if (keys == null)
                    throw new Exception("Keys not found");

                TkNandUtils.IsValid(keys, emulatorNandPath, out var hasUpdate);

                if (!hasUpdate)
                    throw new Exception("No update on NAND");

                return builder
                    .WithSdCard(() => null)
                    .WithPackagedUpdate(() => null)
                    .WithNand(() => [emulatorNandPath])
                    .Build();
            }
        }
        catch (Exception ex)
        {
            TkLog.Instance.LogError(ex, "Ensure your keys and a TotK update are installed on your emulator's " +
                                        "NAND. Alternatively, select a Preferred Game Version in the dump settings.");
        }

        return Configured(builder, settings);
    }

    private static TkExtensibleRomProvider Configured(TkExtensibleRomProviderBuilder builder, TotkSettings settings) =>
        builder
            .WithSdCard(() => settings.SdCardRootPath)
            .WithPackagedUpdate(() => settings.PackagedUpdatePaths)
            .WithNand(() => settings.NandFolderPaths)
            .Build();
}
