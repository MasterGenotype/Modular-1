using Microsoft.Extensions.Logging;
using Modular.Sdk.Compatibility;

namespace Modular.Core.Compatibility;

/// <summary>
/// One core mod and every reason found for it.
/// </summary>
/// <param name="Mod">The mod.</param>
/// <param name="Evidence">The reasons, from every signal that found it.</param>
public sealed record CoreModEntry(InstalledMod Mod, IReadOnlyList<CoreModEvidence> Evidence)
{
    /// <summary>The distinct roles the evidence gives the mod.</summary>
    public IReadOnlyList<CoreModRole> Roles => Evidence.Select(e => e.Role).Distinct().Order().ToList();

    /// <summary>Installed mods that depend on this one, from every signal that named any.</summary>
    public IReadOnlyList<string> Dependents => Evidence
        .SelectMany(e => e.Dependents)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// The core mods of one game.
/// </summary>
public sealed record CoreModReport
{
    /// <summary>Core mods, most depended on first.</summary>
    public IReadOnlyList<CoreModEntry> Entries { get; init; } = [];

    /// <summary>How many mods were looked at.</summary>
    public int ModsChecked { get; init; }

    /// <summary>Signals that failed, as messages fit to show. The others still count.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Works out which installed mods are core: the ones other mods, or the game itself,
/// rely on, and which therefore have to move first when the game updates.
/// </summary>
/// <remarks>
/// Each <see cref="ICoreModSignal"/> covers one kind of evidence; adding a kind means
/// adding a signal. A mod is core when any signal says so, and the report keeps every
/// reason so a person can judge a weak one.
/// </remarks>
public sealed class CoreModClassifier
{
    private readonly IReadOnlyList<ICoreModSignal> _signals;
    private readonly ILogger? _logger;

    /// <summary>Creates the classifier.</summary>
    /// <param name="signals">The signals to consult.</param>
    /// <param name="logger">Optional logger.</param>
    public CoreModClassifier(IEnumerable<ICoreModSignal> signals, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals.ToList();
        _logger = logger;
    }

    /// <summary>Finds the core mods among the given ones.</summary>
    public async Task<CoreModReport> ClassifyAsync(CoreModContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evidence = new List<CoreModEvidence>();
        var errors = new List<string>();

        foreach (var signal in _signals)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                evidence.AddRange(await signal.EvaluateAsync(context, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Core mod signal {Signal} failed", signal.Id);
                errors.Add($"{signal.Id}: {ex.Message}");
            }
        }

        var mods = context.Mods
            .GroupBy(m => m.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var entries = evidence
            .Where(e => mods.ContainsKey(e.ModKey))
            .GroupBy(e => e.ModKey, StringComparer.Ordinal)
            .Select(g => new CoreModEntry(mods[g.Key], g.ToList()))
            .OrderByDescending(e => e.Dependents.Count)
            .ThenBy(e => e.Roles.Min())
            .ThenBy(e => e.Mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CoreModReport { Entries = entries, ModsChecked = context.Mods.Count, Errors = errors };
    }
}
