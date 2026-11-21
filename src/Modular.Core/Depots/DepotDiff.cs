namespace Modular.Core.Depots;

/// <summary>
/// Manifest-to-manifest delta. The removed list is the authority for deletes on a build swap
/// (a Steam verify never deletes), and the chunk delta is what a real update downloads.
/// </summary>
public static class DepotDiff
{
    public static DepotDiffResult Compare(ManifestView from, ManifestView to)
    {
        if (from.DepotId != to.DepotId)
            throw new ArgumentException($"Manifests are from different depots ({from.DepotId} vs {to.DepotId}).");

        var added = new List<ManifestFile>();
        var changed = new List<(ManifestFile From, ManifestFile To)>();
        foreach (var file in to.Files)
        {
            if (!from.ByPath.TryGetValue(file.Path, out var old))
                added.Add(file);
            else if (old.Size != file.Size || !string.Equals(old.Sha1, file.Sha1, StringComparison.OrdinalIgnoreCase))
                changed.Add((old, file));
        }
        var removed = from.Files.Where(f => !to.ByPath.ContainsKey(f.Path)).ToList();

        // Chunks the target needs that the source build does not have anywhere.
        var haveChunks = from.Files.SelectMany(f => f.Chunks).Select(c => c.ChunkId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newChunks = to.Files.SelectMany(f => f.Chunks)
            .Where(c => !haveChunks.Contains(c.ChunkId))
            .DistinctBy(c => c.ChunkId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DepotDiffResult(
            from.DepotId, from.ManifestId, to.ManifestId,
            added.Select(f => f.Path).Order(StringComparer.Ordinal).ToList(),
            removed.Select(f => f.Path).Order(StringComparer.Ordinal).ToList(),
            changed.Select(c => c.To.Path).Order(StringComparer.Ordinal).ToList(),
            newChunks.Count,
            newChunks.Aggregate(0UL, (s, c) => s + c.CompressedLength),
            newChunks.Aggregate(0UL, (s, c) => s + c.UncompressedLength),
            to.TotalSize,
            from.TotalSize);
    }
}

public sealed record DepotDiffResult(
    uint DepotId,
    ulong FromManifest,
    ulong ToManifest,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed,
    int NewChunkCount,
    ulong DownloadBytes,
    ulong NewChunkUncompressedBytes,
    ulong ToTotalSize,
    ulong FromTotalSize)
{
    public bool IsIdentical => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}
