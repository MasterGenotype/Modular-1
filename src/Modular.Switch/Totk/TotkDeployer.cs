using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Switch.Installer;
using Modular.Switch.Totk.Helpers;

namespace Modular.Switch.Totk;

/// <summary>What <see cref="TotkDeployer.Deploy"/> did.</summary>
public enum TotkDeployOutcome
{
    /// <summary>A directory symlink to the merge output was created.</summary>
    Linked,

    /// <summary>The target already was a symlink to the merge output.</summary>
    AlreadyLinked,

    /// <summary>Symlinks failed, so the merge output was copied to the target.</summary>
    Copied,

    /// <summary>The target holds files Modular did not put there, so it was left alone.</summary>
    Refused,

    /// <summary>The target is the merge output itself, so there is nothing to do.</summary>
    Skipped
}

/// <summary>The outcome of a deploy and where it went.</summary>
public sealed record TotkDeployResult(TotkDeployOutcome Outcome, string Target, string? Message = null);

/// <summary>
/// Exposes the merge output to the emulator as one mod folder,
/// <c>&lt;load dir&gt;/0100F2C0115B6000/TKMM</c>, as TKMM v2.0.0-rc1's export locations do
/// (<c>ExportLocations</c>, <c>SymlinkHelper</c>).
/// </summary>
/// <remarks>
/// <para>Like rc1, the target is a directory symlink to the merge output, an empty target
/// folder is replaced, and a target folder with files in it is refused with a warning.</para>
/// <para>Unlike rc1: a target that already links to the merge output counts as deployed
/// (rc1 follows the link, sees files and warns); a symlink to anywhere else is replaced
/// (removing a link deletes no data); and when a symlink can't be created the merge
/// output is copied instead, with a warning. The copy carries a marker file so the next
/// deploy refreshes it (replacing only the merge targets inside) instead of refusing it.</para>
/// </remarks>
public static class TotkDeployer
{
    /// <summary>Marks a target folder as a copy Modular made of the merge output.</summary>
    public const string CopyMarkerFileName = ".modular-totk-copy";

    /// <summary>
    /// The emulator's TotK mod folder: from the configured emulator (Ryujinx, or the
    /// <c>qt-config.ini</c> of a Yuzu-family emulator), falling back to Yuzu's load
    /// folder (<see cref="YuzuPaths"/>).
    /// </summary>
    public static string ResolveTarget(TotkSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.EmulatorPath)
            && TotkEmulatorHelper.GetModPath(settings.EmulatorPath) is { Length: > 0 } modPath)
        {
            return modPath;
        }

        return Path.Combine(YuzuPaths.LoadDir(TotkTitle.TitleId), TotkTitle.ModFolderName);
    }

    /// <summary>
    /// Makes <paramref name="target"/> show the contents of <paramref name="mergeOutput"/>.
    /// </summary>
    /// <param name="mergeOutput">The merge output folder.</param>
    /// <param name="target">The emulator mod folder to create.</param>
    /// <param name="logger">Receives the warnings.</param>
    /// <param name="createSymlink">
    /// Creates the link (<c>link path, target</c>); <see cref="Directory.CreateSymbolicLink"/>
    /// by default. Tests replace it to exercise the copy fallback.
    /// </param>
    public static TotkDeployResult Deploy(
        string mergeOutput,
        string target,
        ILogger? logger = null,
        Action<string, string>? createSymlink = null)
    {
        mergeOutput = Path.GetFullPath(mergeOutput);
        target = Path.GetFullPath(target);
        createSymlink ??= (link, pointsTo) => Directory.CreateSymbolicLink(link, pointsTo);

        if (string.Equals(target, mergeOutput, StringComparison.OrdinalIgnoreCase))
            return new TotkDeployResult(TotkDeployOutcome.Skipped, target, "The merge output is the emulator mod folder.");

        Directory.CreateDirectory(mergeOutput);

        var existing = new DirectoryInfo(target);
        if (existing.LinkTarget is { } linkTarget)
        {
            var resolved = Path.GetFullPath(linkTarget, Path.GetDirectoryName(target)!);
            if (string.Equals(resolved.TrimEnd(Path.DirectorySeparatorChar), mergeOutput, StringComparison.Ordinal))
                return new TotkDeployResult(TotkDeployOutcome.AlreadyLinked, target);

            logger?.LogInformation("Replacing the link '{Target}' -> '{OldTarget}'", target, resolved);
            existing.Delete();
        }
        else if (File.Exists(target))
        {
            return Refuse(target, logger);
        }
        else if (Directory.Exists(target))
        {
            if (File.Exists(Path.Combine(target, CopyMarkerFileName)))
                return RefreshCopy(mergeOutput, target);

            if (Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any())
                return Refuse(target, logger);

            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            createSymlink(target, mergeOutput);
            return new TotkDeployResult(TotkDeployOutcome.Linked, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger?.LogWarning(
                "Could not create a symlink at '{Target}' ({Error}); copying the merge output instead. " +
                "Run deploy again after every merge to refresh the copy.", target, ex.Message);

            TotkDirectoryHelper.Copy(mergeOutput, target);
            File.WriteAllText(Path.Combine(target, CopyMarkerFileName), mergeOutput);
            return new TotkDeployResult(TotkDeployOutcome.Copied, target, ex.Message);
        }
    }

    private static TotkDeployResult RefreshCopy(string mergeOutput, string target)
    {
        TotkDirectoryHelper.DeleteTargetsFromDirectory(target, TotkMergeSnapshot.MergeTargets, recursive: true);
        TotkDirectoryHelper.Copy(mergeOutput, target, overwrite: true);
        File.WriteAllText(Path.Combine(target, CopyMarkerFileName), mergeOutput);
        return new TotkDeployResult(TotkDeployOutcome.Copied, target);
    }

    private static TotkDeployResult Refuse(string target, ILogger? logger)
    {
        logger?.LogWarning(
            "The export location '{ExportLocationPath}' cannot be created because it has contents.", target);
        return new TotkDeployResult(TotkDeployOutcome.Refused, target,
            $"'{target}' already has contents; move them aside and deploy again.");
    }
}
