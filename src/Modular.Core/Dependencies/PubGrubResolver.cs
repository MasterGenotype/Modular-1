using Microsoft.Extensions.Logging;
using Modular.Core.Metadata;
using Modular.Core.Versioning;

namespace Modular.Core.Dependencies;

/// <summary>
/// Dependency resolver using a simplified PubGrub-inspired algorithm.
/// Resolves mod dependencies to a consistent set of versions.
/// </summary>
public class PubGrubResolver
{
    private readonly IModVersionProvider _versionProvider;
    private readonly ILogger<PubGrubResolver>? _logger;

    public PubGrubResolver(
        IModVersionProvider versionProvider,
        ILogger<PubGrubResolver>? logger = null)
    {
        _versionProvider = versionProvider;
        _logger = logger;
    }

    /// <summary>
    /// Resolves dependencies for a set of root mods.
    /// </summary>
    public async Task<ResolutionResult> ResolveAsync(
        List<(string canonicalId, VersionRange? constraint)> rootRequirements,
        CancellationToken ct = default)
    {
        var result = new ResolutionResult();
        var graph = new DependencyGraph();

        // Track selected versions and constraints
        var selectedVersions = new Dictionary<string, SemanticVersion>();
        var constraints = new Dictionary<string, List<VersionConstraintSource>>();

        try
        {
            // Initialize with root requirements
            foreach (var (canonicalId, constraint) in rootRequirements)
            {
                if (!constraints.ContainsKey(canonicalId))
                    constraints[canonicalId] = new();

                constraints[canonicalId].Add(new VersionConstraintSource
                {
                    SourceMod = "<root>",
                    Constraint = constraint,
                    DependencyType = DependencyType.Required
                });
            }

            // Iteratively select versions and propagate constraints
            var unresolved = new Queue<string>(constraints.Keys);
            var visited = new HashSet<string>();

            while (unresolved.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var modId = unresolved.Dequeue();
                if (visited.Contains(modId))
                    continue;

                visited.Add(modId);

                // Get available versions
                var availableVersions = await _versionProvider.GetAvailableVersionsAsync(modId, ct);
                if (availableVersions.Count == 0)
                {
                    result.Conflicts.Add(new ResolutionConflict
                    {
                        CanonicalId = modId,
                        Type = ConflictType.NoVersionsAvailable,
                        Explanation = $"No versions available for {modId}"
                    });
                    result.Success = false;
                    result.FailureReason = $"No versions available for {modId}";
                    return result;
                }

                // Find a version satisfying all constraints
                var modConstraints = constraints[modId];
                var selectedVersion = SelectVersion(availableVersions, modConstraints);

                if (selectedVersion == null)
                {
                    // Conflict: no version satisfies all constraints
                    result.Conflicts.Add(new ResolutionConflict
                    {
                        CanonicalId = modId,
                        Type = ConflictType.NoSatisfyingVersion,
                        Constraints = modConstraints,
                        Explanation = GenerateConflictExplanation(modId, modConstraints)
                    });
                    result.Success = false;
                    result.FailureReason = GenerateConflictExplanation(modId, modConstraints);
                    return result;
                }

                selectedVersions[modId] = selectedVersion;
                _logger?.LogDebug("Selected {ModId}@{Version}", modId, selectedVersion);

                // Add node to graph
                var node = new ModNode(modId, selectedVersion);
                graph.AddNode(node);

                // Get dependencies for selected version
                var dependencies = await _versionProvider.GetDependenciesAsync(modId, selectedVersion, ct);

                // Propagate constraints from dependencies
                foreach (var dep in dependencies)
                {
                    // Skip optional dependencies for now (can be added later)
                    if (dep.Type == DependencyType.Optional)
                        continue;

                    // Handle incompatible dependencies
                    if (dep.Type == DependencyType.Incompatible)
                    {
                        var targetId = GetCanonicalId(dep.Target);
                        if (selectedVersions.ContainsKey(targetId))
                        {
                            var incompatibleVersion = selectedVersions[targetId];
                            var constraint = ParseVersionRange(dep.Constraint);
                            if (constraint == null || constraint.IsSatisfiedBy(incompatibleVersion))
                            {
                                // Conflict: incompatible mods both selected
                                result.Conflicts.Add(new ResolutionConflict
                                {
                                    CanonicalId = targetId,
                                    Type = ConflictType.IncompatibleMods,
                                    Explanation = $"{modId}@{selectedVersion} is incompatible with {targetId}@{incompatibleVersion}"
                                });
                                result.Success = false;
                                result.FailureReason = $"{modId}@{selectedVersion} is incompatible with {targetId}@{incompatibleVersion}";
                                return result;
                            }
                        }
                        continue;
                    }

                    var depTarget = GetCanonicalId(dep.Target);
                    var versionConstraint = ParseVersionRange(dep.Constraint);

                    // Add constraint
                    if (!constraints.ContainsKey(depTarget))
                        constraints[depTarget] = new();

                    constraints[depTarget].Add(new VersionConstraintSource
                    {
                        SourceMod = $"{modId}@{selectedVersion}",
                        Constraint = versionConstraint,
                        DependencyType = dep.Type
                    });

                    // Add to resolution queue
                    if (!visited.Contains(depTarget))
                        unresolved.Enqueue(depTarget);

                    // Add edge to graph
                    var depNode = new ModNode(depTarget);
                    graph.AddEdge(new DependencyEdge(node, depNode, dep.Type, versionConstraint));
                }
            }

            // Check for circular dependencies
            var cycles = graph.DetectCycles();
            if (cycles.Count > 0)
            {
                var cycle = cycles[0];
                result.Conflicts.Add(new ResolutionConflict
                {
                    CanonicalId = cycle[0].CanonicalId,
                    Type = ConflictType.CircularDependency,
                    Explanation = $"Circular dependency detected: {string.Join(" -> ", cycle.Select(n => n.CanonicalId))}"
                });
                result.Success = false;
                result.FailureReason = $"Circular dependency detected: {string.Join(" -> ", cycle.Select(n => n.CanonicalId))}";
                return result;
            }

            // Generate install order (topological sort)
            var sortedNodes = graph.TopologicalSort();
            if (sortedNodes != null)
            {
                result.InstallOrder = sortedNodes;
            }

            result.Success = true;
            result.ResolvedVersions = selectedVersions;
            result.Graph = graph;

            _logger?.LogInformation("Resolution successful: {Count} mods resolved", selectedVersions.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Resolution failed with exception");
            result.Success = false;
            result.FailureReason = $"Resolution failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Selects a version that satisfies all constraints (prefers latest).
    /// </summary>
    private SemanticVersion? SelectVersion(
        List<SemanticVersion> availableVersions,
        List<VersionConstraintSource> constraintSources)
    {
        // Sort versions descending (prefer latest)
        var sortedVersions = availableVersions.OrderByDescending(v => v).ToList();

        foreach (var version in sortedVersions)
        {
            var satisfiesAll = true;

            foreach (var source in constraintSources)
            {
                if (source.Constraint != null && !source.Constraint.IsSatisfiedBy(version))
                {
                    satisfiesAll = false;
                    break;
                }
            }

            if (satisfiesAll)
                return version;
        }

        return null;
    }

    /// <summary>
    /// Generates a human-readable conflict explanation.
    /// </summary>
    private string GenerateConflictExplanation(string modId, List<VersionConstraintSource> constraints)
    {
        if (constraints.Count == 0)
            return $"No constraints for {modId}";

        if (constraints.Count == 1)
            return $"{modId} required by {constraints[0].SourceMod} with constraint {constraints[0].Constraint}, but no satisfying version found";

        var lines = new List<string>
        {
            $"Cannot resolve {modId} due to conflicting requirements:"
        };

        foreach (var constraint in constraints)
        {
            var constraintStr = constraint.Constraint?.ToString() ?? "any version";
            lines.Add($"  - {constraint.SourceMod} requires {constraintStr}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Gets canonical ID from DependencyTarget.
    /// </summary>
    private string GetCanonicalId(DependencyTarget target)
    {
        // If BackendId is specified, construct canonical ID
        if (!string.IsNullOrEmpty(target.BackendId))
            return $"{target.BackendId}:{target.ProjectId}";
        
        // Otherwise assume ProjectId is already canonical
        return target.ProjectId;
    }

    /// <summary>
    /// Parses a version constraint string into a VersionRange.
    /// </summary>
    private VersionRange? ParseVersionRange(string? constraintString)
    {
        if (string.IsNullOrEmpty(constraintString))
            return null;

        if (VersionRange.TryParse(constraintString, out var range))
            return range;

        _logger?.LogWarning("Failed to parse version constraint: {Constraint}", constraintString);
        return null;
    }
}

/// <summary>
/// Provider interface for querying available mod versions and dependencies.
/// Backends must implement this to support dependency resolution.
/// </summary>
public interface IModVersionProvider
{
    /// <summary>
    /// Gets all available versions for a mod.
    /// </summary>
    Task<List<SemanticVersion>> GetAvailableVersionsAsync(
        string canonicalId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets dependencies for a specific mod version.
    /// </summary>
    Task<List<ModDependency>> GetDependenciesAsync(
        string canonicalId,
        SemanticVersion version,
        CancellationToken ct = default);
}
