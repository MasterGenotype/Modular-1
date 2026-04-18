using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modular.Switch.Models;
using SharpCompress.Archives;

namespace Modular.Switch.Scanner;

/// <summary>
/// Discovers Switch mods from a local directory tree.
/// Supports .zip, .7z, .rar, .bnp archives and pre-extracted folders.
///
/// Detection heuristics (in priority order):
/// 1. manifest.json / mod.json / info.json inside root of archive/folder
/// 2. Well-known LayeredFS path segments (romfs/, exefs/, cheats/)
/// 3. TitleID embedded in archive name or parent folder name
/// 4. 16-digit hex TitleID in any leading folder name inside the archive
///
/// BNP archives may contain an options/ directory with selectable option
/// groups described in info.json. The scanner populates BnpOptions on the
/// resulting SwitchMod when detected.
/// </summary>
public sealed class SwitchModScanner
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".bnp"];
    private static readonly Regex TitleIdInName =
        new(@"(?:^|[\s\-_\[(\.])(0[0-9A-Fa-f]{15})(?:$|[\s\-_\])\.])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<SwitchModScanner>? _log;

    public SwitchModScanner(ILogger<SwitchModScanner>? logger = null) => _log = logger;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="searchPath"/> recursively and returns all discovered mods.
    /// Optionally restricted to a single <paramref name="titleId"/>.
    /// </summary>
    public async Task<List<SwitchMod>> ScanAsync(
        string searchPath,
        SwitchTitleId? titleId = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            throw new DirectoryNotFoundException($"Scan path not found: {searchPath}");

        var results = new List<SwitchMod>();

        // Single-file shortcut
        if (File.Exists(searchPath))
        {
            var mod = await TryInspectArchiveAsync(searchPath, ct);
            if (mod != null) results.Add(mod);
            return results;
        }

        // Recurse into search root
        var entries = Directory.EnumerateFileSystemEntries(searchPath, "*", SearchOption.AllDirectories);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            SwitchMod? mod = null;

            if (File.Exists(entry) && IsArchive(entry))
            {
                progress?.Report($"Scanning archive: {Path.GetFileName(entry)}");
                mod = await TryInspectArchiveAsync(entry, ct);
            }
            else if (Directory.Exists(entry) && IsLikelySwitchModFolder(entry))
            {
                progress?.Report($"Scanning folder: {Path.GetFileName(entry)}");
                mod = await TryInspectFolderAsync(entry, ct);
            }

            if (mod == null) continue;
            if (titleId.HasValue &&
                !mod.TitleId.Equals(titleId.Value.Value, StringComparison.OrdinalIgnoreCase))
                continue;

            _log?.LogDebug("Discovered mod '{Name}' ({TitleId}) from {Source}",
                mod.Name, mod.TitleId, mod.SourcePath);
            results.Add(mod);
        }

        return results;
    }

    // ── Archive inspection ────────────────────────────────────────────────

    private async Task<SwitchMod?> TryInspectArchiveAsync(string archivePath, CancellationToken ct)
    {
        try
        {
            string? titleId = null;
            SwitchModCategory category = SwitchModCategory.Unknown;
            SwitchModManifest? manifest = null;
            BnpInfo? bnpInfo = null;
            var entryNames = new List<string>();
            bool isBnp = IsBnpFile(archivePath);
            bool hasOptionsDir = false;

            using (var archive = ArchiveFactory.Open(archivePath))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    var path = entry.Key?.Replace('\\', '/') ?? string.Empty;
                    entryNames.Add(path);

                    if (!hasOptionsDir && path.StartsWith("options/", StringComparison.OrdinalIgnoreCase))
                        hasOptionsDir = true;

                    // Try to read manifest / BNP info
                    if (IsManifestEntry(path) && IsRootLevelEntry(path))
                    {
                        if (isBnp && bnpInfo == null &&
                            Path.GetFileName(path).Equals("info.json", StringComparison.OrdinalIgnoreCase))
                        {
                            bnpInfo = await TryReadBnpInfoFromEntryAsync(entry, ct);
                        }

                        if (manifest == null)
                        {
                            manifest = await TryReadManifestFromEntryAsync(archive, entry, ct);
                        }
                    }
                }
            }

            // Priority 1: manifest carries explicit metadata
            if (manifest != null)
            {
                if (manifest.TitleId != null)
                    titleId = manifest.TitleId.ToUpperInvariant();
                if (manifest.Category != null)
                    category = SwitchModCategoryExtensions.FromPathSegment(manifest.Category);
            }

            // For BNP archives, use info.json fields when manifest fields are missing
            var effectiveName = manifest?.Name ?? bnpInfo?.Name;
            var effectiveVersion = manifest?.Version ?? bnpInfo?.Version;

            // Priority 2: well-known path segments (exclude BNP-internal dirs)
            if (category == SwitchModCategory.Unknown)
            {
                var categoryEntries = isBnp
                    ? entryNames.Where(e => !IsBnpInternalEntry(e))
                    : entryNames;
                category = InferCategoryFromEntries(categoryEntries);
            }

            // Priority 3: TitleID from archive name
            if (titleId == null)
                titleId = TryExtractTitleIdFromName(Path.GetFileNameWithoutExtension(archivePath));

            // Priority 4: TitleID from first leading folder in entries (exclude BNP-internal)
            if (titleId == null)
            {
                var titleEntries = isBnp
                    ? entryNames.Where(e => !IsBnpInternalEntry(e))
                    : entryNames;
                titleId = TryExtractTitleIdFromEntries(titleEntries);
            }

            if (titleId == null)
            {
                _log?.LogDebug("No TitleID found in {Archive} — skipping", Path.GetFileName(archivePath));
                return null;
            }

            var name = effectiveName ?? SanitiseName(Path.GetFileNameWithoutExtension(archivePath));
            var hash = await ComputeFileHashAsync(archivePath, ct);

            var mod = BuildMod(
                titleId, name, effectiveVersion ?? "0.0.0", category,
                manifest?.LoadOrder ?? 0,
                manifest?.Dependencies ?? [],
                manifest?.Conflicts ?? [],
                archivePath, false, hash);

            if (isBnp && bnpInfo?.Options != null && hasOptionsDir)
                mod.BnpOptions = bnpInfo.Options;

            return mod;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "Failed to inspect archive: {Path}", archivePath);
            return null;
        }
    }

    // ── Folder inspection ─────────────────────────────────────────────────

    private async Task<SwitchMod?> TryInspectFolderAsync(string folderPath, CancellationToken ct)
    {
        try
        {
            string? titleId = null;
            SwitchModCategory category = SwitchModCategory.Unknown;

            // Manifest file in folder root
            var manifest = SwitchModManifest.TryLoad(folderPath);

            // BNP info.json (may coexist with manifest — has different schema)
            var bnpInfo = BnpInfo.TryLoad(Path.Combine(folderPath, "info.json"));
            bool hasOptionsDir = Directory.Exists(Path.Combine(folderPath, "options"));

            if (manifest?.TitleId != null)
                titleId = manifest.TitleId.ToUpperInvariant();
            if (manifest?.Category != null)
                category = SwitchModCategoryExtensions.FromPathSegment(manifest.Category);

            var effectiveName = manifest?.Name ?? bnpInfo?.Name;
            var effectiveVersion = manifest?.Version ?? bnpInfo?.Version;

            // Infer from immediate sub-directories (skip BNP-internal dirs)
            if (category == SwitchModCategory.Unknown)
            {
                var subDirs = Directory.GetDirectories(folderPath).Select(Path.GetFileName);
                foreach (var sub in subDirs.OfType<string>())
                {
                    if (sub.Equals("options", StringComparison.OrdinalIgnoreCase) ||
                        sub.Equals("logs", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var cat = SwitchModCategoryExtensions.FromPathSegment(sub);
                    if (cat != SwitchModCategory.Unknown) { category = cat; break; }
                }
            }

            // TitleID from folder name
            if (titleId == null)
                titleId = TryExtractTitleIdFromName(Path.GetFileName(folderPath));

            // TitleID from parent folder name
            if (titleId == null)
                titleId = TryExtractTitleIdFromName(
                    Path.GetFileName(Path.GetDirectoryName(folderPath) ?? string.Empty));

            // TitleID from immediate TitleID-named subdirectory
            if (titleId == null)
            {
                var subDirs = Directory.GetDirectories(folderPath).Select(Path.GetFileName);
                foreach (var sub in subDirs.OfType<string>())
                {
                    if (SwitchTitleId.TryParse(sub, out _))
                    {
                        titleId = sub.ToUpperInvariant();
                        break;
                    }
                }
            }

            if (titleId == null)
                return null;

            var name = effectiveName ?? SanitiseName(Path.GetFileName(folderPath));
            var hash = await ComputeDirectoryHashAsync(folderPath, ct);

            var mod = BuildMod(
                titleId, name, effectiveVersion ?? "0.0.0", category,
                manifest?.LoadOrder ?? 0,
                manifest?.Dependencies ?? [],
                manifest?.Conflicts ?? [],
                folderPath, true, hash);

            if (bnpInfo?.Options != null && hasOptionsDir)
                mod.BnpOptions = bnpInfo.Options;

            return mod;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "Failed to inspect folder: {Path}", folderPath);
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsLikelySwitchModFolder(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;

        // A folder that IS already a well-known Yuzu subfolder — skip, it's a sub-component
        var lower = name.ToLowerInvariant();
        if (lower is "romfs" or "exefs" or "cheats") return false;

        var sub = Directory.GetDirectories(path).Select(d => Path.GetFileName(d)?.ToLowerInvariant());
        return sub.Any(s => s is "romfs" or "exefs" or "cheats" or "content")
               || File.Exists(Path.Combine(path, "manifest.json"))
               || File.Exists(Path.Combine(path, "mod.json"))
               || File.Exists(Path.Combine(path, "info.json"));
    }

    private static bool IsManifestEntry(string entryPath)
    {
        var name = Path.GetFileName(entryPath).ToLowerInvariant();
        return name is "manifest.json" or "mod.json" or "info.json";
    }

    private static async Task<SwitchModManifest?> TryReadManifestFromEntryAsync(
        IArchive archive, IArchiveEntry entry, CancellationToken ct)
    {
        try
        {
            await using var stream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            ms.Position = 0;
            return await System.Text.Json.JsonSerializer.DeserializeAsync<SwitchModManifest>(ms,
                cancellationToken: ct);
        }
        catch { return null; }
    }

    private static SwitchModCategory InferCategoryFromEntries(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            var seg = entry.Split('/').FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (seg == null) continue;
            var cat = SwitchModCategoryExtensions.FromPathSegment(seg);
            if (cat != SwitchModCategory.Unknown) return cat;
        }
        return SwitchModCategory.Content; // Default — flat content mod
    }

    private static string? TryExtractTitleIdFromName(string name)
    {
        var m = TitleIdInName.Match(name);
        if (!m.Success) return null;
        var candidate = m.Groups[1].Value;
        return SwitchTitleId.TryParse(candidate, out _) ? candidate.ToUpperInvariant() : null;
    }

    private static string? TryExtractTitleIdFromEntries(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            var first = entry.Split('/').FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (first != null && SwitchTitleId.TryParse(first, out _))
                return first.ToUpperInvariant();
        }
        return null;
    }

    private static bool IsBnpFile(string path) =>
        Path.GetExtension(path).Equals(".bnp", StringComparison.OrdinalIgnoreCase);

    private static bool IsRootLevelEntry(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        return !normalized.Contains('/');
    }

    /// <summary>
    /// Returns true for archive entries under options/ or logs/ (BNP-internal paths
    /// that should be excluded from TitleID and category inference).
    /// </summary>
    private static bool IsBnpInternalEntry(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("options/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("logs/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<BnpInfo?> TryReadBnpInfoFromEntryAsync(
        IArchiveEntry entry, CancellationToken ct)
    {
        try
        {
            await using var stream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            ms.Position = 0;
            return await System.Text.Json.JsonSerializer.DeserializeAsync<BnpInfo>(ms,
                cancellationToken: ct);
        }
        catch { return null; }
    }

    private static string SanitiseName(string raw) =>
        Regex.Replace(raw, @"[\-_]+", " ").Trim();

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var bytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<string> ComputeDirectoryHashAsync(string dir, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relBytes = System.Text.Encoding.UTF8.GetBytes(
                Path.GetRelativePath(dir, file).Replace('\\', '/'));
            sha.AppendData(relBytes);
            await using var fs = File.OpenRead(file);
            var buf = new byte[8192];
            int read;
            while ((read = await fs.ReadAsync(buf, ct)) > 0)
                sha.AppendData(buf.AsSpan(0, read));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static SwitchMod BuildMod(
        string titleId, string name, string version,
        SwitchModCategory category, int loadOrder,
        List<string> deps, List<string> conflicts,
        string sourcePath, bool isExtracted, string hash)
    {
        var key = $"{titleId}/{category}/{name}".Replace(" ", "_");
        var internalPath = $"Domain/Switch/{titleId}/{category}/{name}".Replace(" ", "_");

        return new SwitchMod
        {
            ModKey        = key,
            Name          = name,
            Version       = version,
            TitleId       = titleId,
            Category      = category,
            SourcePath    = sourcePath,
            IsExtracted   = isExtracted,
            SourceHash    = hash,
            Dependencies  = deps,
            Conflicts     = conflicts,
            LoadOrder     = loadOrder,
            InternalPath  = internalPath
        };
    }
}
