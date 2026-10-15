namespace Modular.Switch.Totk.Helpers;

/// <summary>
/// Port of TKMM v2.0.0-rc1 <c>Tkmm.Core/Helpers/DirectoryHelper.cs</c> (deleting and
/// copying merge targets). Behaviour is unchanged.
/// </summary>
public static class TotkDirectoryHelper
{
    /// <summary>
    /// Deletes each of <paramref name="targets"/> (files or folders, relative to
    /// <paramref name="targetDirectory"/>) that exists. Nothing else is touched.
    /// </summary>
    public static void DeleteTargetsFromDirectory(string targetDirectory, string[] targets, bool recursive = false)
    {
        foreach (var target in targets)
        {
            var absolutePath = Path.Combine(targetDirectory, target);
            DeleteTarget(absolutePath, recursive);
        }
    }

    private static void DeleteTarget(string target, bool recursive = false)
    {
        if (File.Exists(target))
        {
            File.Delete(target);
            return;
        }

        if (Directory.Exists(target))
            Directory.Delete(target, recursive);
    }

    /// <summary>
    /// Deletes the files under each of <paramref name="targets"/> that pass
    /// <paramref name="filter"/>, and each folder left with nothing in it.
    /// </summary>
    public static void DeleteTargetsFromDirectory(string targetDirectory, string[] targets, Func<string, bool> filter, bool recursive = false)
    {
        foreach (var target in targets)
        {
            var absolutePath = Path.Combine(targetDirectory, target);
            DeleteTarget(absolutePath, filter, recursive);
        }
    }

    private static void DeleteTarget(string target, Func<string, bool> filter, bool recursive = false)
    {
        if (File.Exists(target) && filter(target))
        {
            File.Delete(target);
            return;
        }

        if (Directory.Exists(target))
        {
            if (DeleteTargetFolder(target, filter, recursive))
                Directory.Delete(target);
        }
    }

    /// <summary>
    /// Returns true if the <paramref name="target"/> can be deleted.
    /// </summary>
    private static bool DeleteTargetFolder(string target, Func<string, bool> filter, bool recursive = false)
    {
        var deleted = 0;
        var files = Directory.GetFiles(target);

        foreach (var file in files.Where(filter))
        {
            deleted++;
            File.Delete(file);
        }

        var folders = Directory.GetDirectories(target);

        if (!recursive)
            return folders.Length + files.Length == deleted;

        foreach (var folder in folders.Where(folder => filter(folder) && DeleteTargetFolder(folder, filter, recursive)))
        {
            deleted++;
            Directory.Delete(folder);
        }

        return folders.Length + files.Length == deleted;
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="output"/> recursively.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="output"/> is inside <paramref name="source"/>.
    /// </exception>
    public static void Copy(string source, string output, bool overwrite = false)
    {
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);

        if (output.Length >= source.Length && output[..source.Length] == source)
        {
            throw new InvalidOperationException(
                $"The folder '{source}' cannot be recursively copied into itself ('{output}').");
        }

        Directory.CreateDirectory(output);

        foreach (var sourceFile in Directory.EnumerateFiles(source))
        {
            var outputFile = Path.Combine(output, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, outputFile, overwrite);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var outputDirectory = Path.Combine(output, Path.GetFileName(directory));
            Copy(directory, outputDirectory, overwrite);
        }
    }
}
