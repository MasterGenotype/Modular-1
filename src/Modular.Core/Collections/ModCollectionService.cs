using Microsoft.Extensions.Logging;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Sdk.Collections;

namespace Modular.Core.Collections;

/// <summary>
/// Core service for managing mod collections: CRUD, add/remove mods,
/// bulk download, verification, import/export, and update checking.
/// </summary>
public class ModCollectionService
{
    private readonly ModCollectionRepository _repository;
    private readonly IModBackend _backend;
    private readonly ILogger<ModCollectionService>? _logger;

    public ModCollectionService(
        ModCollectionRepository repository,
        IModBackend backend,
        ILogger<ModCollectionService>? logger = null)
    {
        _repository = repository;
        _backend = backend;
        _logger = logger;
    }

    public async Task<ModCollection> CreateAsync(string name, string gameId, CancellationToken ct = default)
    {
        var collection = await _repository.CreateAsync(name, gameId, ct);
        var path = _repository.GetCollectionPath(name);
        await _repository.SaveAsync(collection, path, ct);
        _logger?.LogInformation("Created collection '{Name}' for {GameId}", name, gameId);
        return collection;
    }

    public Task<List<ModCollection>> ListAsync(CancellationToken ct = default)
    {
        return _repository.ListAsync(ct);
    }

    public async Task<ModCollection?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var (collection, _) = await _repository.FindByNameAsync(name, ct);
        return collection;
    }

    public async Task AddModAsync(ModCollection collection, BackendMod mod, string? fileId = null, bool isOptional = false, CancellationToken ct = default)
    {
        if (collection.Entries.Any(e => e.ModId == mod.ModId))
        {
            _logger?.LogWarning("Mod {ModId} is already in collection '{Name}'", mod.ModId, collection.Name);
            return;
        }

        BackendModFile? pinnedFile = null;
        if (fileId != null)
        {
            var files = await _backend.GetModFilesAsync(mod.ModId, collection.GameId, ct: ct);
            pinnedFile = files.FirstOrDefault(f => f.FileId == fileId);
        }

        var entry = new ModCollectionEntry
        {
            ModId = mod.ModId,
            Name = mod.Name,
            Author = mod.Author,
            Version = mod.Version ?? pinnedFile?.Version,
            FileId = fileId ?? pinnedFile?.FileId,
            FileName = pinnedFile?.FileName,
            FileSizeBytes = pinnedFile?.SizeBytes,
            Md5 = pinnedFile?.Md5,
            Url = mod.Url,
            IsOptional = isOptional
        };

        collection.Entries.Add(entry);
        await SaveCollectionAsync(collection, ct);
        _logger?.LogInformation("Added mod '{ModName}' to collection '{CollectionName}'", mod.Name, collection.Name);
    }

    public async Task RemoveModAsync(ModCollection collection, string modId, CancellationToken ct = default)
    {
        var removed = collection.Entries.RemoveAll(e => e.ModId == modId);
        if (removed > 0)
        {
            await SaveCollectionAsync(collection, ct);
            _logger?.LogInformation("Removed mod {ModId} from collection '{Name}'", modId, collection.Name);
        }
    }

    public async Task DownloadCollectionAsync(
        ModCollection collection,
        string outputDirectory,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= DownloadOptions.Default;
        var entries = options.IncludeOptional
            ? collection.Entries
            : collection.Entries.Where(e => !e.IsOptional).ToList();

        var total = entries.Count;
        var completed = 0;

        progress?.Report(DownloadProgress.Scanning($"Downloading collection '{collection.Name}' ({total} mods)..."));

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            // Resolve file ID if not pinned
            var fileId = entry.FileId;
            if (string.IsNullOrEmpty(fileId))
            {
                var files = await _backend.GetModFilesAsync(entry.ModId, collection.GameId, ct: ct);
                var latestFile = files.OrderByDescending(f => f.UploadedAt).FirstOrDefault();
                if (latestFile == null)
                {
                    _logger?.LogWarning("No files found for mod {ModId} ({Name}), skipping", entry.ModId, entry.Name);
                    completed++;
                    continue;
                }
                fileId = latestFile.FileId;
            }

            var url = await _backend.ResolveDownloadUrlAsync(entry.ModId, fileId, collection.GameId, ct);
            if (string.IsNullOrEmpty(url))
            {
                _logger?.LogWarning("Could not resolve download URL for mod {ModId} file {FileId}", entry.ModId, fileId);
                completed++;
                continue;
            }

            if (options.DryRun)
            {
                completed++;
                progress?.Report(DownloadProgress.Downloading(
                    $"[DRY RUN] {entry.Name}", completed, total, entry.FileName ?? entry.Name));
                continue;
            }

            var fileName = entry.FileName ?? $"{entry.ModId}_{fileId}";
            var modDir = Path.Combine(outputDirectory, collection.GameId, entry.ModId);
            var outputPath = Path.Combine(modDir, FileUtils.SanitizeFilename(fileName));

            try
            {
                FileUtils.EnsureDirectoryExists(modDir);
                // Use a simple HttpClient download since we have the resolved URL
                using var httpClient = new HttpClient();
                var data = await httpClient.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(outputPath, data, ct);

                // Verify MD5 if available
                if (options.VerifyDownloads && !string.IsNullOrEmpty(entry.Md5))
                {
                    var actualMd5 = await Md5Calculator.CalculateMd5Async(outputPath, ct);
                    if (!actualMd5.Equals(entry.Md5, StringComparison.OrdinalIgnoreCase))
                        _logger?.LogWarning("MD5 mismatch for {FileName}: expected {Expected}, got {Actual}",
                            fileName, entry.Md5, actualMd5);
                }

                _logger?.LogInformation("Downloaded: {FileName}", fileName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download mod {ModId} ({Name})", entry.ModId, entry.Name);
            }

            completed++;
            progress?.Report(DownloadProgress.Downloading(
                $"Downloaded {entry.Name}", completed, total, fileName));
        }

        progress?.Report(DownloadProgress.Done(total));
    }

    public async Task<List<(ModCollectionEntry entry, bool exists, bool md5Match)>> VerifyCollectionAsync(
        ModCollection collection,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var results = new List<(ModCollectionEntry entry, bool exists, bool md5Match)>();

        foreach (var entry in collection.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = entry.FileName ?? $"{entry.ModId}_{entry.FileId}";
            var filePath = Path.Combine(outputDirectory, collection.GameId, entry.ModId,
                FileUtils.SanitizeFilename(fileName));

            var exists = File.Exists(filePath);
            var md5Match = false;

            if (exists && !string.IsNullOrEmpty(entry.Md5))
            {
                var actualMd5 = await Md5Calculator.CalculateMd5Async(filePath, ct);
                md5Match = actualMd5.Equals(entry.Md5, StringComparison.OrdinalIgnoreCase);
            }
            else if (exists)
            {
                md5Match = true; // No MD5 to check against
            }

            results.Add((entry, exists, md5Match));
        }

        return results;
    }

    public async Task ExportAsync(ModCollection collection, string outputPath, CancellationToken ct = default)
    {
        await _repository.SaveAsync(collection, outputPath, ct);
        _logger?.LogInformation("Exported collection '{Name}' to {Path}", collection.Name, outputPath);
    }

    public async Task<ModCollection?> ImportAsync(string inputPath, CancellationToken ct = default)
    {
        var collection = await _repository.LoadAsync(inputPath, ct);
        if (collection == null)
        {
            _logger?.LogWarning("Could not load collection from {Path}", inputPath);
            return null;
        }

        var destPath = _repository.GetCollectionPath(collection.Name);
        await _repository.SaveAsync(collection, destPath, ct);
        _logger?.LogInformation("Imported collection '{Name}' from {Path}", collection.Name, inputPath);
        return collection;
    }

    public async Task<List<(ModCollectionEntry entry, string? latestFileId, string? latestVersion)>> CheckUpdatesAsync(
        ModCollection collection,
        CancellationToken ct = default)
    {
        var updates = new List<(ModCollectionEntry entry, string? latestFileId, string? latestVersion)>();

        foreach (var entry in collection.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var files = await _backend.GetModFilesAsync(entry.ModId, collection.GameId, ct: ct);
                var latest = files
                    .Where(f => f.Category?.ToLowerInvariant() == "main")
                    .OrderByDescending(f => f.UploadedAt)
                    .FirstOrDefault() ?? files.OrderByDescending(f => f.UploadedAt).FirstOrDefault();

                if (latest != null && latest.FileId != entry.FileId)
                {
                    updates.Add((entry, latest.FileId, latest.Version));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not check updates for mod {ModId}", entry.ModId);
            }
        }

        return updates;
    }

    private async Task SaveCollectionAsync(ModCollection collection, CancellationToken ct)
    {
        var (_, existingPath) = await _repository.FindByNameAsync(collection.Name, ct);
        var path = existingPath ?? _repository.GetCollectionPath(collection.Name);
        await _repository.SaveAsync(collection, path, ct);
    }
}

