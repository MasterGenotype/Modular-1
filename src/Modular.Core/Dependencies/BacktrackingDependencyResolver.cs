using Microsoft.Extensions.Logging;
using Modular.Core.Metadata;
using Modular.Core.Versioning;

namespace Modular.Core.Dependencies;

/// <summary>
/// Backtracking constraint solver inspired by PubGrub that explores the version space
/// with systematic backtracking when conflicts arise.
/// </summary>
/// <remarks>
/// Unlike the <see cref="GreedyDependencyResolver"/> which greedily picks the latest version
/// and cannot recover from bad choices, this solver maintains a decision stack and backtracks
/// when it discovers that the current partial assignment leads to an unsatisfiable state.
///
/// Algorithm overview:
///   1. Pick an unresolved mod from the queue.
///   2. Try versions in descending order (prefer latest).
///   3. For each candidate version, propagate constraints from its dependencies.
///   4. If propagation fails (no satisfying version exists for a transitive dep),
///      undo the current decision and try the next candidate.
///   5. If all candidates for a mod are exhausted, backtrack to the previous decision.
///   6. If we backtrack past the first decision, the system is unsatisfiable.
///
/// Supports optional dependencies with partial-install suggestions when they cannot be satisfied.
/// </remarks>
public class BacktrackingDependencyResolver
{
    private readonly IModVersionProvider _versionProvider;
    private readonly ILogger<BacktrackingDependencyResolver>? _logger;
    private readonly int _maxBacktracks;

    public BacktrackingDependencyResolver(
        IModVersionProvider versionProvider,
        ILogger<BacktrackingDependencyResolver>? logger = null,
        int maxBacktracks = 1000)
    {
        _versionProvider = versionProvider;
        _logger = logger;
        _maxBacktracks = maxBacktracks;
    }

    /// <summary>
    /// Resolves dependencies for a set of root mods using backtracking.
    /// </summary>
    public async Task<ResolutionResult> ResolveAsync(
        List<(string canonicalId, VersionRange? constraint)> rootRequirements,
        bool includeOptional = false,
        CancellationToken ct = default)
    {
        var result = new ResolutionResult();
        var optionalFailures = new List<OptionalDependencyFailure>();

        try
        {
            // Phase 1: Resolve required dependencies with backtracking.
            var state = new SolverState();

            // Seed root constraints.
            foreach (var (canonicalId, constraint) in rootRequirements)
            {
                state.AddConstraint(canonicalId, new VersionConstraintSource
                {
                    SourceMod = "<root>",
                    Constraint = constraint,
                    DependencyType = DependencyType.Required
                });
                state.Enqueue(canonicalId);
            }

            var success = await SolveAsync(state, includeOptional, optionalFailures, ct);

            if (!success)
            {
                result.Success = false;
                result.FailureReason = state.FailureReason ?? "Dependency resolution failed after exhausting all candidates";
                result.Conflicts = state.Conflicts;
                return result;
            }

            // Build dependency graph for the solution.
            var graph = await BuildGraphAsync(state, ct);

            // Check for circular dependencies.
            var cycles = graph.DetectCycles();
            if (cycles.Count > 0)
            {
                var cycle = cycles[0];
                result.Success = false;
                result.FailureReason = $"Circular dependency detected: {string.Join(" -> ", cycle.Select(n => n.CanonicalId))}";
                result.Conflicts.Add(new ResolutionConflict
                {
                    CanonicalId = cycle[0].CanonicalId,
                    Type = ConflictType.CircularDependency,
                    Explanation = result.FailureReason
                });
                return result;
            }

            result.Success = true;
            result.ResolvedVersions = state.SelectedVersions;
            result.Graph = graph;
            result.InstallOrder = graph.TopologicalSort();
            result.OptionalFailures = optionalFailures;

            _logger?.LogInformation(
                "Resolution successful: {Count} mods resolved, {Optional} optional skipped",
                state.SelectedVersions.Count, optionalFailures.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Resolution failed with exception");
            result.Success = false;
            result.FailureReason = $"Resolution failed: {ex.Message}";
        }

        return result;
    }

    private async Task<bool> SolveAsync(
        SolverState state,
        bool includeOptional,
        List<OptionalDependencyFailure> optionalFailures,
        CancellationToken ct)
    {
        int backtracks = 0;

        while (state.HasUnresolved)
        {
            ct.ThrowIfCancellationRequested();

            var modId = state.Dequeue();
            if (state.IsResolved(modId))
                continue;

            var availableVersions = await _versionProvider.GetAvailableVersionsAsync(modId, ct);
            if (availableVersions.Count == 0)
            {
                // Check if this was an optional dependency.
                if (state.IsOptional(modId))
                {
                    optionalFailures.Add(new OptionalDependencyFailure
                    {
                        CanonicalId = modId,
                        Reason = "No versions available"
                    });
                    state.MarkSkipped(modId);
                    continue;
                }

                state.FailureReason = $"No versions available for {modId}";
                state.Conflicts.Add(new ResolutionConflict
                {
                    CanonicalId = modId,
                    Type = ConflictType.NoVersionsAvailable,
                    Explanation = state.FailureReason
                });
                return false;
            }

            // Sort versions descending (prefer latest).
            var candidates = availableVersions
                .Where(v => state.SatisfiesConstraints(modId, v))
                .OrderByDescending(v => v)
                .ToList();

            if (candidates.Count == 0)
            {
                if (state.IsOptional(modId))
                {
                    optionalFailures.Add(new OptionalDependencyFailure
                    {
                        CanonicalId = modId,
                        Reason = "No version satisfies constraints"
                    });
                    state.MarkSkipped(modId);
                    continue;
                }

                // Need to backtrack.
                if (!state.CanBacktrack())
                {
                    state.FailureReason = GenerateConflictExplanation(modId, state.GetConstraints(modId));
                    state.Conflicts.Add(new ResolutionConflict
                    {
                        CanonicalId = modId,
                        Type = ConflictType.NoSatisfyingVersion,
                        Constraints = state.GetConstraints(modId),
                        Explanation = state.FailureReason
                    });
                    return false;
                }

                backtracks++;
                if (backtracks > _maxBacktracks)
                {
                    state.FailureReason = $"Exceeded maximum backtrack limit ({_maxBacktracks})";
                    return false;
                }

                _logger?.LogDebug("Backtracking from {ModId} (backtrack #{Count})", modId, backtracks);
                state.Backtrack();
                continue;
            }

            bool selected = false;
            foreach (var version in candidates)
            {
                ct.ThrowIfCancellationRequested();

                // Try this version: push a decision point.
                state.PushDecision(modId, version, candidates);

                var dependencies = await _versionProvider.GetDependenciesAsync(modId, version, ct);

                // Check incompatibilities.
                bool incompatible = false;
                foreach (var dep in dependencies.Where(d => d.Type == DependencyType.Incompatible))
                {
                    var targetId = GetCanonicalId(dep.Target);
                    if (state.IsResolved(targetId))
                    {
                        var resolvedVersion = state.SelectedVersions[targetId];
                        var constraint = ParseVersionRange(dep.Constraint);
                        if (constraint == null || constraint.IsSatisfiedBy(resolvedVersion))
                        {
                            incompatible = true;
                            break;
                        }
                    }
                }

                if (incompatible)
                {
                    state.PopDecision();
                    continue;
                }

                // Propagate constraints from this version's dependencies.
                bool propagationOk = true;
                foreach (var dep in dependencies)
                {
                    if (dep.Type == DependencyType.Incompatible)
                        continue;
                    if (dep.Type == DependencyType.Embedded)
                        continue;
                    if (dep.Type == DependencyType.Optional && !includeOptional)
                        continue;

                    var depTarget = GetCanonicalId(dep.Target);
                    var versionConstraint = ParseVersionRange(dep.Constraint);

                    state.AddConstraint(depTarget, new VersionConstraintSource
                    {
                        SourceMod = $"{modId}@{version}",
                        Constraint = versionConstraint,
                        DependencyType = dep.Type
                    });

                    // If target is already resolved, verify compatibility.
                    if (state.IsResolved(depTarget))
                    {
                        if (versionConstraint != null && !versionConstraint.IsSatisfiedBy(state.SelectedVersions[depTarget]))
                        {
                            if (dep.Type == DependencyType.Optional)
                            {
                                optionalFailures.Add(new OptionalDependencyFailure
                                {
                                    CanonicalId = depTarget,
                                    Reason = $"Already resolved to {state.SelectedVersions[depTarget]}, incompatible with {versionConstraint}"
                                });
                            }
                            else
                            {
                                propagationOk = false;
                                break;
                            }
                        }
                    }
                    else if (!state.IsSkipped(depTarget))
                    {
                        state.Enqueue(depTarget);
                    }
                }

                if (propagationOk)
                {
                    selected = true;
                    _logger?.LogDebug("Selected {ModId}@{Version}", modId, version);
                    break;
                }

                state.PopDecision();
            }

            if (!selected)
            {
                if (state.IsOptional(modId))
                {
                    optionalFailures.Add(new OptionalDependencyFailure
                    {
                        CanonicalId = modId,
                        Reason = "All candidate versions lead to conflicts"
                    });
                    state.MarkSkipped(modId);
                    continue;
                }

                if (!state.CanBacktrack())
                {
                    state.FailureReason = $"All versions of {modId} lead to conflicts";
                    state.Conflicts.Add(new ResolutionConflict
                    {
                        CanonicalId = modId,
                        Type = ConflictType.NoSatisfyingVersion,
                        Explanation = state.FailureReason
                    });
                    return false;
                }

                backtracks++;
                if (backtracks > _maxBacktracks)
                {
                    state.FailureReason = $"Exceeded maximum backtrack limit ({_maxBacktracks})";
                    return false;
                }

                _logger?.LogDebug("Backtracking from {ModId} (backtrack #{Count})", modId, backtracks);
                state.Backtrack();
            }
        }

        return true;
    }

    private async Task<DependencyGraph> BuildGraphAsync(SolverState state, CancellationToken ct)
    {
        var graph = new DependencyGraph();

        foreach (var (modId, version) in state.SelectedVersions)
        {
            var node = new ModNode(modId, version);
            graph.AddNode(node);

            var dependencies = await _versionProvider.GetDependenciesAsync(modId, version, ct);
            foreach (var dep in dependencies)
            {
                if (dep.Type == DependencyType.Embedded)
                    continue;

                var targetId = GetCanonicalId(dep.Target);
                if (state.IsResolved(targetId))
                {
                    var targetNode = new ModNode(targetId, state.SelectedVersions[targetId]);
                    var constraint = ParseVersionRange(dep.Constraint);
                    graph.AddEdge(new DependencyEdge(node, targetNode, dep.Type, constraint));
                }
            }
        }

        return graph;
    }

    private string GenerateConflictExplanation(string modId, List<VersionConstraintSource> constraints)
    {
        if (constraints.Count == 0)
            return $"No constraints for {modId}";

        if (constraints.Count == 1)
            return $"{modId} required by {constraints[0].SourceMod} with constraint {constraints[0].Constraint}, but no satisfying version found";

        var lines = new List<string> { $"Cannot resolve {modId} due to conflicting requirements:" };
        foreach (var constraint in constraints)
        {
            var constraintStr = constraint.Constraint?.ToString() ?? "any version";
            lines.Add($"  - {constraint.SourceMod} requires {constraintStr}");
        }
        return string.Join("\n", lines);
    }

    private string GetCanonicalId(DependencyTarget target)
    {
        if (!string.IsNullOrEmpty(target.BackendId))
            return $"{target.BackendId}:{target.ProjectId}";
        return target.ProjectId;
    }

    private VersionRange? ParseVersionRange(string? constraintString)
    {
        if (string.IsNullOrEmpty(constraintString))
            return null;

        if (VersionRange.TryParse(constraintString, out var range))
            return range;

        _logger?.LogWarning("Failed to parse version constraint: {Constraint}", constraintString);
        return null;
    }

    /// <summary>
    /// Internal solver state with decision stack for backtracking.
    /// </summary>
    private class SolverState
    {
        private readonly Queue<string> _unresolved = new();
        private readonly HashSet<string> _queued = new();
        private readonly HashSet<string> _skipped = new();
        private readonly Stack<Decision> _decisionStack = new();

        /// <summary>Current selected versions.</summary>
        public Dictionary<string, SemanticVersion> SelectedVersions { get; } = new();

        /// <summary>Constraints per mod.</summary>
        private Dictionary<string, List<VersionConstraintSource>> _constraints = new();

        /// <summary>Conflicts found.</summary>
        public List<ResolutionConflict> Conflicts { get; } = new();

        public string? FailureReason { get; set; }

        public bool HasUnresolved => _unresolved.Count > 0;

        public void Enqueue(string modId)
        {
            if (!_queued.Contains(modId) && !SelectedVersions.ContainsKey(modId))
            {
                _unresolved.Enqueue(modId);
                _queued.Add(modId);
            }
        }

        public string Dequeue() => _unresolved.Dequeue();

        public bool IsResolved(string modId) => SelectedVersions.ContainsKey(modId);
        public bool IsSkipped(string modId) => _skipped.Contains(modId);

        public bool IsOptional(string modId)
        {
            if (!_constraints.TryGetValue(modId, out var list))
                return false;
            return list.All(c => c.DependencyType == DependencyType.Optional);
        }

        public void MarkSkipped(string modId) => _skipped.Add(modId);

        public void AddConstraint(string modId, VersionConstraintSource constraint)
        {
            if (!_constraints.ContainsKey(modId))
                _constraints[modId] = new();
            _constraints[modId].Add(constraint);
        }

        public List<VersionConstraintSource> GetConstraints(string modId)
        {
            return _constraints.TryGetValue(modId, out var list) ? list : new();
        }

        public bool SatisfiesConstraints(string modId, SemanticVersion version)
        {
            if (!_constraints.TryGetValue(modId, out var list))
                return true;
            return list.All(c => c.Constraint == null || c.Constraint.IsSatisfiedBy(version));
        }

        public void PushDecision(string modId, SemanticVersion version, List<SemanticVersion> candidates)
        {
            // Snapshot current state.
            var decision = new Decision
            {
                ModId = modId,
                SelectedVersion = version,
                RemainingCandidates = candidates.Where(v => v != version).ToList(),
                SnapshotVersions = new Dictionary<string, SemanticVersion>(SelectedVersions),
                SnapshotConstraints = _constraints.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new List<VersionConstraintSource>(kvp.Value)),
                SnapshotQueued = new HashSet<string>(_queued),
                SnapshotUnresolved = new Queue<string>(_unresolved),
                SnapshotSkipped = new HashSet<string>(_skipped)
            };

            _decisionStack.Push(decision);
            SelectedVersions[modId] = version;
        }

        public void PopDecision()
        {
            if (_decisionStack.Count == 0) return;

            var decision = _decisionStack.Pop();

            // Restore state.
            SelectedVersions.Clear();
            foreach (var kvp in decision.SnapshotVersions)
                SelectedVersions[kvp.Key] = kvp.Value;

            _constraints = decision.SnapshotConstraints.ToDictionary(
                kvp => kvp.Key,
                kvp => new List<VersionConstraintSource>(kvp.Value));

            _queued.Clear();
            foreach (var q in decision.SnapshotQueued)
                _queued.Add(q);

            _unresolved.Clear();
            foreach (var u in decision.SnapshotUnresolved)
                _unresolved.Enqueue(u);

            _skipped.Clear();
            foreach (var s in decision.SnapshotSkipped)
                _skipped.Add(s);
        }

        public bool CanBacktrack() => _decisionStack.Count > 0;

        public void Backtrack()
        {
            while (_decisionStack.Count > 0)
            {
                var decision = _decisionStack.Peek();

                if (decision.RemainingCandidates.Count > 0)
                {
                    // Try the next candidate for this decision.
                    PopDecision();
                    var nextVersion = decision.RemainingCandidates[0];
                    var remaining = decision.RemainingCandidates.Skip(1).ToList();
                    PushDecision(decision.ModId, nextVersion, remaining.Prepend(nextVersion).ToList());
                    return;
                }

                // No more candidates for this mod — undo and try the parent decision.
                PopDecision();
                // Re-enqueue this mod so it gets retried after the parent backtracks.
                Enqueue(decision.ModId);
            }
        }
    }

    private class Decision
    {
        public string ModId { get; init; } = string.Empty;
        public SemanticVersion SelectedVersion { get; init; } = null!;
        public List<SemanticVersion> RemainingCandidates { get; init; } = new();
        public Dictionary<string, SemanticVersion> SnapshotVersions { get; init; } = new();
        public Dictionary<string, List<VersionConstraintSource>> SnapshotConstraints { get; init; } = new();
        public HashSet<string> SnapshotQueued { get; init; } = new();
        public Queue<string> SnapshotUnresolved { get; init; } = new();
        public HashSet<string> SnapshotSkipped { get; init; } = new();
    }
}

/// <summary>
/// Records an optional dependency that could not be satisfied.
/// </summary>
public class OptionalDependencyFailure
{
    public string CanonicalId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
