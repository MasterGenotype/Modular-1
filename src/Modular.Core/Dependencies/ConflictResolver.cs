using Microsoft.Extensions.Logging;
using Modular.Core.Versioning;

namespace Modular.Core.Dependencies;

/// <summary>
/// Provides automated strategies for resolving dependency and file conflicts.
/// </summary>
public class ConflictResolver
{
    private readonly IModVersionProvider _versionProvider;
    private readonly ILogger<ConflictResolver>? _logger;

    public ConflictResolver(
        IModVersionProvider versionProvider,
        ILogger<ConflictResolver>? logger = null)
    {
        _versionProvider = versionProvider;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to resolve conflicts using automated strategies.
    /// </summary>
    public async Task<ConflictResolutionResult> ResolveConflictsAsync(
        ResolutionResult resolutionResult,
        FileConflictReport? fileConflicts = null,
        ResolutionStrategy strategy = ResolutionStrategy.Automatic,
        CancellationToken ct = default)
    {
        var result = new ConflictResolutionResult
        {
            OriginalConflicts = resolutionResult.Conflicts,
            Strategy = strategy
        };

        if (resolutionResult.Success)
        {
            result.Success = true;
            result.Suggestions = new List<ResolutionSuggestion>();
            return result;
        }

        // Generate resolution suggestions based on conflict type
        foreach (var conflict in resolutionResult.Conflicts)
        {
            var suggestions = await GenerateSuggestionsAsync(conflict, strategy, ct);
            result.Suggestions.AddRange(suggestions);
        }

        // Apply automatic resolution if strategy allows
        if (strategy == ResolutionStrategy.Automatic && result.Suggestions.Count > 0)
        {
            result.AppliedSuggestions = ApplyAutomaticResolutions(result.Suggestions);
            result.Success = result.AppliedSuggestions.Count > 0;
        }

        return result;
    }

    /// <summary>
    /// Generates resolution suggestions for a conflict.
    /// </summary>
    private async Task<List<ResolutionSuggestion>> GenerateSuggestionsAsync(
        ResolutionConflict conflict,
        ResolutionStrategy strategy,
        CancellationToken ct)
    {
        var suggestions = new List<ResolutionSuggestion>();

        switch (conflict.Type)
        {
            case ConflictType.NoSatisfyingVersion:
                suggestions.AddRange(await GenerateVersionConflictSuggestionsAsync(conflict, ct));
                break;

            case ConflictType.IncompatibleMods:
                suggestions.AddRange(GenerateIncompatibilitySuggestions(conflict));
                break;

            case ConflictType.CircularDependency:
                suggestions.Add(new ResolutionSuggestion
                {
                    Type = ResolutionAction.RemoveDependency,
                    Description = "Break circular dependency by removing one of the involved mods",
                    Confidence = 0.5,
                    AffectedMods = conflict.Constraints.Select(c => c.SourceMod).ToList()
                });
                break;

            case ConflictType.ModNotFound:
            case ConflictType.NoVersionsAvailable:
                suggestions.Add(new ResolutionSuggestion
                {
                    Type = ResolutionAction.Skip,
                    Description = $"Skip {conflict.CanonicalId} (not available)",
                    Confidence = 0.8,
                    AffectedMods = new List<string> { conflict.CanonicalId }
                });
                break;
        }

        return suggestions;
    }

    /// <summary>
    /// Generates suggestions for version constraint conflicts.
    /// </summary>
    private async Task<List<ResolutionSuggestion>> GenerateVersionConflictSuggestionsAsync(
        ResolutionConflict conflict,
        CancellationToken ct)
    {
        var suggestions = new List<ResolutionSuggestion>();

        // Strategy 1: Try upgrading/downgrading conflicting mods
        var availableVersions = await _versionProvider.GetAvailableVersionsAsync(conflict.CanonicalId, ct);
        if (availableVersions.Count > 0)
        {
            // Find version that satisfies most constraints
            var bestMatch = FindBestMatchingVersion(availableVersions, conflict.Constraints);
            if (bestMatch != null)
            {
                suggestions.Add(new ResolutionSuggestion
                {
                    Type = ResolutionAction.ChangeVersion,
                    TargetMod = conflict.CanonicalId,
                    TargetVersion = bestMatch,
                    Description = $"Change {conflict.CanonicalId} to version {bestMatch}",
                    Confidence = 0.7,
                    AffectedMods = new List<string> { conflict.CanonicalId }
                });
            }
        }

        // Strategy 2: Relax constraints by upgrading dependent mods
        foreach (var constraint in conflict.Constraints)
        {
            if (constraint.SourceMod != "<root>")
            {
                suggestions.Add(new ResolutionSuggestion
                {
                    Type = ResolutionAction.UpgradeDependent,
                    TargetMod = constraint.SourceMod,
                    Description = $"Try upgrading {constraint.SourceMod} to relax constraints",
                    Confidence = 0.6,
                    AffectedMods = new List<string> { constraint.SourceMod, conflict.CanonicalId }
                });
            }
        }

        // Strategy 3: Remove optional dependencies if any
        var optionalDeps = conflict.Constraints.Where(c => c.DependencyType == Modular.Core.Metadata.DependencyType.Optional).ToList();
        if (optionalDeps.Count > 0)
        {
            suggestions.Add(new ResolutionSuggestion
            {
                Type = ResolutionAction.RemoveDependency,
                Description = $"Remove optional dependency {conflict.CanonicalId}",
                Confidence = 0.8,
                AffectedMods = new List<string> { conflict.CanonicalId }
            });
        }

        return suggestions;
    }

    /// <summary>
    /// Generates suggestions for incompatibility conflicts.
    /// </summary>
    private List<ResolutionSuggestion> GenerateIncompatibilitySuggestions(ResolutionConflict conflict)
    {
        var suggestions = new List<ResolutionSuggestion>
        {
            new ResolutionSuggestion
            {
                Type = ResolutionAction.RemoveMod,
                TargetMod = conflict.CanonicalId,
                Description = $"Remove {conflict.CanonicalId} (incompatible)",
                Confidence = 0.7,
                AffectedMods = new List<string> { conflict.CanonicalId }
            }
        };

        return suggestions;
    }

    /// <summary>
    /// Finds the version that satisfies the most constraints.
    /// </summary>
    private SemanticVersion? FindBestMatchingVersion(
        List<SemanticVersion> availableVersions,
        List<VersionConstraintSource> constraints)
    {
        SemanticVersion? bestVersion = null;
        int maxSatisfied = 0;

        foreach (var version in availableVersions.OrderByDescending(v => v))
        {
            int satisfied = 0;
            foreach (var constraint in constraints)
            {
                if (constraint.Constraint == null || constraint.Constraint.IsSatisfiedBy(version))
                    satisfied++;
            }

            if (satisfied > maxSatisfied)
            {
                maxSatisfied = satisfied;
                bestVersion = version;
            }

            // If we satisfy all constraints, use this version
            if (satisfied == constraints.Count)
                break;
        }

        return maxSatisfied > 0 ? bestVersion : null;
    }

    /// <summary>
    /// Applies automatic resolutions for suggestions with high confidence.
    /// </summary>
    private List<ResolutionSuggestion> ApplyAutomaticResolutions(List<ResolutionSuggestion> suggestions)
    {
        // Only apply suggestions with confidence >= 0.8
        return suggestions.Where(s => s.Confidence >= 0.8).ToList();
    }
}

/// <summary>
/// Result of conflict resolution attempt.
/// </summary>
public class ConflictResolutionResult
{
    public bool Success { get; set; }
    public List<ResolutionConflict> OriginalConflicts { get; set; } = new();
    public List<ResolutionSuggestion> Suggestions { get; set; } = new();
    public List<ResolutionSuggestion> AppliedSuggestions { get; set; } = new();
    public ResolutionStrategy Strategy { get; set; }
}

/// <summary>
/// A suggested resolution action.
/// </summary>
public class ResolutionSuggestion
{
    public ResolutionAction Type { get; set; }
    public string? TargetMod { get; set; }
    public SemanticVersion? TargetVersion { get; set; }
    public string Description { get; set; } = string.Empty;
    public double Confidence { get; set; } // 0.0 - 1.0
    public List<string> AffectedMods { get; set; } = new();

    public override string ToString()
    {
        return $"{Description} (confidence: {Confidence:P0})";
    }
}

/// <summary>
/// Type of resolution action.
/// </summary>
public enum ResolutionAction
{
    /// <summary>Change a mod to a different version.</summary>
    ChangeVersion,

    /// <summary>Upgrade a dependent mod.</summary>
    UpgradeDependent,

    /// <summary>Downgrade a dependent mod.</summary>
    DowngradeDependent,

    /// <summary>Remove a mod from the resolution.</summary>
    RemoveMod,

    /// <summary>Remove a dependency relationship.</summary>
    RemoveDependency,

    /// <summary>Skip this mod (don't install).</summary>
    Skip,

    /// <summary>Adjust load order to resolve file conflicts.</summary>
    AdjustLoadOrder,

    /// <summary>Replace with an alternative mod.</summary>
    ReplaceWithAlternative
}

/// <summary>
/// Strategy for conflict resolution.
/// </summary>
public enum ResolutionStrategy
{
    /// <summary>Automatically apply high-confidence resolutions.</summary>
    Automatic,

    /// <summary>Generate suggestions but require manual approval.</summary>
    Manual,

    /// <summary>Conservative - only suggest safe changes.</summary>
    Conservative,

    /// <summary>Aggressive - try all possible resolutions.</summary>
    Aggressive
}
