using System.Security.Cryptography;

namespace Modular.Core.Depots;

/// <summary>
/// Works out which archived manifest the files on disk actually are, without trusting the acf.
/// <list type="number">
/// <item>Stat every candidate's files (cheap) and keep the candidates that fit best by size.</item>
/// <item>Hash only the files whose content differs between those candidates (SHA-1, as Steam records it).
///   For 2.31 vs the 2_31_old branch that is one DLL, not 66 GB.</item>
/// <item>With <c>fullVerify</c>, hash every file of the winner to find modified files.</item>
/// </list>
/// </summary>
public sealed class BuildIdentifier(IFileHasher? hasher = null)
{
    private readonly IFileHasher _hasher = hasher ?? new Sha1FileHasher();

    public async Task<DepotIdentification> IdentifyAsync(
        string installPath,
        uint depotId,
        IReadOnlyList<ManifestView> candidates,
        bool fullVerify = false,
        IProgress<HashProgress>? progress = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? ignore = null)
    {
        if (candidates.Count == 0)
            return DepotIdentification.Empty(depotId, DepotMatch.NoCandidates);

        // Paths a mod deployed over (e.g. a loader replacing a game DLL) say nothing about the build.
        if (ignore is { Count: > 0 })
            candidates = candidates.Select(c => c.Without(ignore)).ToList();

        // 1. Stat once per path across all candidates.
        var stats = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var path in candidates.SelectMany(c => c.Files).Select(f => f.Path).Distinct(StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(installPath, path));
            stats[path] = info.Exists ? info.Length : null;
        }

        var scored = candidates.Select(c =>
        {
            int sizeMatches = 0, missing = 0, sizeMismatch = 0;
            foreach (var f in c.Files)
            {
                var size = stats[f.Path];
                if (size is null) missing++;
                else if ((ulong)size.Value == f.Size) sizeMatches++;
                else sizeMismatch++;
            }
            return new Scored(c, sizeMatches, missing, sizeMismatch);
        }).ToList();

        var bestScore = scored.Max(s => s.Score);
        var top = scored.Where(s => s.Score == bestScore).ToList();
        if (top.All(s => s.SizeMatches == 0))
            return DepotIdentification.Empty(depotId, DepotMatch.Unknown) with { Candidates = Scores(scored, null) };

        // 2. Hash the files that tell the top candidates apart.
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var distinguishing = top.Count > 1 ? DistinguishingPaths(top.Select(t => t.View).ToList(), stats) : new List<string>();
        var hashProgress = new HashProgressTracker(progress, distinguishing.Sum(p => stats[p] ?? 0));
        foreach (var path in distinguishing)
            hashes[path] = await HashAsync(installPath, path, hashProgress, ct);

        var hashScores = top.ToDictionary(t => t.View.ManifestId, t => distinguishing.Count(p =>
            t.View.ByPath.TryGetValue(p, out var f) && hashes[p] is { } h && string.Equals(f.Sha1, h, StringComparison.OrdinalIgnoreCase)));
        var bestHash = hashScores.Values.DefaultIfEmpty(0).Max();
        var winners = top.Where(t => hashScores[t.View.ManifestId] == bestHash).Select(t => t.View).ToList();

        // Several winners with identical content are the same build under different manifest ids.
        var chosen = winners.OrderByDescending(w => w.CreationTime).First();
        var equivalent = winners.Where(w => w != chosen && DepotDiff.Compare(w, chosen).IsIdentical)
            .Select(w => w.ManifestId).ToList();
        var ambiguous = winners.Count - 1 > equivalent.Count;

        // 3. Full verify of the winner, reusing hashes already taken.
        if (fullVerify)
        {
            var toHash = chosen.Files.Where(f => stats[f.Path] is { } s && (ulong)s == f.Size && !hashes.ContainsKey(f.Path)).ToList();
            hashProgress = new HashProgressTracker(progress, toHash.Sum(f => (long)f.Size));
            foreach (var f in toHash)
                hashes[f.Path] = await HashAsync(installPath, f.Path, hashProgress, ct);
        }

        var missingFiles = chosen.Files.Where(f => stats[f.Path] is null).Select(f => f.Path).ToList();
        var sizeMismatchFiles = chosen.Files.Where(f => stats[f.Path] is { } s && (ulong)s != f.Size).Select(f => f.Path).ToList();
        var hashMismatchFiles = chosen.Files
            .Where(f => hashes.TryGetValue(f.Path, out var h) && h is not null && !string.Equals(h, f.Sha1, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path).ToList();

        var match = (missingFiles.Count + sizeMismatchFiles.Count + hashMismatchFiles.Count) > 0 ? DepotMatch.Modified
            : ambiguous ? DepotMatch.Ambiguous
            : fullVerify || distinguishing.Count > 0 ? DepotMatch.Exact
            : DepotMatch.SizesOnly;

        return new DepotIdentification(
            depotId, match, chosen.ManifestId, equivalent,
            FilesHashed: hashes.Count,
            Missing: missingFiles, SizeMismatch: sizeMismatchFiles, HashMismatch: hashMismatchFiles,
            Candidates: Scores(scored, hashScores));
    }

    /// <summary>Paths where the candidates disagree (present in some only, or different size/hash),
    /// limited to files on disk whose size fits at least one candidate (otherwise hashing proves nothing).</summary>
    private static List<string> DistinguishingPaths(IReadOnlyList<ManifestView> top, IReadOnlyDictionary<string, long?> stats)
    {
        var result = new List<string>();
        foreach (var path in top.SelectMany(t => t.Files).Select(f => f.Path).Distinct(StringComparer.Ordinal))
        {
            var versions = top.Select(t => t.ByPath.TryGetValue(path, out var f) ? f : null).ToList();
            var differ = versions.Any(v => v is null)
                || versions.Select(v => v!.Sha1.ToUpperInvariant()).Distinct().Count() > 1;
            if (!differ || stats[path] is not { } size)
                continue;
            if (versions.Any(v => v is not null && v.Size == (ulong)size))
                result.Add(path);
        }
        return result;
    }

    private async Task<string?> HashAsync(string installPath, string path, HashProgressTracker progress, CancellationToken ct)
    {
        var full = Path.Combine(installPath, path);
        try
        {
            var hash = await _hasher.Sha1Async(full, ct);
            progress.Add(path, new FileInfo(full).Length);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<CandidateScore> Scores(IEnumerable<Scored> scored, IReadOnlyDictionary<ulong, int>? hashScores) =>
        scored.Select(s => new CandidateScore(
                s.View.ManifestId, s.View.CreationTime, s.View.Files.Count, s.SizeMatches, s.Missing, s.SizeMismatch,
                hashScores is not null && hashScores.TryGetValue(s.View.ManifestId, out var h) ? h : null))
            .OrderByDescending(c => c.SizeMatches - c.Missing - c.SizeMismatch)
            .ThenByDescending(c => c.HashMatches ?? -1)
            .ToList();

    private sealed record Scored(ManifestView View, int SizeMatches, int Missing, int SizeMismatch)
    {
        public int Score => SizeMatches - Missing - SizeMismatch;
    }

    private sealed class HashProgressTracker(IProgress<HashProgress>? progress, long totalBytes)
    {
        private long _done;
        public void Add(string path, long bytes)
        {
            _done += bytes;
            progress?.Report(new HashProgress(path, _done, totalBytes));
        }
    }
}

public enum DepotMatch
{
    /// <summary>Hashes confirm one manifest (distinguishing files, or a full verify).</summary>
    Exact,
    /// <summary>Only one candidate fits and every size matches; no hashing was needed to choose.</summary>
    SizesOnly,
    /// <summary>Closest manifest found, but files are missing or differ from it.</summary>
    Modified,
    /// <summary>Two or more different manifests fit equally well.</summary>
    Ambiguous,
    /// <summary>No archived manifest fits the files on disk.</summary>
    Unknown,
    /// <summary>No archived manifest for this depot at all.</summary>
    NoCandidates,
}

public sealed record DepotIdentification(
    uint DepotId,
    DepotMatch Match,
    ulong? ManifestId,
    IReadOnlyList<ulong> EquivalentManifests,
    int FilesHashed,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> SizeMismatch,
    IReadOnlyList<string> HashMismatch,
    IReadOnlyList<CandidateScore> Candidates)
{
    public static DepotIdentification Empty(uint depotId, DepotMatch match) =>
        new(depotId, match, null, Array.Empty<ulong>(), 0, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<CandidateScore>());
}

public sealed record CandidateScore(
    ulong ManifestId, DateTime CreationTime, int Files, int SizeMatches, int Missing, int SizeMismatch, int? HashMatches);

public sealed record HashProgress(string CurrentFile, long BytesDone, long BytesTotal);

public interface IFileHasher
{
    /// <summary>Upper-case hex SHA-1 of the file's contents.</summary>
    Task<string> Sha1Async(string path, CancellationToken ct);
}

public sealed class Sha1FileHasher : IFileHasher
{
    public async Task<string> Sha1Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
#pragma warning disable CA5350 // SHA-1 is what Steam manifests record; this is identification, not security.
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, ct));
#pragma warning restore CA5350
    }
}
