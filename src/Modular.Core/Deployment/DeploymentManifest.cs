using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Core.Deployment;

/// <summary>
/// The record of what Modular currently has deployed for one game.
/// </summary>
/// <remarks>
/// Stored as <c>staging/&lt;gameKey&gt;/deployment.json</c>. It answers the two questions
/// purge has to get right: which files in the game folder are Modular's, and is each of
/// them still the file Modular placed. Without it, a purge could not tell a mod file from
/// a vanilla one, and a "conflict" report could not tell a mod's file from Modular's own
/// previous output.
/// </remarks>
public sealed class DeploymentManifest
{
    /// <summary>Manifest format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Key of the game this deployment belongs to.</summary>
    public string GameKey { get; set; } = string.Empty;

    /// <summary>Store build id the deployment was made against.</summary>
    /// <remarks>
    /// A changed build id means the game was updated, so the vanilla files backed up
    /// under the old build are stale and must not be restored over the new ones.
    /// </remarks>
    public string? BuildId { get; set; }

    /// <summary>Deployment method requested for this deployment.</summary>
    public string RequestedMethod { get; set; } = DeploymentMethod.Hardlink.ToName();

    /// <summary>When the deployment was written.</summary>
    public DateTime DeployedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Mod ids in load order, later winning.</summary>
    public List<string> ModOrder { get; set; } = new();

    /// <summary>The deployed files.</summary>
    public List<DeployedFileRecord> Files { get; set; } = new();

    /// <summary>Absolute path of each named root at deploy time.</summary>
    public Dictionary<string, string> Roots { get; set; } = new();

    /// <summary>
    /// Manifest files Modular wrote (such as <c>modlist.txt</c>) whose pre-existing
    /// version was backed up.
    /// </summary>
    public List<string> WrittenManifests { get; set; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Path of the manifest for a game.
    /// </summary>
    /// <param name="gameStagingDirectory">The game's staging directory.</param>
    /// <returns>The manifest path.</returns>
    public static string PathFor(string gameStagingDirectory) =>
        Path.Combine(gameStagingDirectory, "deployment.json");

    /// <summary>
    /// Reads a manifest, or returns an empty one.
    /// </summary>
    /// <param name="gameStagingDirectory">The game's staging directory.</param>
    /// <param name="gameKey">Game key to stamp on an empty manifest.</param>
    /// <returns>The manifest. Never null: no manifest means nothing is deployed.</returns>
    public static DeploymentManifest Load(string gameStagingDirectory, string gameKey)
    {
        var path = PathFor(gameStagingDirectory);

        if (!File.Exists(path))
            return new DeploymentManifest { GameKey = gameKey };

        try
        {
            return JsonSerializer.Deserialize<DeploymentManifest>(
                       File.ReadAllText(path), SerializerOptions)
                   ?? new DeploymentManifest { GameKey = gameKey };
        }
        catch (JsonException)
        {
            // A corrupt manifest must not make the game folder unpurgeable, but it must
            // not silently pretend the game is clean either — it is kept for inspection.
            var salvaged = path + ".corrupt";
            File.Move(path, salvaged, overwrite: true);
            return new DeploymentManifest { GameKey = gameKey };
        }
    }

    /// <summary>
    /// Writes the manifest.
    /// </summary>
    /// <param name="gameStagingDirectory">The game's staging directory.</param>
    public void Save(string gameStagingDirectory)
    {
        Directory.CreateDirectory(gameStagingDirectory);
        File.WriteAllText(
            PathFor(gameStagingDirectory),
            JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>
    /// Looks a deployed file up by root and root-relative path.
    /// </summary>
    /// <param name="rootName">Root name.</param>
    /// <param name="relativePath">Path relative to that root.</param>
    /// <returns>The record, or <c>null</c> when Modular did not deploy that path.</returns>
    public DeployedFileRecord? Find(string rootName, string relativePath) =>
        Files.FirstOrDefault(f =>
            string.Equals(f.Root, rootName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.Path, relativePath, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One file Modular deployed, and how to recognise it later.
/// </summary>
public sealed class DeployedFileRecord
{
    /// <summary>Name of the root this file was deployed into.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Path relative to that root, forward-slashed.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Id of the mod that won this path.</summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>Absolute path of the staged source file.</summary>
    public string StagedPath { get; set; } = string.Empty;

    /// <summary>Method that actually placed the file.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Method that was asked for, when it differs from <see cref="Method"/>.
    /// </summary>
    /// <remarks>
    /// Present only for a file that fell back, so a mixed deployment is visible in the
    /// report and purge uses the right identity check per file.
    /// </remarks>
    public string? Requested { get; set; }

    /// <summary>Size in bytes, when the method records one.</summary>
    public long Size { get; set; } = -1;

    /// <summary>Last write time in UTC ticks, when the method records one.</summary>
    public long ModifiedUtcTicks { get; set; }

    /// <summary>Device and inode, for a hardlink.</summary>
    public string? Inode { get; set; }

    /// <summary>Absolute link target, for a symlink.</summary>
    public string? LinkTarget { get; set; }

    /// <summary>Content hash, for a copy.</summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Path of the vanilla file this deployment displaced, relative to the backup root.
    /// </summary>
    public string? VanillaBackup { get; set; }

    /// <summary>Package type label carried from the installer, when it supplied one.</summary>
    public string? PackageType { get; set; }

    /// <summary>
    /// Reads the record's identity back as a <see cref="FileIdentity"/>.
    /// </summary>
    /// <returns>The identity recorded at deploy time.</returns>
    public FileIdentity ToIdentity() => new(
        DeploymentMethods.ParseMethod(Method) ?? DeploymentMethod.Copy,
        Size,
        ModifiedUtcTicks,
        Inode,
        LinkTarget,
        ContentHash);

    /// <summary>
    /// Stores an identity on the record.
    /// </summary>
    /// <param name="identity">Identity to store.</param>
    public void SetIdentity(FileIdentity identity)
    {
        Method = identity.Method.ToName();
        Size = identity.Size;
        ModifiedUtcTicks = identity.ModifiedUtcTicks;
        Inode = identity.Inode;
        LinkTarget = identity.LinkTarget;
        ContentHash = identity.ContentHash;
    }
}
