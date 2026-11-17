using System.Security.Cryptography;

namespace Modular.Core.Depots;

/// <summary>Where chunk bytes come from when they are not already on disk.</summary>
public interface IChunkSource
{
    /// <summary>Returns the chunk's uncompressed, decrypted bytes.</summary>
    Task<byte[]> GetChunkAsync(uint depotId, ManifestChunk chunk, CancellationToken ct);
}

/// <summary>
/// Builds every file a target manifest adds or changes inside a staging folder, without touching
/// the game. Each chunk is copied from the installed files when they already hold it (checked by
/// the chunk's SHA-1 id) and downloaded otherwise. Every staged file is checked against the
/// target's SHA-1 before it counts as staged. Re-running resumes: verified staged files are kept.
/// </summary>
public sealed class DepotStager(IChunkSource chunks)
{
    private const string PartialSuffix = ".modular-partial";

    public static StagePlan Plan(string installPath, string stagingDir, ManifestView installed, ManifestView target)
    {
        var diff = DepotDiff.Compare(installed, target);
        var toStage = diff.Added.Concat(diff.Changed).Select(p => target.ByPath[p]).ToList();
        var localChunks = LocalChunkIndex(installed);

        int local = 0, remote = 0, alreadyStaged = 0;
        ulong downloadBytes = 0, stageBytes = 0;
        foreach (var file in toStage)
        {
            stageBytes += file.Size;
            if (HasStagedSize(stagingDir, file)) // cheap; StageAsync re-checks the hash
            {
                alreadyStaged++;
                continue;
            }
            foreach (var c in file.Chunks)
            {
                if (localChunks.ContainsKey(c.ChunkId)) local++;
                else { remote++; downloadBytes += c.CompressedLength; }
            }
        }
        return new StagePlan(installed.DepotId, installed.ManifestId, target.ManifestId, diff, toStage,
            alreadyStaged, local, remote, downloadBytes, stageBytes);
    }

    /// <param name="chunkParallelism">Chunks fetched at once across all files. Steam files can be several GB
    /// of ~1 MB chunks, so parallelism has to be per chunk, not per file.</param>
    public async Task StageAsync(
        string installPath, string stagingDir, ManifestView installed, ManifestView target,
        IProgress<StageProgress>? progress = null, int chunkParallelism = 16, CancellationToken ct = default)
    {
        var plan = Plan(installPath, stagingDir, installed, target);
        var localChunks = LocalChunkIndex(installed);
        long total = plan.Files.Sum(f => (long)f.Size);
        long done = 0;
        void Advance(string path, long bytes) =>
            progress?.Report(new StageProgress(path, Interlocked.Add(ref done, bytes), total));

        using var slots = new SemaphoreSlim(chunkParallelism);
        var failures = new List<string>();

        // A few files at a time; the chunk semaphore is what bounds network and disk load.
        await Parallel.ForEachAsync(plan.Files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (file, token) =>
            {
                try
                {
                    if (IsStaged(stagingDir, file))
                        Advance(file.Path, (long)file.Size);
                    else
                        await StageFileAsync(installPath, stagingDir, target.DepotId, file, localChunks, slots, Advance, token);
                }
                catch (Exception ex) when (ex is IOException or StagingException or HttpRequestException
                                           && !token.IsCancellationRequested)
                {
                    lock (failures)
                        failures.Add($"{file.Path}: {ex.Message}");
                }
            });

        if (failures.Count > 0)
            throw new StagingException($"{failures.Count} file(s) could not be staged (re-run to resume):\n  " +
                                       string.Join("\n  ", failures.Take(20)));
    }

    private static bool HasStagedSize(string stagingDir, ManifestFile file)
    {
        var info = new FileInfo(Path.Combine(stagingDir, file.Path));
        return info.Exists && (ulong)info.Length == file.Size;
    }

    /// <summary>True when the staged copy exists and matches the target's size and SHA-1.</summary>
    public static bool IsStaged(string stagingDir, ManifestFile file)
    {
        var path = Path.Combine(stagingDir, file.Path);
        var info = new FileInfo(path);
        if (!info.Exists || (ulong)info.Length != file.Size)
            return false;
        return string.Equals(Sha1OfFile(path), file.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private async Task StageFileAsync(
        string installPath, string stagingDir, uint depotId, ManifestFile file,
        IReadOnlyDictionary<string, LocalChunk> localChunks, SemaphoreSlim slots,
        Action<string, long> advance, CancellationToken ct)
    {
        var dest = Path.Combine(stagingDir, file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var partial = dest + PartialSuffix;

        using (var handle = File.OpenHandle(partial, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous))
        {
            RandomAccess.SetLength(handle, (long)file.Size);
            // Positional writes are safe to run concurrently on one handle.
            await Task.WhenAll(file.Chunks.Select(async chunk =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var data = localChunks.TryGetValue(chunk.ChunkId, out var local)
                        ? await TryReadLocalChunkAsync(installPath, local, chunk, ct)
                        : null;
                    data ??= await chunks.GetChunkAsync(depotId, chunk, ct);
                    if (data.Length != chunk.UncompressedLength)
                        throw new StagingException($"chunk {chunk.ChunkId} is {data.Length} bytes, expected {chunk.UncompressedLength}");
                    await RandomAccess.WriteAsync(handle, data, (long)chunk.Offset, ct);
                    advance(file.Path, data.Length);
                }
                finally
                {
                    slots.Release();
                }
            }));
        }

        var hash = Sha1OfFile(partial);
        if (!string.Equals(hash, file.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new StagingException($"assembled file hash {hash} does not match the manifest's {file.Sha1}");
        }
        File.Move(partial, dest, overwrite: true);
    }

    /// <summary>Reads a chunk from an installed file and accepts it only if its SHA-1 equals the chunk id
    /// (the installed file might be modded or partially updated).</summary>
    private static async Task<byte[]?> TryReadLocalChunkAsync(string installPath, LocalChunk local, ManifestChunk chunk, CancellationToken ct)
    {
        var path = Path.Combine(installPath, local.Path);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            if (input.Length < (long)(local.Offset + chunk.UncompressedLength))
                return null;
            var buffer = new byte[chunk.UncompressedLength];
            input.Position = (long)local.Offset;
            await input.ReadExactlyAsync(buffer, ct);
#pragma warning disable CA5350 // Steam chunk ids are SHA-1 of the chunk content.
            var id = Convert.ToHexString(SHA1.HashData(buffer));
#pragma warning restore CA5350
            return string.Equals(id, chunk.ChunkId, StringComparison.OrdinalIgnoreCase) ? buffer : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, LocalChunk> LocalChunkIndex(ManifestView installed)
    {
        var index = new Dictionary<string, LocalChunk>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in installed.Files)
            foreach (var c in f.Chunks)
                index.TryAdd(c.ChunkId, new LocalChunk(f.Path, c.Offset));
        return index;
    }

    private static string Sha1OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(stream));
#pragma warning restore CA5350
    }

    private sealed record LocalChunk(string Path, ulong Offset);
}

public sealed record StagePlan(
    uint DepotId,
    ulong FromManifest,
    ulong ToManifest,
    DepotDiffResult Diff,
    IReadOnlyList<ManifestFile> Files,
    int AlreadyStaged,
    int LocalChunks,
    int DownloadChunks,
    ulong DownloadBytes,
    ulong StageBytes);

public sealed record StageProgress(string CurrentFile, long BytesDone, long BytesTotal);

public sealed class StagingException(string message) : Exception(message);
