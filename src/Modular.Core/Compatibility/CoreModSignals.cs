using System.Text.Json;
using System.Text.RegularExpressions;
using Modular.Sdk.Backends;
using Modular.Sdk.Compatibility;

namespace Modular.Core.Compatibility;

/// <summary>
/// Finds mods that other installed mods list as requirements on their hub pages.
/// </summary>
/// <remarks>
/// This is the most direct sign of a framework, but only as complete as the authors made
/// it: a mod whose page lists nothing hides what it relies on.
/// </remarks>
public sealed class RequiredByInstalledSignal : ICoreModSignal
{
    private readonly IReadOnlyList<IModBackend> _backends;

    /// <summary>Creates the signal.</summary>
    /// <param name="backends">Backends to ask; those that cannot list requirements are skipped.</param>
    public RequiredByInstalledSignal(IEnumerable<IModBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.ToList();
    }

    /// <inheritdoc />
    public string Id => "required-by";

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoreModEvidence>> EvaluateAsync(CoreModContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var byKey = context.Mods.Where(m => m.IsTraced)
            .GroupBy(m => InstalledMod.HubKey(m.BackendId!, m.GameDomain, m.ModId!))
            .ToDictionary(g => g.Key, g => g.First());

        // Required mod key -> names of the installed mods that list it.
        var dependents = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var group in byKey.Values.GroupBy(m => (
                     Backend: m.BackendId!.ToLowerInvariant(),
                     Domain: m.GameDomain?.ToLowerInvariant())))
        {
            ct.ThrowIfCancellationRequested();

            var backend = _backends.FirstOrDefault(b =>
                string.Equals(b.Id, group.Key.Backend, StringComparison.OrdinalIgnoreCase));
            if (backend is not IRequirementsBackend requirements)
                continue;

            var mods = group.ToList();
            var fetched = await requirements.GetRequirementsAsync(
                mods.Select(m => m.ModId!).Distinct().ToList(), mods[0].GameDomain, ct);

            foreach (var mod in mods)
            {
                if (!fetched.TryGetValue(mod.ModId!, out var listed))
                    continue;

                foreach (var requirement in listed)
                {
                    if (requirement.IsExternal || !requirement.SameGame || string.IsNullOrEmpty(requirement.ModId))
                        continue;

                    var required = InstalledMod.HubKey(mod.BackendId!, mod.GameDomain, requirement.ModId);
                    if (required == InstalledMod.HubKey(mod.BackendId!, mod.GameDomain, mod.ModId!))
                        continue;

                    if (!dependents.TryGetValue(required, out var names))
                        dependents[required] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                    names.Add(mod.Name);
                }
            }
        }

        // Only requirements that are installed are core mods of this game; the rest are
        // missing dependencies, which MissingDependencyService reports.
        return dependents
            .Where(d => byKey.ContainsKey(d.Key))
            .Select(d => new CoreModEvidence(
                byKey[d.Key].Key,
                CoreModRole.Dependency,
                Id,
                d.Value.Count == 1
                    ? $"Required by {d.Value.First()}"
                    : $"Required by {d.Value.Count} installed mods")
            {
                Dependents = d.Value.ToList()
            })
            .ToList();
    }
}

/// <summary>
/// Finds mods by files that mark a loader or native code, from the game module's rules
/// plus any given here.
/// </summary>
public sealed class GameFileRuleSignal : ICoreModSignal
{
    private readonly IReadOnlyList<CoreModRule> _extraRules;

    /// <summary>Creates the signal.</summary>
    /// <param name="extraRules">Rules to apply on top of the game module's own.</param>
    public GameFileRuleSignal(IEnumerable<CoreModRule>? extraRules = null)
    {
        _extraRules = extraRules?.ToList() ?? [];
    }

    /// <inheritdoc />
    public string Id => "game-files";

    /// <inheritdoc />
    public Task<IReadOnlyList<CoreModEvidence>> EvaluateAsync(CoreModContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rules = (context.Module?.GetCoreModRules() ?? []).Concat(_extraRules)
            .Select(r => (Rule: r, Pattern: PathPattern.Compile(r.PathPattern)))
            .ToList();

        var evidence = new List<CoreModEvidence>();
        foreach (var mod in context.Mods)
        {
            ct.ThrowIfCancellationRequested();

            // One piece of evidence per rule, however many of the mod's files it matches.
            foreach (var (rule, pattern) in rules)
            {
                var file = mod.Files.FirstOrDefault(f => pattern.IsMatch(f.Replace('\\', '/')));
                if (file != null)
                    evidence.Add(new CoreModEvidence(mod.Key, rule.Role, Id, $"{rule.Reason} ({file})"));
            }
        }

        return Task.FromResult<IReadOnlyList<CoreModEvidence>>(evidence);
    }
}

/// <summary>
/// Marks mods named in a list kept by hand: frameworks known by reputation that nothing
/// installed happens to list, or that the game files do not give away.
/// </summary>
/// <remarks>
/// The list is a JSON file keyed by game, each entry naming a mod by hub id or by name:
/// <code>
/// { "cyberpunk2077": [ { "modId": "107", "reason": "Script loader" }, { "name": "Virtual Atelier" } ] }
/// </code>
/// A game is looked up by its key, then by its hub domain. A missing file means an
/// empty list.
/// </remarks>
public sealed class DeclaredCoreModsSignal : ICoreModSignal
{
    private readonly string _path;

    /// <summary>Creates the signal.</summary>
    /// <param name="path">The list's file.</param>
    public DeclaredCoreModsSignal(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <inheritdoc />
    public string Id => "declared";

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoreModEvidence>> EvaluateAsync(CoreModContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(_path))
            return [];

        Dictionary<string, List<Entry>>? lists;
        await using (var stream = File.OpenRead(_path))
        {
            lists = await JsonSerializer.DeserializeAsync<Dictionary<string, List<Entry>>>(
                stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                }, ct);
        }

        lists = lists == null ? [] : new Dictionary<string, List<Entry>>(lists, StringComparer.OrdinalIgnoreCase);

        var keys = new[] { context.GameKey }
            .Concat(context.Mods.Select(m => m.GameDomain))
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var entries = keys.SelectMany(k => lists.TryGetValue(k!, out var found) ? found : []).ToList();

        var evidence = new List<CoreModEvidence>();
        foreach (var entry in entries)
        {
            var mod = context.Mods.FirstOrDefault(m =>
                (!string.IsNullOrEmpty(entry.ModId) &&
                 string.Equals(m.ModId, entry.ModId, StringComparison.OrdinalIgnoreCase) &&
                 (entry.Backend == null || string.Equals(m.BackendId, entry.Backend, StringComparison.OrdinalIgnoreCase))) ||
                (!string.IsNullOrEmpty(entry.Name) &&
                 string.Equals(m.Name, entry.Name, StringComparison.OrdinalIgnoreCase)));

            if (mod != null)
            {
                evidence.Add(new CoreModEvidence(
                    mod.Key, entry.Role ?? CoreModRole.Declared, Id, entry.Reason ?? "On the core mod list"));
            }
        }

        return evidence;
    }

    private sealed record Entry(string? ModId, string? Backend, string? Name, string? Reason, CoreModRole? Role);
}

/// <summary>
/// Matches game-relative paths against patterns with <c>*</c> and <c>**</c>.
/// </summary>
internal static class PathPattern
{
    /// <summary>Turns a pattern into a regex that matches whole paths, without regard to case.</summary>
    public static Regex Compile(string pattern)
    {
        var normalized = pattern.Replace('\\', '/').TrimStart('/');
        var body = Regex.Escape(normalized)
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");

        return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
