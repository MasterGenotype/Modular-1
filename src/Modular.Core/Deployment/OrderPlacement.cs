namespace Modular.Core.Deployment;

/// <summary>
/// Where to put something in an ordered list.
/// </summary>
public enum PlacementKind
{
    /// <summary>First in the list, so everything after it wins over it.</summary>
    Top,

    /// <summary>Last in the list, so it wins over everything.</summary>
    Bottom,

    /// <summary>Directly before an anchor entry.</summary>
    Before,

    /// <summary>Directly after an anchor entry.</summary>
    After,

    /// <summary>At a 1-based position, clamped to the list.</summary>
    At
}

/// <summary>
/// A place in an ordered list: the top, the bottom, beside an anchor, or a position.
/// </summary>
/// <param name="Kind">The kind of placement.</param>
/// <param name="Anchor">The anchor's key, for <see cref="PlacementKind.Before"/> and <see cref="PlacementKind.After"/>.</param>
/// <param name="Position">The 1-based position, for <see cref="PlacementKind.At"/>.</param>
/// <remarks>
/// Every ordered list in Modular runs first to last with the last winning, so "top" is
/// the weakest place and "bottom" the strongest. The install order, the load order and
/// both front ends move entries through <see cref="Apply{T}"/>, so they agree.
/// </remarks>
public sealed record OrderPlacement(PlacementKind Kind, string? Anchor = null, int Position = 0)
{
    /// <summary>First in the list.</summary>
    public static OrderPlacement Top { get; } = new(PlacementKind.Top);

    /// <summary>Last in the list.</summary>
    public static OrderPlacement Bottom { get; } = new(PlacementKind.Bottom);

    /// <summary>Directly before the entry with this key.</summary>
    /// <param name="anchor">The anchor's key.</param>
    public static OrderPlacement Before(string anchor) => new(PlacementKind.Before, anchor);

    /// <summary>Directly after the entry with this key.</summary>
    /// <param name="anchor">The anchor's key.</param>
    public static OrderPlacement After(string anchor) => new(PlacementKind.After, anchor);

    /// <summary>At a 1-based position.</summary>
    /// <param name="position">The position; 1 is the top.</param>
    public static OrderPlacement At(int position) => new(PlacementKind.At, Position: position);

    /// <summary>
    /// Moves a block of entries to this place, keeping their order among themselves.
    /// </summary>
    /// <typeparam name="T">Entry type.</typeparam>
    /// <param name="list">The list as it stands.</param>
    /// <param name="moving">The entries to move; all must be in <paramref name="list"/>.</param>
    /// <param name="key">An entry's key, compared case-insensitively.</param>
    /// <returns>The new list. The input is not changed.</returns>
    /// <exception cref="ArgumentException">
    /// Nothing to move, an entry to move is not in the list, the anchor is missing, or the
    /// anchor is itself being moved.
    /// </exception>
    /// <remarks>
    /// A block keeps the order its entries had in the list, not the order they were
    /// given in, so moving a mod with several entries never shuffles them. An
    /// <see cref="PlacementKind.At"/> position counts in the finished list: the block's
    /// first entry lands there, unless the list is too short.
    /// </remarks>
    public List<T> Apply<T>(IReadOnlyList<T> list, IReadOnlyCollection<T> moving, Func<T, string> key)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(moving);
        ArgumentNullException.ThrowIfNull(key);

        var movingKeys = moving.Select(key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (movingKeys.Count == 0)
            throw new ArgumentException("Nothing to move.", nameof(moving));

        var missing = movingKeys.Where(k => !list.Any(e => Matches(e, k, key))).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"'{missing[0]}' is not in the list.", nameof(moving));

        var block = list.Where(e => movingKeys.Contains(key(e))).ToList();
        var rest = list.Where(e => !movingKeys.Contains(key(e))).ToList();

        int at;
        switch (Kind)
        {
            case PlacementKind.Top:
                at = 0;
                break;

            case PlacementKind.Bottom:
                at = rest.Count;
                break;

            case PlacementKind.Before:
            case PlacementKind.After:
                if (string.IsNullOrEmpty(Anchor))
                    throw new ArgumentException("No entry was given to place it beside.");

                if (movingKeys.Contains(Anchor))
                    throw new ArgumentException("An entry cannot be placed beside itself.");

                var anchorIndex = rest.FindIndex(e => Matches(e, Anchor, key));
                if (anchorIndex < 0)
                    throw new ArgumentException($"'{Anchor}' is not in the list.");

                at = Kind == PlacementKind.Before ? anchorIndex : anchorIndex + 1;
                break;

            case PlacementKind.At:
                at = Math.Clamp(Position - 1, 0, rest.Count);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
        }

        rest.InsertRange(at, block);
        return rest;
    }

    /// <summary>
    /// Says where this places an entry, for messages.
    /// </summary>
    /// <param name="anchorName">The anchor's display name, when there is one.</param>
    /// <returns>A phrase such as "after 'SBBF'" or "at #3".</returns>
    public string Describe(string? anchorName = null) => Kind switch
    {
        PlacementKind.Top => "at the top",
        PlacementKind.Bottom => "at the bottom",
        PlacementKind.Before => $"before '{anchorName ?? Anchor}'",
        PlacementKind.After => $"after '{anchorName ?? Anchor}'",
        PlacementKind.At => $"at #{Position}",
        _ => Kind.ToString()
    };

    private static bool Matches<T>(T entry, string wanted, Func<T, string> key) =>
        string.Equals(key(entry), wanted, StringComparison.OrdinalIgnoreCase);
}
