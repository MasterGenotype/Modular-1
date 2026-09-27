using System.IO.Hashing;

namespace Modular.Core.Deployment;

/// <summary>
/// Places files as hardlinks: a second directory entry for the same data.
/// </summary>
/// <remarks>
/// The default. No extra disk use, and nothing — game, launcher or anti-cheat — can tell
/// a hardlink from a normal file. Identity is the inode, so an atomic save (which writes
/// a temp file and renames it over the link) shows up as a new inode, and an in-place
/// edit shows up as a changed size or modification time. Requires staging and the target
/// to share a filesystem.
/// </remarks>
public sealed class HardlinkDeploymentStrategy : IDeploymentStrategy
{
    /// <inheritdoc />
    public DeploymentMethod Method => DeploymentMethod.Hardlink;

    /// <inheritdoc />
    public FileIdentity Place(string stagedPath, string destinationPath)
    {
        // A pre-existing entry must go first: link() refuses to overwrite.
        DeploymentFile.RemoveEntry(destinationPath);

        NativeLink.CreateHardLink(stagedPath, destinationPath);

        return ReadIdentity(destinationPath)
            ?? throw new IOException($"Hardlink '{destinationPath}' vanished after creation.");
    }

    /// <inheritdoc />
    public FileIdentity? ReadIdentity(string destinationPath)
    {
        var info = new FileInfo(destinationPath);
        if (!info.Exists)
            return null;

        return new FileIdentity(
            DeploymentMethod.Hardlink,
            Size: info.Length,
            ModifiedUtcTicks: info.LastWriteTimeUtc.Ticks,
            Inode: NativeLink.TryGetInodeIdentity(destinationPath));
    }

    /// <inheritdoc />
    public bool Matches(FileIdentity recorded, FileIdentity current)
    {
        // The inode is the strong signal, where the platform provides one.
        if (recorded.Inode != null && current.Inode != null)
            return recorded.Inode == current.Inode;

        return recorded.Size == current.Size &&
               recorded.ModifiedUtcTicks == current.ModifiedUtcTicks;
    }

    /// <inheritdoc />
    public void Remove(string destinationPath) => DeploymentFile.RemoveEntry(destinationPath);

    /// <inheritdoc />
    public string? Probe(string stagingRoot, string destinationRoot) =>
        DeploymentFile.Probe(stagingRoot, destinationRoot, (source, destination) =>
        {
            NativeLink.CreateHardLink(source, destination);
        });
}

/// <summary>
/// Places files as symbolic links pointing at the absolute staged path.
/// </summary>
/// <remarks>
/// Works across drives, which hardlinks cannot, but is visibly a link and dangles if the
/// staging folder moves. Targets are absolute on purpose: a relative target breaks as soon
/// as two roots live in different trees, which is the normal case once a Documents root is
/// involved. Identity is "is a link, pointing here", read without following the link.
/// </remarks>
public sealed class SymlinkDeploymentStrategy : IDeploymentStrategy
{
    /// <inheritdoc />
    public DeploymentMethod Method => DeploymentMethod.Symlink;

    /// <inheritdoc />
    public FileIdentity Place(string stagedPath, string destinationPath)
    {
        DeploymentFile.RemoveEntry(destinationPath);

        var absoluteTarget = Path.GetFullPath(stagedPath);

        try
        {
            File.CreateSymbolicLink(destinationPath, absoluteTarget);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DeploymentMethodUnavailableException(
                DeploymentMethod.Symlink,
                OperatingSystem.IsWindows()
                    ? "Creating symbolic links needs Developer Mode or an elevated process."
                    : $"Not permitted to create a symbolic link at '{destinationPath}'.",
                ex);
        }
        catch (IOException ex) when (IsUnsupported(ex))
        {
            throw new DeploymentMethodUnavailableException(
                DeploymentMethod.Symlink,
                $"The filesystem holding '{destinationPath}' does not support symbolic links.",
                ex);
        }

        return new FileIdentity(DeploymentMethod.Symlink, LinkTarget: absoluteTarget);
    }

    /// <inheritdoc />
    public FileIdentity? ReadIdentity(string destinationPath)
    {
        var info = new FileInfo(destinationPath);

        // A dangling link still exists as an entry, and Exists is false for one, so the
        // link target is checked first.
        var target = info.LinkTarget;

        if (target == null && !info.Exists)
            return null;

        return new FileIdentity(
            DeploymentMethod.Symlink,
            LinkTarget: target == null
                ? null
                : Path.IsPathRooted(target)
                    ? target
                    : Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(destinationPath) ?? string.Empty, target)));
    }

    /// <inheritdoc />
    public bool Matches(FileIdentity recorded, FileIdentity current) =>
        // No target means the entry is no longer a link: an atomic save replaced it with
        // a regular file, and its contents are the user's, not ours.
        current.LinkTarget != null &&
        string.Equals(recorded.LinkTarget, current.LinkTarget, StringComparison.Ordinal);

    /// <inheritdoc />
    public void Remove(string destinationPath) => DeploymentFile.RemoveEntry(destinationPath);

    /// <inheritdoc />
    public string? Probe(string stagingRoot, string destinationRoot) =>
        DeploymentFile.Probe(stagingRoot, destinationRoot, (source, destination) =>
        {
            try
            {
                File.CreateSymbolicLink(destination, Path.GetFullPath(source));
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new DeploymentMethodUnavailableException(
                    DeploymentMethod.Symlink,
                    OperatingSystem.IsWindows()
                        ? "Creating symbolic links needs Developer Mode or an elevated process."
                        : "Not permitted to create symbolic links here.",
                    ex);
            }
            catch (IOException ex) when (IsUnsupported(ex))
            {
                throw new DeploymentMethodUnavailableException(
                    DeploymentMethod.Symlink,
                    "This filesystem does not support symbolic links.",
                    ex);
            }
        });

    private static bool IsUnsupported(IOException ex) =>
        ex is not FileNotFoundException and not DirectoryNotFoundException;
}

/// <summary>
/// Places files by copying them.
/// </summary>
/// <remarks>
/// The compatibility option: works on any filesystem, needs no privileges, and isolates
/// the staged bytes from anything written through the game path. Identity is a content
/// hash computed while copying, which makes a redeploy incremental — reordering a large
/// mod list only rewrites the files whose winner actually changed.
/// </remarks>
public sealed class CopyDeploymentStrategy : IDeploymentStrategy
{
    /// <inheritdoc />
    public DeploymentMethod Method => DeploymentMethod.Copy;

    /// <inheritdoc />
    public FileIdentity Place(string stagedPath, string destinationPath)
    {
        DeploymentFile.RemoveEntry(destinationPath);

        File.Copy(stagedPath, destinationPath, overwrite: true);

        // Preserving the source timestamp keeps a redeploy from looking like a change.
        var stagedWriteTime = File.GetLastWriteTimeUtc(stagedPath);
        File.SetLastWriteTimeUtc(destinationPath, stagedWriteTime);

        return ReadIdentity(destinationPath)
            ?? throw new IOException($"Copy '{destinationPath}' vanished after creation.");
    }

    /// <inheritdoc />
    public FileIdentity? ReadIdentity(string destinationPath)
    {
        var info = new FileInfo(destinationPath);
        if (!info.Exists)
            return null;

        return new FileIdentity(
            DeploymentMethod.Copy,
            Size: info.Length,
            ModifiedUtcTicks: info.LastWriteTimeUtc.Ticks,
            ContentHash: DeploymentFile.HashFile(destinationPath));
    }

    /// <inheritdoc />
    public bool Matches(FileIdentity recorded, FileIdentity current) =>
        recorded.ContentHash != null &&
        string.Equals(recorded.ContentHash, current.ContentHash, StringComparison.Ordinal);

    /// <inheritdoc />
    public void Remove(string destinationPath) => DeploymentFile.RemoveEntry(destinationPath);

    /// <inheritdoc />
    public string? Probe(string stagingRoot, string destinationRoot) =>
        DeploymentFile.Probe(stagingRoot, destinationRoot, (source, destination) =>
        {
            File.Copy(source, destination, overwrite: true);
        });
}

/// <summary>
/// Filesystem primitives shared by the deployment strategies.
/// </summary>
public static class DeploymentFile
{
    /// <summary>
    /// Deletes a path's directory entry without following it.
    /// </summary>
    /// <param name="path">Path to remove. A missing path is not an error.</param>
    /// <remarks>
    /// <see cref="File.Delete(string)"/> unlinks the entry rather than the data, which is
    /// exactly right here: for a hardlink the staged copy keeps the bytes alive, and for a
    /// symlink the link goes while its target stays.
    /// </remarks>
    public static void RemoveEntry(string path)
    {
        var info = new FileInfo(path);

        // A dangling symlink reports Exists == false but still occupies the name.
        if (info.Exists || info.LinkTarget != null)
            File.Delete(path);
    }

    /// <summary>
    /// Hashes a file's contents with xxHash64.
    /// </summary>
    /// <param name="path">File to hash.</param>
    /// <returns>The hash, lowercase hex.</returns>
    /// <remarks>
    /// Chosen for speed over cryptographic strength: this detects accidental drift in
    /// files Modular itself placed, not tampering.
    /// </remarks>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = new XxHash64();
        hash.Append(stream);
        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    /// <summary>
    /// Runs a placement once between two directories and cleans up after itself.
    /// </summary>
    /// <param name="stagingRoot">Directory the probe source is created in.</param>
    /// <param name="destinationRoot">Directory the probe destination is created in.</param>
    /// <param name="place">The placement to try.</param>
    /// <returns>Null on success, otherwise why the method is unavailable.</returns>
    public static string? Probe(
        string stagingRoot,
        string destinationRoot,
        Action<string, string> place)
    {
        var probeName = ".modular-probe-" + Guid.NewGuid().ToString("N")[..8];
        var source = Path.Combine(stagingRoot, probeName + ".src");
        var destination = Path.Combine(destinationRoot, probeName + ".dst");

        try
        {
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(destinationRoot);

            File.WriteAllText(source, "probe");
            place(source, destination);
            return null;
        }
        catch (DeploymentMethodUnavailableException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            TryRemove(destination);
            TryRemove(source);
        }
    }

    private static void TryRemove(string path)
    {
        try
        {
            RemoveEntry(path);
        }
        catch (Exception)
        {
            // Probe cleanup is best effort.
        }
    }
}
