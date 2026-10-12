using Microsoft.Extensions.Logging;
using Modular.Core.Database;
using Modular.Core.Metadata;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// Answers mod categories from the NexusMods API, through the shared metadata cache.
/// </summary>
public sealed class NexusModCategorySource : IModCategorySource
{
    private readonly NexusModsBackend _backend;
    private readonly ModMetadataCache _cache;
    private readonly ILogger? _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="backend">Backend used to ask NexusMods.</param>
    /// <param name="cache">Cache the answers are kept in.</param>
    /// <param name="logger">Optional logger.</param>
    public NexusModCategorySource(
        NexusModsBackend backend, ModMetadataCache cache, ILogger? logger = null)
    {
        _backend = backend;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? GetCategory(string gameDomain, string modId)
    {
        if (string.IsNullOrEmpty(gameDomain) || !int.TryParse(modId, out var id))
            return null;

        var categoryId = _cache.GetModMetadata(gameDomain, id)?.CategoryId ?? 0;
        if (categoryId <= 0)
            return null;

        return _cache.GetGameCategories(gameDomain)?.GetValueOrDefault(categoryId);
    }

    /// <inheritdoc />
    public async Task WarmAsync(
        string gameDomain, IReadOnlyList<string> modIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        if (string.IsNullOrEmpty(gameDomain) || _backend.ValidateConfiguration().Count > 0)
            return;

        var changed = false;

        try
        {
            if (_cache.GetGameCategories(gameDomain) == null)
            {
                _cache.SetGameCategories(
                    gameDomain, await _backend.GetGameCategoriesAsync(gameDomain, ct));
                changed = true;
            }

            foreach (var modId in modIds.Distinct())
            {
                ct.ThrowIfCancellationRequested();

                if (!int.TryParse(modId, out var id) ||
                    _cache.GetModMetadata(gameDomain, id) is { CategoryId: > 0 })
                {
                    continue;
                }

                var info = await _backend.GetModInfoAsync(modId, gameDomain, ct);
                if (info?.CategoryId is not > 0)
                    continue;

                _cache.SetModMetadata(gameDomain, new ModMetadata
                {
                    ModId = id,
                    Name = info.Name,
                    CategoryId = info.CategoryId.Value,
                    FetchedAt = DateTime.UtcNow
                });
                changed = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Category lookup failed for {Domain}", gameDomain);
        }

        if (changed)
            await _cache.SaveAsync();
    }
}
