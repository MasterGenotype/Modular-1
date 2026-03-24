using System.Text.Json;
using Modular.Core.Utilities;
using Modular.Sdk.Collections;

namespace Modular.Core.Collections;

/// <summary>
/// File-system backed collection repository.
/// Stores collections as JSON files in ~/.config/Modular/collections/.
/// </summary>
public class ModCollectionRepository : IModCollectionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DefaultCollectionsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Modular", "collections");

    private readonly string _collectionsDir;

    public ModCollectionRepository(string? collectionsDir = null)
    {
        _collectionsDir = collectionsDir ?? DefaultCollectionsDirectory;
    }

    public Task<ModCollection> CreateAsync(string name, string gameId, CancellationToken ct = default)
    {
        var collection = new ModCollection { Name = name, GameId = gameId };
        return Task.FromResult(collection);
    }

    public async Task<ModCollection?> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<ModCollection>(json, JsonOptions);
    }

    public async Task SaveAsync(ModCollection collection, string path, CancellationToken ct = default)
    {
        collection.UpdatedAt = DateTime.UtcNow;
        FileUtils.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(collection, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    public async Task<List<ModCollection>> ListAsync(CancellationToken ct = default)
    {
        var collections = new List<ModCollection>();
        if (!Directory.Exists(_collectionsDir))
            return collections;

        foreach (var file in Directory.GetFiles(_collectionsDir, "*.collection.json"))
        {
            ct.ThrowIfCancellationRequested();
            var collection = await LoadAsync(file, ct);
            if (collection != null)
                collections.Add(collection);
        }

        return collections;
    }

    /// <summary>
    /// Gets the default file path for a collection by name.
    /// </summary>
    public string GetCollectionPath(string name)
    {
        var safeName = FileUtils.SanitizeFilename(name);
        return Path.Combine(_collectionsDir, $"{safeName}.collection.json");
    }

    /// <summary>
    /// Finds a collection by name (case-insensitive).
    /// </summary>
    public async Task<(ModCollection? collection, string? path)> FindByNameAsync(string name, CancellationToken ct = default)
    {
        if (!Directory.Exists(_collectionsDir))
            return (null, null);

        foreach (var file in Directory.GetFiles(_collectionsDir, "*.collection.json"))
        {
            ct.ThrowIfCancellationRequested();
            var collection = await LoadAsync(file, ct);
            if (collection != null && collection.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (collection, file);
        }
        return (null, null);
    }
}
