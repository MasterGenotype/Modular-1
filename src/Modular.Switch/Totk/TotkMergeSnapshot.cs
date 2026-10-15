using System.Text.Json;
using System.Text.Json.Serialization;
using Modular.Core.Deployment;
using Modular.Switch.Totk.Helpers;

namespace Modular.Switch.Totk;

/// <summary>
/// The merge output as it was before the latest merge, so <c>switch rollback</c> can put
/// it back. Only the latest is kept.
/// </summary>
/// <remarks>
/// Files are hardlinked into the snapshot (copied across filesystems), which costs next to
/// nothing. That is safe because a merge deletes the merge targets before writing, so it
/// never writes into a file the snapshot shares. A restore copies rather than links, so
/// the restored output shares nothing with the snapshot either.
/// </remarks>
public sealed class TotkMergeSnapshot(TotkPaths paths)
{
    /// <summary>The merge output entries a merge replaces (rc1 <c>TKMM.Merge</c>).</summary>
    public static readonly string[] MergeTargets = ["romfs", "romfslite", "exefs", "cheats", "romfs_metadata.bin"];

    private const string InfoFileName = "snapshot.json";

    private readonly string _folder = paths.SnapshotFolder;

    /// <summary>The snapshot's description, or null when there is none.</summary>
    public TotkSnapshotInfo? Info
    {
        get
        {
            var infoPath = Path.Combine(_folder, InfoFileName);
            if (!File.Exists(infoPath))
                return null;

            try
            {
                return JsonSerializer.Deserialize<TotkSnapshotInfo>(File.ReadAllText(infoPath));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Replaces the snapshot with the merge targets currently in
    /// <paramref name="mergeOutput"/>.
    /// </summary>
    public void Take(string mergeOutput)
    {
        mergeOutput = Path.GetFullPath(mergeOutput);

        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
        Directory.CreateDirectory(_folder);

        foreach (var target in MergeTargets)
        {
            var source = Path.Combine(mergeOutput, target);
            var destination = Path.Combine(_folder, target);

            if (File.Exists(source))
                LinkOrCopy(source, destination);
            else if (Directory.Exists(source))
                LinkOrCopyTree(source, destination);
        }

        var info = new TotkSnapshotInfo(mergeOutput, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(_folder, InfoFileName), JsonSerializer.Serialize(info));
    }

    /// <summary>
    /// Puts the snapshot back into <paramref name="mergeOutput"/>: deletes its merge targets
    /// and copies the snapshot's in. Nothing else in the folder is touched.
    /// </summary>
    /// <returns>
    /// False, changing nothing, when there is no snapshot or it was taken of a different
    /// folder.
    /// </returns>
    public bool Restore(string mergeOutput)
    {
        mergeOutput = Path.GetFullPath(mergeOutput);

        if (Info is not { } info || !string.Equals(info.MergeOutput, mergeOutput, StringComparison.Ordinal))
            return false;

        TotkDirectoryHelper.DeleteTargetsFromDirectory(mergeOutput, MergeTargets, recursive: true);
        Directory.CreateDirectory(mergeOutput);

        foreach (var target in MergeTargets)
        {
            var source = Path.Combine(_folder, target);
            var destination = Path.Combine(mergeOutput, target);

            if (File.Exists(source))
                File.Copy(source, destination);
            else if (Directory.Exists(source))
                TotkDirectoryHelper.Copy(source, destination);
        }

        return true;
    }

    private static void LinkOrCopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            LinkOrCopy(file, Path.Combine(destination, Path.GetFileName(file)));

        foreach (var directory in Directory.EnumerateDirectories(source))
            LinkOrCopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void LinkOrCopy(string source, string destination)
    {
        try
        {
            NativeLink.CreateHardLink(source, destination);
        }
        catch (DeploymentMethodUnavailableException)
        {
            File.Copy(source, destination);
        }
    }
}

/// <summary>What a <see cref="TotkMergeSnapshot"/> was taken of, and when.</summary>
/// <param name="MergeOutput">Full path of the merge output the snapshot was taken of.</param>
/// <param name="TakenAt">UTC time the snapshot was taken.</param>
public sealed record TotkSnapshotInfo(
    [property: JsonPropertyName("merge_output")] string MergeOutput,
    [property: JsonPropertyName("taken_at")] DateTime TakenAt);
