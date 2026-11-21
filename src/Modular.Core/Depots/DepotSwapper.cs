using System.Text.Json;

namespace Modular.Core.Depots;

/// <summary>
/// Applies a staged build to the game folder: moves each staged file over the installed one,
/// deletes the files the target build dropped (a Steam verify never does), creates the
/// target's directories and prunes directories the swap left empty. Every completed step is
/// appended to a journal in the staging folder, so an interrupted swap resumes where it stopped.
/// Files not in either manifest (mods, runtime state) are never touched.
/// </summary>
public static class DepotSwapper
{
    public const string JournalFile = "swap-journal.log";

    /// <param name="modOwned">Game-folder paths a mod deployed over. They are left as the mod's; the target build's
    /// own copy of each is moved to <paramref name="keepBuildFilesIn"/> instead, for when the mod is removed.</param>
    public static SwapResult Apply(string installPath, string stagingDir, StagePlan plan, ManifestView installed, ManifestView target,
        IReadOnlySet<string>? modOwned = null, string? keepBuildFilesIn = null)
    {
        modOwned ??= new HashSet<string>();
        if (plan.FromManifest != installed.ManifestId || plan.ToManifest != target.ManifestId)
            throw new ArgumentException("The plan was made for different manifests.");

        var journalPath = Path.Combine(stagingDir, JournalFile);
        var done = File.Exists(journalPath)
            ? File.ReadAllLines(journalPath).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        using var journal = new StreamWriter(journalPath, append: true) { AutoFlush = true };

        int moved = 0, deleted = 0, kept = 0;
        foreach (var file in plan.Files)
        {
            var entry = "move " + file.Path;
            if (done.Contains(entry))
                continue;
            var source = Path.Combine(stagingDir, file.Path);
            var dest = Path.Combine(installPath, file.Path);
            if (modOwned.Contains(file.Path))
            {
                // The mod's file stays; the build's own version waits aside.
                dest = Path.Combine(keepBuildFilesIn ?? Path.Combine(stagingDir, "..", "_build-files"), file.Path);
                kept++;
            }
            if (!File.Exists(source))
                throw new StagingException($"staged file missing: {file.Path}. Run 'depot stage' again.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(source, dest, overwrite: true); // same filesystem: a rename, atomic per file
            journal.WriteLine(entry);
            moved++;
        }

        var removedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in plan.Diff.Removed)
        {
            var entry = "delete " + path;
            if (done.Contains(entry) || modOwned.Contains(path))
                continue;
            var full = Path.Combine(installPath, path);
            if (File.Exists(full))
            {
                File.Delete(full);
                deleted++;
            }
            journal.WriteLine(entry);
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                removedDirs.Add(dir);
        }

        foreach (var dir in target.Directories)
            Directory.CreateDirectory(Path.Combine(installPath, dir));

        var pruned = PruneEmptyDirectories(installPath, removedDirs, target);
        journal.WriteLine("complete");
        return new SwapResult(moved - kept, deleted, pruned, kept);
    }

    /// <summary>True when a journal says a swap finished applying (only staging cleanup may be left).</summary>
    public static bool IsComplete(string stagingDir)
    {
        var journal = Path.Combine(stagingDir, JournalFile);
        return File.Exists(journal) && File.ReadLines(journal).Contains("complete");
    }

    private static int PruneEmptyDirectories(string installPath, IEnumerable<string> dirs, ManifestView target)
    {
        var keep = target.Directories
            .Concat(target.Files.Select(f => Path.GetDirectoryName(f.Path) ?? ""))
            .ToHashSet(StringComparer.Ordinal);
        var pruned = 0;
        foreach (var start in dirs.OrderByDescending(d => d.Length))
        {
            var dir = start;
            while (!string.IsNullOrEmpty(dir) && !keep.Contains(dir))
            {
                var full = Path.Combine(installPath, dir);
                if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
                    break;
                Directory.Delete(full);
                pruned++;
                dir = Path.GetDirectoryName(dir);
            }
        }
        return pruned;
    }
}

public sealed record SwapResult(int FilesMoved, int FilesDeleted, int DirectoriesPruned, int ModFilesKept = 0);

/// <summary>Where staged builds live: on the same filesystem as the Steam library, so applying is a rename.</summary>
public static class StagingPaths
{
    public static string For(SteamAppManifest acf, uint depotId, ulong manifestId) =>
        Path.Combine(acf.LibraryRoot, "modular-staging", acf.AppId.ToString(), $"{depotId}_{manifestId}");

    public static string AppRoot(SteamAppManifest acf) =>
        Path.Combine(acf.LibraryRoot, "modular-staging", acf.AppId.ToString());
}

/// <summary>Is the Steam client running? acf writes are only allowed while it is not (decision D3).</summary>
public static class SteamProcess
{
    private static readonly string[] Names = ["steam", "steamwebhelper"];

    public static bool IsRunning()
    {
        if (!OperatingSystem.IsLinux())
            return System.Diagnostics.Process.GetProcesses().Any(p => Names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase));
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _))
                continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                if (Names.Contains(comm, StringComparer.Ordinal))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // process exited while scanning
            }
        }
        return false;
    }
}

/// <summary>JSON helpers shared by the depot state files.</summary>
internal static class DepotJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
