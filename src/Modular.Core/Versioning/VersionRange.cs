using System.Text.RegularExpressions;

namespace Modular.Core.Versioning;

/// <summary>
/// Represents a version range constraint for dependency resolution.
/// Supports common constraint syntaxes: >=, <=, ~, ^, ||, and combinations.
/// </summary>
public class VersionRange
{
    private readonly List<List<VersionConstraint>> _orGroups = [];

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
            var andGroup = new List<VersionConstraint>();

            // Handle AND operator (implicit space separation)
            var andParts = orPart.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var constraint in andParts)
            {
                if (!TryParseConstraint(constraint, out var parsed))
                    return false;

                andGroup.Add(parsed!);
            }

            if (andGroup.Count > 0)
                result._orGroups.Add(andGroup);
        }

        return result._orGroups.Count > 0;
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

        // Wildcard: *, 1.x, 1.2.x, 1.X, 1.2.X, 1.*, 1.2.*
        if (constraint == "*")
        {
            result = new VersionConstraint(ConstraintType.Any, null);
            return true;
        }

        var wildcardMatch = Regex.Match(constraint, @"^(\d+)\.[xX*](?:\.[xX*])?$");
        if (wildcardMatch.Success)
        {
            // 1.x or 1.* → >=1.0.0 <2.0.0
            var major = int.Parse(wildcardMatch.Groups[1].Value);
            result = new VersionConstraint(ConstraintType.WildcardMinor, new SemanticVersion(major, 0, 0));
            return true;
        }

        wildcardMatch = Regex.Match(constraint, @"^(\d+)\.(\d+)\.[xX*]$");
        if (wildcardMatch.Success)
        {
            // 1.2.x or 1.2.* → >=1.2.0 <1.3.0
            var major = int.Parse(wildcardMatch.Groups[1].Value);
            var minor = int.Parse(wildcardMatch.Groups[2].Value);
            result = new VersionConstraint(ConstraintType.WildcardPatch, new SemanticVersion(major, minor, 0));
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
    /// A version satisfies the range if it matches ANY OR group,
    /// where each group requires ALL of its constraints to match.
    /// </summary>
    public bool IsSatisfiedBy(SemanticVersion version)
    {
        if (_orGroups.Count == 0)
            return false;

        return _orGroups.Any(group => group.All(c => c.IsSatisfiedBy(version)));
    }

    public override string ToString() =>
        string.Join(" || ", _orGroups.Select(group =>
            string.Join(" ", group.Select(c => c.ToString()))));
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
            ConstraintType.WildcardMinor => IsSatisfiedByWildcardMinor(version),
            ConstraintType.WildcardPatch => IsSatisfiedByWildcardPatch(version),
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

    private bool IsSatisfiedByWildcardMinor(SemanticVersion version)
    {
        // 1.x := >=1.0.0 <2.0.0
        if (Version is null) return false;

        if (version.Major != Version.Major) return false;
        return version >= Version;
    }

    private bool IsSatisfiedByWildcardPatch(SemanticVersion version)
    {
        // 1.2.x := >=1.2.0 <1.3.0
        if (Version is null) return false;

        if (version.Major != Version.Major) return false;
        if (version.Minor != Version.Minor) return false;
        return version >= Version;
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
            ConstraintType.WildcardMinor => $"{Version!.Major}.x",
            ConstraintType.WildcardPatch => $"{Version!.Major}.{Version.Minor}.x",
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
    WildcardMinor,
    WildcardPatch,
    Any
}
