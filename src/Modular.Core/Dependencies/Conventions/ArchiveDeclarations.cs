using Modular.Core.Metadata;

namespace Modular.Core.Dependencies.Conventions;

/// <summary>
/// Where a mod archive came from: a mod on a mod hub.
/// </summary>
/// <param name="BackendId">Id of the backend the hub is reached through.</param>
/// <param name="ModId">The mod's id on that hub.</param>
/// <param name="GameDomain">The hub's name for the game, when the record carries one.</param>
/// <param name="FileId">The hub's id for the downloaded file, when known.</param>
public sealed record DeclaredSource(
    string BackendId,
    string ModId,
    string? GameDomain = null,
    string? FileId = null);

/// <summary>
/// Something a mod says, in metadata it ships with, about another mod.
/// </summary>
/// <param name="Name">Name of the other mod, or of the file that stands for it.</param>
/// <param name="Identifier">
/// The identifier the convention uses, such as a Thunderstore dependency string or a
/// plugin file name. Matched against <see cref="ArchiveDeclarations.Provides"/>.
/// </param>
/// <param name="Convention">Name of the convention the statement was read from.</param>
/// <param name="Type">Whether the other mod is needed, optional, or must be absent.</param>
/// <param name="Url">Where the other mod can be found, when the convention implies it.</param>
public sealed record DeclaredRequirement(
    string Name,
    string Identifier,
    string Convention,
    DependencyType Type = DependencyType.Required,
    string? Url = null);

/// <summary>
/// Everything the metadata shipped with one mod archive declares.
/// </summary>
public sealed record ArchiveDeclarations
{
    /// <summary>Nothing declared.</summary>
    public static ArchiveDeclarations None { get; } = new();

    /// <summary>The hub mod the archive came from, from the most reliable record found.</summary>
    public DeclaredSource? Source { get; init; }

    /// <summary>The mod's name, as its own metadata gives it.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Identifiers other mods may use to depend on this one, such as its Thunderstore
    /// <c>Namespace-Name</c>.
    /// </summary>
    public IReadOnlyList<string> Provides { get; init; } = [];

    /// <summary>What the mod says about other mods.</summary>
    public IReadOnlyList<DeclaredRequirement> Requirements { get; init; } = [];

    /// <summary>
    /// Combines two sets of declarations. The first one's source and name win, because
    /// conventions are consulted most reliable first.
    /// </summary>
    public ArchiveDeclarations MergedWith(ArchiveDeclarations other) => new()
    {
        Source = Source ?? other.Source,
        Name = Name ?? other.Name,
        Provides = Provides.Concat(other.Provides).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Requirements = Requirements.Concat(other.Requirements)
            .DistinctBy(r => (r.Convention, r.Identifier.ToLowerInvariant(), r.Type))
            .ToList()
    };
}
