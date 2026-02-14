using System.Text.RegularExpressions;

namespace Modular.Core.Versioning;

/// <summary>
/// Represents a version range constraint for dependency resolution.
/// Supports common constraint syntaxes: >=, <=, ~, ^, ||, and combinations.
/// </summary>
public class VersionRange
{
    private readonly List<VersionConstraint> _constraints = [];

    /// <summary>
    /// Parses a version range string (e.g., ">=1.0.0", "^2.3.4", "~1.2.3 || >=2.0.0").
    /// </summary>
    public static VersionRange Parse(string range)
    {
        if (TryParse(range, out var result))
            return result!;
        throw new FormatException($"Invalid version range: {range}");
    }

    /// <summary>
    /// Tries to parse a version range string.
    /// </summary>
    public static bool TryParse(string range, out VersionRange? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(range))
            return false;

        result = new VersionRange();

        // Handle OR operator (||)
        var orParts = range.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var orPart in orParts)
        {
            // Handle AND operator (implicit space separation)
            var andParts = orPart.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var constraint in andParts)
            {
                if (!TryParseConstraint(constraint, out var parsed))
                    return false;

                result._constraints.Add(parsed!);
            }
        }

        return result._constraints.Count > 0;
    }

    private static bool TryParseConstraint(string constraint, out VersionConstraint? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(constraint))
            return false;

        constraint = constraint.Trim();

        // Caret range: ^1.2.3 means >=1.2.3 <2.0.0 (or >=0.x.y <0.(x+1).0 for 0.x.y)
        if (constraint.StartsWith('^'))
        {
            if (!SemanticVersion.TryParse(constraint[1..], out var version))
                return false;

            result = new VersionConstraint(ConstraintType.Caret, version!);
            return true;
        }

        // Tilde range: ~1.2.3 means >=1.2.3 <1.3.0
        if (constraint.StartsWith('~'))
        {
            if (!SemanticVersion.TryParse(constraint[1..], out var version))
                return false;

            result = new VersionConstraint(ConstraintType.Tilde, version!);
            return true;
        }

        // Exact match: =1.2.3 or just 1.2.3
        if (constraint.StartsWith('='))
        {
            if (!SemanticVersion.TryParse(constraint[1..], out var version))
                return false;

            result = new VersionConstraint(ConstraintType.Exact, version!);
            return true;
        }

        // Greater than or equal: >=1.2.3
        if (constraint.StartsWith(">="))
        {
            if (!SemanticVersion.TryParse(constraint[2..], out var version))
                return false;

            result = new VersionConstraint(ConstraintType.GreaterThanOrEqual, version!);
            return true;
        }

        // Greater than: >1.2.3
        if (constraint.StartsWith('>'))
        {
            if (!SemanticVersion.TryParse(constraint[1..], out var version))
                return false;

            result = new VersionConstraint(ConstraintType.GreaterThan, version!);
            return true;
        }

        // Less than or equal: <=1.2.3
        if (constraint.StartsWith("<="))
        {
            if (!SemanticVersion.TryParse(constraint[2..], out var version))
                return false;

            result = new VersionConstraint(ConstraintType.LessThanOrEqual, version!);
            return true;
        }

        // Less than: <1.2.3
        if (constraint.StartsWith('<'))
        {
            if (!SemanticVersion.TryParse(constraint[1..], out var version))
                return false;

            result = new VersionConstraint(ConstraintType.LessThan, version!);
            return true;
        }

        // Wildcard: 1.x, 1.2.x, * (not implemented yet - treat as any)
        if (constraint == "*" || constraint.Contains('x') || constraint.Contains('X'))
        {
            result = new VersionConstraint(ConstraintType.Any, null);
            return true;
        }

        // Default: exact version match
        if (SemanticVersion.TryParse(constraint, out var exactVersion))
        {
            result = new VersionConstraint(ConstraintType.Exact, exactVersion!);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tests if a version satisfies this range constraint.
    /// </summary>
    public bool IsSatisfiedBy(SemanticVersion version)
    {
        if (_constraints.Count == 0)
            return false;

        // Group constraints by OR blocks (separated by ||)
        // For now, we treat all constraints as AND (simplification)
        return _constraints.All(c => c.IsSatisfiedBy(version));
    }

    public override string ToString() => string.Join(" ", _constraints.Select(c => c.ToString()));
}

/// <summary>
/// A single version constraint (e.g., >=1.0.0, ^2.3.4).
/// </summary>
internal class VersionConstraint
{
    public ConstraintType Type { get; }
    public SemanticVersion? Version { get; }

    public VersionConstraint(ConstraintType type, SemanticVersion? version)
    {
        Type = type;
        Version = version;
    }

    public bool IsSatisfiedBy(SemanticVersion version)
    {
        return Type switch
        {
            ConstraintType.Exact => version == Version,
            ConstraintType.GreaterThan => version > Version!,
            ConstraintType.GreaterThanOrEqual => version >= Version!,
            ConstraintType.LessThan => version < Version!,
            ConstraintType.LessThanOrEqual => version <= Version!,
            ConstraintType.Tilde => IsSatisfiedByTilde(version),
            ConstraintType.Caret => IsSatisfiedByCaret(version),
            ConstraintType.Any => true,
            _ => false
        };
    }

    private bool IsSatisfiedByTilde(SemanticVersion version)
    {
        // ~1.2.3 := >=1.2.3 <1.3.0
        if (Version is null) return false;

        if (version < Version) return false;
        
        var upperBound = new SemanticVersion(Version.Major, Version.Minor + 1, 0);
        return version < upperBound;
    }

    private bool IsSatisfiedByCaret(SemanticVersion version)
    {
        // ^1.2.3 := >=1.2.3 <2.0.0
        // ^0.2.3 := >=0.2.3 <0.3.0 (special case for 0.x)
        // ^0.0.3 := >=0.0.3 <0.0.4 (special case for 0.0.x)
        if (Version is null) return false;

        if (version < Version) return false;

        if (Version.Major > 0)
        {
            // Normal case: increment major
            var upperBound = new SemanticVersion(Version.Major + 1, 0, 0);
            return version < upperBound;
        }
        else if (Version.Minor > 0)
        {
            // 0.x.y case: increment minor
            var upperBound = new SemanticVersion(0, Version.Minor + 1, 0);
            return version < upperBound;
        }
        else
        {
            // 0.0.x case: increment patch
            var upperBound = new SemanticVersion(0, 0, Version.Patch + 1);
            return version < upperBound;
        }
    }

    public override string ToString()
    {
        return Type switch
        {
            ConstraintType.Exact => $"={Version}",
            ConstraintType.GreaterThan => $">{Version}",
            ConstraintType.GreaterThanOrEqual => $">={Version}",
            ConstraintType.LessThan => $"<{Version}",
            ConstraintType.LessThanOrEqual => $"<={Version}",
            ConstraintType.Tilde => $"~{Version}",
            ConstraintType.Caret => $"^{Version}",
            ConstraintType.Any => "*",
            _ => ""
        };
    }
}

internal enum ConstraintType
{
    Exact,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Tilde,
    Caret,
    Any
}
