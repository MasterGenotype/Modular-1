using Microsoft.Extensions.Logging;
using Modular.Core.Versioning;

namespace Modular.Core.Installers.Steam;

/// <summary>
/// Constraint solver for Steam mod dependencies. Resolves a set of mods into a valid
/// topological install order, respecting version constraints, detecting cycles,
/// and handling optional/conditional dependencies.
/// </summary>
public class SteamConstraintSolver
{
    private readonly ILogger<SteamConstraintSolver>? _logger;

    public SteamConstraintSolver(ILogger<SteamConstraintSolver>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves the install order for a set of mods, satisfying all dependency and version constraints.
    /// </summary>
    /// <param name="mods">All available mods to consider.</param>
    /// <param name="activeConditions">Active conditions for conditional dependencies (e.g., "linux").</param>
    /// <returns>A result containing the ordered install list or error details.</returns>
    public SteamResolutionResult Resolve(
        IReadOnlyList<SteamModMetadata> mods,
        IReadOnlySet<string>? activeConditions = null)
    {
        var result = new SteamResolutionResult();
        var modsByName = new Dictionary<string, SteamModMetadata>(StringComparer.OrdinalIgnoreCase);
        activeConditions ??= new HashSet<string>();

        // Index mods by name, detecting duplicates
        foreach (var mod in mods)
        {
            if (modsByName.ContainsKey(mod.Name))
            {
                result.Errors.Add($"Duplicate mod name: '{mod.Name}'");
                return result;
            }
            modsByName[mod.Name] = mod;
        }

        // Validate all required dependencies exist and version constraints are satisfiable
        foreach (var mod in mods)
        {
            foreach (var dep in mod.Dependencies)
            {
                // Skip conditional dependencies whose condition is not active
                if (!string.IsNullOrEmpty(dep.Condition) && !activeConditions.Contains(dep.Condition))
                {
                    _logger?.LogDebug(
                        "Skipping conditional dependency {Mod} -> {Dep} (condition '{Condition}' not active)",
                        mod.Name, dep.ModName, dep.Condition);
                    continue;
                }

                if (!modsByName.TryGetValue(dep.ModName, out var depMod))
                {
                    if (dep.IsOptional)
                    {
                        _logger?.LogInformation(
                            "Optional dependency '{Dep}' for '{Mod}' not found, skipping",
                            dep.ModName, mod.Name);
                        result.Warnings.Add($"Optional dependency '{dep.ModName}' for '{mod.Name}' not available");
                        continue;
                    }

                    result.Errors.Add(
                        $"Missing required dependency: '{mod.Name}' requires '{dep.ModName}' which is not available");
                    return result;
                }

                // Validate version constraint
                if (!string.IsNullOrEmpty(dep.VersionConstraint))
                {
                    if (!VersionRange.TryParse(dep.VersionConstraint, out var range))
                    {
                        result.Errors.Add(
                            $"Invalid version constraint: '{mod.Name}' requires '{dep.ModName}' " +
                            $"with constraint '{dep.VersionConstraint}' which cannot be parsed");
                        return result;
                    }

                    if (!SemanticVersion.TryParse(depMod.Version, out var depVersion))
                    {
                        result.Errors.Add(
                            $"Invalid version: '{dep.ModName}' has unparseable version '{depMod.Version}'");
                        return result;
                    }

                    if (!range!.IsSatisfiedBy(depVersion!))
                    {
                        result.Errors.Add(
                            $"Version conflict: '{mod.Name}' requires '{dep.ModName}' {dep.VersionConstraint}, " +
                            $"but only version {depMod.Version} is available");
                        return result;
                    }
                }
            }
        }

        // Build adjacency list for topological sort (edges from mod to its dependencies)
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            adjacency[mod.Name] = new List<string>();
            foreach (var dep in mod.Dependencies)
            {
                if (!string.IsNullOrEmpty(dep.Condition) && !activeConditions.Contains(dep.Condition))
                    continue;
                if (dep.IsOptional && !modsByName.ContainsKey(dep.ModName))
                    continue;
                if (!modsByName.ContainsKey(dep.ModName))
                    continue;

                adjacency[mod.Name].Add(dep.ModName);
            }
        }

        // Detect cycles using DFS with three-color marking
        var cycleResult = DetectCycles(adjacency, mods);
        if (cycleResult != null)
        {
            result.Errors.Add($"Circular dependency detected: {cycleResult}");
            return result;
        }

        // Topological sort using Kahn's algorithm (produces dependencies-first order)
        var installOrder = TopologicalSort(adjacency, mods);
        if (installOrder == null)
        {
            result.Errors.Add("Unable to determine install order (possible undetected cycle)");
            return result;
        }

        result.InstallOrder = installOrder;
        result.Success = true;

        _logger?.LogInformation(
            "Resolution successful: {Count} mods in order: {Order}",
            installOrder.Count,
            string.Join(" -> ", installOrder.Select(m => m.Name)));

        return result;
    }

    /// <summary>
    /// Detects cycles in the dependency graph using iterative DFS with three-color marking.
    /// Returns a human-readable cycle description, or null if no cycles exist.
    /// </summary>
    private string? DetectCycles(
        Dictionary<string, List<string>> adjacency,
        IReadOnlyList<SteamModMetadata> mods)
    {
        // 0 = white (unvisited), 1 = gray (in progress), 2 = black (done)
        var color = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            color[mod.Name] = 0;
            parent[mod.Name] = null;
        }

        foreach (var mod in mods)
        {
            if (color[mod.Name] != 0)
                continue;

            // Iterative DFS
            var stack = new Stack<(string node, int neighborIndex)>();
            color[mod.Name] = 1;
            stack.Push((mod.Name, 0));

            while (stack.Count > 0)
            {
                var (current, idx) = stack.Pop();
                var neighbors = adjacency.GetValueOrDefault(current, new List<string>());

                if (idx < neighbors.Count)
                {
                    // Re-push current with next neighbor index
                    stack.Push((current, idx + 1));

                    var neighbor = neighbors[idx];
                    if (color.GetValueOrDefault(neighbor) == 1)
                    {
                        // Found a cycle: reconstruct the path
                        var cycle = new List<string> { neighbor, current };
                        foreach (var (stackNode, _) in stack)
                        {
                            cycle.Add(stackNode);
                            if (string.Equals(stackNode, neighbor, StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                        cycle.Reverse();
                        return string.Join(" -> ", cycle);
                    }

                    if (color.GetValueOrDefault(neighbor) == 0)
                    {
                        color[neighbor] = 1;
                        parent[neighbor] = current;
                        stack.Push((neighbor, 0));
                    }
                }
                else
                {
                    color[current] = 2;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Performs Kahn's topological sort, producing an install order where
    /// dependencies come before the mods that depend on them.
    /// </summary>
    private List<SteamModMetadata>? TopologicalSort(
        Dictionary<string, List<string>> adjacency,
        IReadOnlyList<SteamModMetadata> mods)
    {
        var modsByName = mods.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        // Compute in-degrees
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
            inDegree[mod.Name] = 0;

        foreach (var (_, deps) in adjacency)
        {
            foreach (var dep in deps)
            {
                if (inDegree.ContainsKey(dep))
                    inDegree[dep]++;
            }
        }

        // NOTE: In our adjacency list, edges go FROM a mod TO its dependencies.
        // Kahn's algorithm processes nodes with in-degree 0 first. Since
        // dependencies point inward (toward the dependency), nodes with 0
        // in-degree are "leaves" — mods that nothing depends on.
        // We need the REVERSE: dependencies installed first.
        // So we reverse the adjacency: edges from dependency -> dependent.
        var reverseAdj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
            reverseAdj[mod.Name] = new List<string>();

        foreach (var (modName, deps) in adjacency)
        {
            foreach (var dep in deps)
            {
                if (reverseAdj.ContainsKey(dep))
                    reverseAdj[dep].Add(modName);
            }
        }

        // Recompute in-degrees on reversed graph
        var revInDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
            revInDegree[mod.Name] = 0;

        foreach (var (_, dependents) in reverseAdj)
        {
            foreach (var dependent in dependents)
            {
                if (revInDegree.ContainsKey(dependent))
                    revInDegree[dependent]++;
            }
        }

        // Kahn's on reversed graph: nodes with 0 in-degree are dependencies
        var queue = new Queue<string>();
        foreach (var (name, degree) in revInDegree)
        {
            if (degree == 0)
                queue.Enqueue(name);
        }

        var sorted = new List<SteamModMetadata>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(modsByName[current]);

            foreach (var dependent in reverseAdj[current])
            {
                revInDegree[dependent]--;
                if (revInDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted.Count != mods.Count)
            return null; // Cycle detected (shouldn't happen if DetectCycles passed)

        return sorted;
    }
}

/// <summary>
/// Result of Steam mod dependency resolution.
/// </summary>
public class SteamResolutionResult
{
    /// <summary>
    /// Whether resolution was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Topologically sorted install order (dependencies first).
    /// </summary>
    public List<SteamModMetadata> InstallOrder { get; set; } = new();

    /// <summary>
    /// Error messages if resolution failed.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Non-fatal warnings (e.g., missing optional dependencies).
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
