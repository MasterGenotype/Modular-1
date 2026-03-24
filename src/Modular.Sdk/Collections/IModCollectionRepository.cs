namespace Modular.Sdk.Collections;

/// <summary>
/// Persistence contract for mod collections.
/// </summary>
public interface IModCollectionRepository
{
    /// <summary>Creates a new empty collection for the given game.</summary>
    Task<ModCollection> CreateAsync(string name, string gameId, CancellationToken ct = default);

    /// <summary>Loads a collection from a file path.</summary>
    Task<ModCollection?> LoadAsync(string path, CancellationToken ct = default);

    /// <summary>Saves a collection to a file path.</summary>
    Task SaveAsync(ModCollection collection, string path, CancellationToken ct = default);

    /// <summary>Lists all collections in the default collections directory.</summary>
    Task<List<ModCollection>> ListAsync(CancellationToken ct = default);
}
