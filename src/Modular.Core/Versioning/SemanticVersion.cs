using System.Text.RegularExpressions;

namespace Modular.Core.Versioning;

/// <summary>
/// Represents a semantic version following SemVer 2.0.0 specification.
/// Format: MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]
/// </summary>
public class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex SemVerRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
        @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)" +
        @"(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? Build { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public SemanticVersion(int major, int minor, int patch, string? prerelease = null, string? build = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
            throw new ArgumentException("Version components must be non-negative");

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    /// <summary>
    /// Parses a semantic version string.
    /// </summary>
    public static SemanticVersion Parse(string version)
    {
        if (TryParse(version, out var result))
            return result!;
        throw new FormatException($"Invalid semantic version: {version}");
    }

    /// <summary>
    /// Tries to parse a semantic version string.
    /// </summary>
    public static bool TryParse(string version, out SemanticVersion? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(version))
            return false;

        // Remove 'v' prefix if present
        version = version.Trim();
        if (version.StartsWith('v') || version.StartsWith('V'))
            version = version[1..];

        var match = SemVerRegex.Match(version);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["major"].Value, out var major))
            return false;
        if (!int.TryParse(match.Groups["minor"].Value, out var minor))
            return false;
        if (!int.TryParse(match.Groups["patch"].Value, out var patch))
            return false;

        var prerelease = match.Groups["prerelease"].Success ? match.Groups["prerelease"].Value : null;
        var build = match.Groups["build"].Success ? match.Groups["build"].Value : null;

        result = new SemanticVersion(major, minor, patch, prerelease, build);
        return true;
    }

    /// <summary>
    /// Compares two semantic versions according to SemVer 2.0.0 precedence rules.
    /// </summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        // Compare major, minor, patch
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // When a major, minor, and patch are equal, a pre-release version has LOWER precedence
        if (IsPrerelease && !other.IsPrerelease) return -1;
        if (!IsPrerelease && other.IsPrerelease) return 1;
        if (!IsPrerelease && !other.IsPrerelease) return 0;

        // Compare prerelease identifiers
        return ComparePrereleaseIdentifiers(Prerelease!, other.Prerelease!);
    }

    private static int ComparePrereleaseIdentifiers(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var minLength = Math.Min(leftParts.Length, rightParts.Length);

        for (int i = 0; i < minLength; i++)
        {
            var leftPart = leftParts[i];
            var rightPart = rightParts[i];

            var leftIsNumeric = int.TryParse(leftPart, out var leftNum);
            var rightIsNumeric = int.TryParse(rightPart, out var rightNum);

            // Numeric identifiers have lower precedence than alphanumeric
            if (leftIsNumeric && !rightIsNumeric) return -1;
            if (!leftIsNumeric && rightIsNumeric) return 1;

            // Both numeric: compare numerically
            if (leftIsNumeric && rightIsNumeric)
            {
                if (leftNum != rightNum)
                    return leftNum.CompareTo(rightNum);
            }
            // Both alphanumeric: compare lexically (ASCII sort order)
            else
            {
                var comparison = string.CompareOrdinal(leftPart, rightPart);
                if (comparison != 0)
                    return comparison;
            }
        }

        // A larger set of identifiers has higher precedence
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    public bool Equals(SemanticVersion? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        
        // Build metadata SHOULD be ignored for precedence
        return Major == other.Major &&
               Minor == other.Minor &&
               Patch == other.Patch &&
               Prerelease == other.Prerelease;
    }

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString()
    {
        var version = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease))
            version += $"-{Prerelease}";
        if (!string.IsNullOrEmpty(Build))
            version += $"+{Build}";
        return version;
    }

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) =>
        !(left == right);

    public static bool operator <(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;
}
