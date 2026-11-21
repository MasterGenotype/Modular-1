using System.Text;

namespace Modular.Core.Installers.Enshrouded;

/// <summary>
/// Puts the game's data index back when EML is no longer installed.
/// </summary>
/// <remarks>
/// <para>
/// On every launch EML copies the pristine <c>enshrouded.kfc</c> to
/// <c>enshrouded.kfc.bak</c> (once per game version) and rebuilds <c>enshrouded.kfc</c>
/// from that backup with the mods applied. While EML is installed it keeps the file in
/// step with <c>mods/</c>, even with no mods at all. Once EML is removed nothing does, and
/// the game would keep loading the last patched index; so Modular copies the backup back.
/// </para>
/// <para>
/// The backup is only trusted when its version tag matches the current file's. After a
/// game update the current file is the new vanilla one and the backup belongs to the old
/// build, so it is left alone. Data EML appended to <c>enshrouded.kfc_resources</c> or
/// wrote to extra <c>.dat</c> containers is unreferenced by the vanilla index and harmless.
/// </para>
/// </remarks>
public static class EnshroudedKfc
{
    /// <summary>The client's data index.</summary>
    public const string IndexFile = "enshrouded.kfc";

    /// <summary>EML's copy of the vanilla index.</summary>
    public const string BackupFile = "enshrouded.kfc.bak";

    private const uint Magic = 0x3443464B; // "KFC4"

    /// <summary>Header offset of the version tag's location (relative offset, then length).</summary>
    private const int VersionLocationOffset = 16;

    /// <summary>
    /// Reads the version tag from a <c>.kfc</c> header.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The tag, or <c>null</c> when the file is missing or not a KFC index.</returns>
    public static string? ReadVersionTag(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < VersionLocationOffset + 8 || reader.ReadUInt32() != Magic)
                return null;

            stream.Position = VersionLocationOffset;
            var relative = reader.ReadUInt32();
            var length = reader.ReadUInt32();

            var start = VersionLocationOffset + (long)relative;
            if (length == 0 || length > 4096 || start + length > stream.Length)
                return null;

            stream.Position = start;
            return Encoding.UTF8.GetString(reader.ReadBytes((int)length)).TrimEnd('\0');
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Restores the vanilla index when no loader is installed and EML left a backup.
    /// </summary>
    /// <param name="gameDirectory">The game root.</param>
    /// <returns>A line for the deploy report, or <c>null</c> when there was nothing to do.</returns>
    public static string? RestoreIfLoaderGone(string gameDirectory)
    {
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
            return null;

        if (HasLoader(gameDirectory))
            return null;

        var backup = Path.Combine(gameDirectory, BackupFile);
        var index = Path.Combine(gameDirectory, IndexFile);

        if (!File.Exists(backup) || !File.Exists(index))
            return null;

        var backupTag = ReadVersionTag(backup);
        var indexTag = ReadVersionTag(index);

        if (backupTag == null || indexTag == null || !string.Equals(backupTag, indexTag, StringComparison.Ordinal))
        {
            return $"EML is no longer installed, but {BackupFile} is from a different game version " +
                   $"than {IndexFile}, so it was left alone. If the game misbehaves, verify its files.";
        }

        var temporary = index + ".modular-restore";
        File.Copy(backup, temporary, overwrite: true);
        File.Move(temporary, index, overwrite: true);
        File.Delete(backup);

        return $"EML is no longer installed: restored the original {IndexFile} from its backup.";
    }

    /// <summary>
    /// Whether a loader DLL sits beside the executable.
    /// </summary>
    /// <param name="gameDirectory">The game root.</param>
    /// <returns>True when one does.</returns>
    public static bool HasLoader(string gameDirectory) =>
        Directory.EnumerateFiles(gameDirectory)
            .Any(f => EnshroudedArchiveAnalyzer.LoaderDllNames.Contains(Path.GetFileName(f)));
}
