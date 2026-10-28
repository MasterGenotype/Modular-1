namespace Modular.Core.Backends;

/// <summary>
/// A game found by name on a mod site.
/// </summary>
/// <param name="Id">The site's id for the game, as the site's searches take it.</param>
/// <param name="Name">The game's name.</param>
public sealed record GameMatch(string Id, string Name)
{
    /// <summary>The name, which is how a list or text box shows the game.</summary>
    public override string ToString() => Name;
}
