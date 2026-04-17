using Modular.Switch.Models;

namespace Modular.Switch.DependencyResolver;

/// <summary>
/// Directed dependency graph for Switch mods within a single TitleID.
/// Detects cycles, conflicts, and missing dependencies, then produces
/// a topologically-sorted install order via Kahn's algorithm.
/// </summary>
public sealed class SwitchDependencyGraph
{
    private readonly Dictionary<string, SwitchMod> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // ── Construction ──────────────────────────────────────────────────────

    public void Add(SwitchMod mod)
    {
        lock (_lock)
            _nodes[mod.ModKey] = mod;
    }

    public void AddRange(IEnumerable<SwitchMod> mods)
    {
        foreach (var m in mods) Add(m);
    }

    // ── Resolution ────────────────────────────────────────────────────────

    public SwitchResolutionResult Resolve(IEnumerable<string> requestedKeys)
    {
        lock (_lock)
        {
            var result = new SwitchResolutionResult();
            var requested = new HashSet<string>(requestedKeys, StringComparer.OrdinalIgnoreCase);

            // 1. Collect all reachable mods (BFS through dependency tree)
            var reachable = new Dictionary<string, SwitchMod>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(requested);

            while (queue.TryDequeue(out var key))
            {
                if (reachable.ContainsKey(key)) continue;

                if (!_nodes.TryGetValue(key, out var mod))
                {
                    result.MissingDependencies.Add(key);
                    continue;
                }

                reachable[key] = mod;
                foreach (var dep in mod.Dependencies)
                    queue.Enqueue(dep);
            }

            if (result.MissingDependencies.Count > 0)
            {
                result.Success = false;
                result.Error = $"Missing dependencies: {string.Join(", ", result.MissingDependencies)}";
                return result;
            }

            // 2. Conflict detection
            foreach (var mod in reachable.Values)
            {
                foreach (var conflict in mod.Conflicts)
                {
                    if (reachable.ContainsKey(conflict))
                    {
                        result.Conflicts.Add((mod.ModKey, conflict));
                    }
                }
            }

            if (result.Conflicts.Count > 0)
            {
                result.Success = false;
                result.Error = $"Conflicting mods: " +
                               string.Join(", ", result.Conflicts.Select(c => $"{c.A} ↔ {c.B}"));
                return result;
            }

            // 3. Topological sort (Kahn's algorithm)
            // Build in-degree map from dependency edges
            var inDegree = reachable.Keys.ToDictionary(k => k, _ => 0, StringComparer.OrdinalIgnoreCase);
            var adjacency = reachable.Keys.ToDictionary(
                k => k, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var mod in reachable.Values)
            {
                foreach (var dep in mod.Dependencies)
                {
                    if (!reachable.ContainsKey(dep)) continue;
                    // dep → mod (mod depends on dep, so dep comes first)
                    adjacency[dep].Add(mod.ModKey);
                    inDegree[mod.ModKey]++;
                }
            }

            var ready = new SortedSet<string>(
                inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key),
                // Secondary sort by explicit LoadOrder, then alphabetical for stability
                Comparer<string>.Create((a, b) =>
                {
                    var la = reachable[a].LoadOrder;
                    var lb = reachable[b].LoadOrder;
                    return la != lb ? la.CompareTo(lb) : string.Compare(a, b, StringComparison.Ordinal);
                }));

            var sorted = new List<SwitchMod>();

            while (ready.Count > 0)
            {
                var key = ready.Min!;
                ready.Remove(key);

                sorted.Add(reachable[key]);

                foreach (var neighbour in adjacency[key])
                {
                    inDegree[neighbour]--;
                    if (inDegree[neighbour] == 0)
                        ready.Add(neighbour);
                }
            }

            // Cycle detection
            if (sorted.Count != reachable.Count)
            {
                var cycleNodes = reachable.Keys.Except(sorted.Select(m => m.ModKey)).ToList();
                result.Cycles.AddRange(cycleNodes);
                result.Success = false;
                result.Error = $"Circular dependency detected among: {string.Join(", ", cycleNodes)}";
                return result;
            }

            result.InstallOrder = sorted;
            result.Success = true;
            return result;
        }
    }
}

/// <summary>Outcome of a dependency resolution pass.</summary>
public sealed class SwitchResolutionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>Mods in the order they should be installed (dependencies first).</summary>
    public List<SwitchMod> InstallOrder { get; set; } = [];

    public List<string> MissingDependencies { get; set; } = [];
    public List<(string A, string B)> Conflicts { get; set; } = [];
    public List<string> Cycles { get; set; } = [];
}
