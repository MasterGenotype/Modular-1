using SteamKit2;

namespace Modular.Core.Depots;

/// <summary>
/// Immutable view of a depot manifest: the parts diffing and build identification need.
/// Decouples that logic from SteamKit2's <see cref="DepotManifest"/>, which cannot be built in memory.
/// </summary>
public sealed class ManifestView
{
    public ManifestView(uint depotId, ulong manifestId, DateTime creationTime, IEnumerable<ManifestFile> files)
    {
        DepotId = depotId;
        ManifestId = manifestId;
        CreationTime = creationTime;
        var all = files.ToList();
        Files = all.Where(f => !f.IsDirectory).ToList();
        Directories = all.Where(f => f.IsDirectory).Select(f => f.Path).ToList();
        ByPath = Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
    }

    /// <summary>Directory entries (including empty ones a swap must create).</summary>
    public IReadOnlyList<string> Directories { get; }

    public uint DepotId { get; }
    public ulong ManifestId { get; }
    public DateTime CreationTime { get; }

    /// <summary>Regular files and symlinks; directories are left out.</summary>
    public IReadOnlyList<ManifestFile> Files { get; }

    public IReadOnlyDictionary<string, ManifestFile> ByPath { get; }

    public ulong TotalSize => Files.Aggregate(0UL, (sum, f) => sum + f.Size);

    public static ManifestView From(DepotManifest manifest) => new(
        manifest.DepotID,
        manifest.ManifestGID,
        manifest.CreationTime,
        (manifest.Files ?? new()).Select(f => new ManifestFile(
            NormalizePath(f.FileName),
            f.TotalSize,
            Hex(f.FileHash),
            f.Flags.HasFlag(EDepotFileFlag.Directory),
            f.Chunks.Select(c => new ManifestChunk(Hex(c.ChunkID), c.CompressedLength, c.UncompressedLength, c.Offset, c.Checksum)).ToList())));

    /// <summary>Depot paths use '\' on Windows depots; Modular compares and joins with '/'.</summary>
    public static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string Hex(byte[]? bytes) => bytes is null ? string.Empty : Convert.ToHexString(bytes);
}

/// <param name="Sha1">Upper-case hex SHA-1 of the whole file, as Steam records it.</param>
public sealed record ManifestFile(string Path, ulong Size, string Sha1, bool IsDirectory, IReadOnlyList<ManifestChunk> Chunks);

/// <param name="ChunkId">Upper-case hex SHA-1 of the chunk's uncompressed bytes.</param>
/// <param name="Offset">Where the chunk sits in its file.</param>
/// <param name="Checksum">Steam's Adler-style checksum, passed back to the CDN client.</param>
public sealed record ManifestChunk(string ChunkId, uint CompressedLength, uint UncompressedLength, ulong Offset = 0, uint Checksum = 0);
