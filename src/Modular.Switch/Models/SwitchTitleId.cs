using System.Text.RegularExpressions;

namespace Modular.Switch.Models;

/// <summary>
/// Represents a Nintendo Switch TitleID — a 16-character hex string (64-bit).
/// Case-normalised to uppercase on construction. Validated against the Switch
/// naming convention (16 hex digits, base game IDs end in 000, DLC end in ≥001).
/// </summary>
public readonly record struct SwitchTitleId
{
    private static readonly Regex TitleIdPattern = new(@"^[0-9A-Fa-f]{16}$", RegexOptions.Compiled);

    /// <summary>The canonical uppercase 16-character hex string.</summary>
    public string Value { get; }

    public SwitchTitleId(string value)
    {
        if (!TitleIdPattern.IsMatch(value))
            throw new ArgumentException($"'{value}' is not a valid Switch TitleID (expected 16 hex chars).", nameof(value));
        Value = value.ToUpperInvariant();
    }

    /// <summary>Attempts to parse; returns false and sets <paramref name="result"/> to default on failure.</summary>
    public static bool TryParse(string? raw, out SwitchTitleId result)
    {
        if (raw != null && TitleIdPattern.IsMatch(raw))
        {
            result = new SwitchTitleId(raw);
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>True if this ID looks like a base-game title (last 3 hex digits are 000).</summary>
    public bool IsBaseGame => Value.EndsWith("000", StringComparison.Ordinal);

    /// <summary>The corresponding base-game ID (clears the last 12 bits, sets them to 000).</summary>
    public SwitchTitleId ToBaseGame()
    {
        var baseHex = Value[..^3] + "000";
        return new SwitchTitleId(baseHex);
    }

    /// <summary>Yuzu load path component — the TitleID as written on disk.</summary>
    public string YuzuLoadComponent => Value;

    public override string ToString() => Value;
}
