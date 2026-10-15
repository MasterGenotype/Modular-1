using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TkSharp.Core;

namespace Modular.Switch.Totk.Helpers;

/// <summary>
/// Port of the path lookups in TKMM v2.0.0-rc1 <c>Tkmm.Core/Helpers/TkRyujinxHelper.cs</c>:
/// Ryujinx's data folder (portable or app data), its TotK mod and SD folders, and the
/// update selected for TotK. rc1's interactive setup (<c>UseRyujinx</c>) is not ported.
/// Behaviour is unchanged.
/// </summary>
public static class TotkRyujinxHelper
{
    /// <summary>
    /// The update file selected for TotK in Ryujinx (<c>games/0100f2c0115b6000/updates.json</c>),
    /// or null when none is selected.
    /// </summary>
    public static string? GetSelectedUpdatePath(string emulatorFilePath)
    {
        var updatesJsonPath = Path.Combine(GetRyujinxDataFolder(emulatorFilePath), "games", "0100f2c0115b6000", "updates.json");
        if (File.Exists(updatesJsonPath))
        {
            try
            {
                var json = File.ReadAllText(updatesJsonPath);
                var updates = JsonSerializer.Deserialize<RyujinxUpdates>(json);
                if (updates != null && !string.IsNullOrWhiteSpace(updates.Selected))
                    return updates.Selected;
            }
            catch (Exception ex)
            {
                TkLog.Instance.LogError("Failed to read updates.json: {Error}", ex.Message);
            }
        }

        return null;
    }

    /// <summary>Ryujinx's TotK mod folder, <c>mods/contents/0100f2c0115b6000/TKMM</c>.</summary>
    public static string GetModPath(string emulatorExecutablePath)
        => GetModFolder(GetRyujinxDataFolder(emulatorExecutablePath));

    /// <summary>Ryujinx's SD card folder.</summary>
    public static string GetSdPath(string emulatorExecutablePath)
        => Path.Combine(GetRyujinxDataFolder(emulatorExecutablePath), "sdcard");

    private static string GetRyujinxDataFolder(string ryujinxExeFilePath)
    {
        if (Path.GetDirectoryName(ryujinxExeFilePath) is not { } ryujinxFolder)
            return AppDataInstall();

        var portable = Path.Combine(ryujinxFolder, "portable");

        return Directory.Exists(portable) ? portable : AppDataInstall();

        static string AppDataInstall() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ryujinx");
    }

    private static string GetModFolder(string ryujinxDataFolder)
        => Path.Combine(ryujinxDataFolder, "mods", "contents", "0100f2c0115b6000", TotkTitle.ModFolderName);

    private sealed record RyujinxUpdates(
        [property: JsonPropertyName("selected")]
        string Selected);
}
