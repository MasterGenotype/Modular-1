using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Modular.Core.Backends;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Picks the game a mod search is limited to, by name: suggestions come from the site as
/// the user types, and the configured games are offered first.
/// </summary>
/// <remarks>
/// A game id typed straight in still works, for anyone who knows it.
/// </remarks>
public partial class GamePickerViewModel : ViewModelBase
{
    // Sites refuse or flood shorter name searches; below this only configured games are offered.
    private const int MinSearchLength = 3;

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<GameMatch>>>? _search;
    private bool _choosingInternally;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private GameMatch? _selectedGame;

    /// <summary>Raised when the user picks a game from the suggestions.</summary>
    public event EventHandler? GameChosen;

    /// <summary>Games from the settings, offered ahead of the site's matches.</summary>
    public ObservableCollection<GameMatch> ConfiguredGames { get; } = new();

    /// <summary>Fills the suggestion list for the text typed so far.</summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> Populator { get; }

    /// <summary>Whether anything has been typed.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    // Designer constructor
    public GamePickerViewModel()
    {
        Populator = PopulateAsync;
    }

    /// <summary>
    /// Creates the picker.
    /// </summary>
    /// <param name="search">Finds games by name, or by id when given a number.</param>
    /// <param name="configuredIds">Game ids from the settings; the first is preselected.</param>
    public GamePickerViewModel(
        Func<string, CancellationToken, Task<IReadOnlyList<GameMatch>>> search,
        IEnumerable<string> configuredIds)
    {
        _search = search;
        Populator = PopulateAsync;

        // Shown by id until their names come back.
        foreach (var id in configuredIds)
            ConfiguredGames.Add(new GameMatch(id, id));

        if (ConfiguredGames.Count > 0)
        {
            Choose(ConfiguredGames[0]);
            _ = NameConfiguredGamesAsync();
        }
    }

    partial void OnSelectedGameChanged(GameMatch? value)
    {
        if (value != null && !_choosingInternally)
            GameChosen?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Works out which game the typed text means.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The game, or null when nothing is typed or no game has that name.
    /// </returns>
    public async Task<GameMatch?> ResolveAsync(CancellationToken ct = default)
    {
        var text = Text.Trim();
        if (text.Length == 0)
            return null;

        if (SelectedGame != null && Matches(SelectedGame, text))
            return SelectedGame;

        var configured = ConfiguredGames.FirstOrDefault(g => Matches(g, text));
        if (configured != null)
            return configured;

        if (IsGameId(text))
            return new GameMatch(text, text);

        if (_search == null)
            return null;

        var found = await _search(text, ct);
        var best = found.FirstOrDefault(g => Matches(g, text)) ?? found.FirstOrDefault();
        if (best != null)
            Choose(best);

        return best;
    }

    private async Task<IEnumerable<object>> PopulateAsync(string? text, CancellationToken ct)
    {
        text = text?.Trim() ?? string.Empty;

        var configured = ConfiguredGames
            .Where(g => g.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || g.Id == text)
            .ToList();

        if (_search == null || (text.Length < MinSearchLength && !IsGameId(text)))
            return configured;

        IReadOnlyList<GameMatch> found;
        try
        {
            found = await _search(text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No suggestions is better than an error popup while typing.
            found = [];
        }

        return configured
            .Concat(found.Where(g => configured.All(c => c.Id != g.Id)))
            .ToList();
    }

    private async Task NameConfiguredGamesAsync()
    {
        for (var i = 0; i < ConfiguredGames.Count; i++)
        {
            var game = ConfiguredGames[i];
            GameMatch? named;
            try
            {
                named = (await _search!(game.Id, CancellationToken.None)).FirstOrDefault();
            }
            catch
            {
                // Left showing its id.
                continue;
            }

            if (named == null)
                continue;

            ConfiguredGames[i] = named;

            // Swap the name in only if the user has not typed over the preselected id.
            if (SelectedGame == game && Text == game.Name)
                Choose(named);
        }
    }

    private void Choose(GameMatch game)
    {
        _choosingInternally = true;
        try
        {
            SelectedGame = game;
            Text = game.Name;
        }
        finally
        {
            _choosingInternally = false;
        }
    }

    private static bool Matches(GameMatch game, string text) =>
        game.Id == text || string.Equals(game.Name, text, StringComparison.OrdinalIgnoreCase);

    private static bool IsGameId(string text) =>
        text.Length > 0 && text.All(char.IsAsciiDigit);
}
