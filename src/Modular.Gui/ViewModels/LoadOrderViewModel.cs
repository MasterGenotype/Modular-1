using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Sdk.Modules;

namespace Modular.Gui.ViewModels;

/// <summary>
/// The Load Order tab: a game's install order and load order, side by side, each
/// reorderable, applied together.
/// </summary>
/// <remarks>
/// Edits are staged in the two lists until Apply, which hands both to
/// <see cref="LoadOrderService"/>, the same code the <c>order</c> commands use. Every move
/// goes through <see cref="OrderPlacement.Apply{T}"/>, so the tab and the CLI agree on what
/// a move means.
/// </remarks>
public partial class LoadOrderViewModel : ViewModelBase
{
    private readonly ModInstallationService? _installService;

    // The orders as last read, to tell staged edits from the stored state.
    private List<string> _storedInstall = [];
    private List<string> _storedLoad = [];
    private GameInstallation? _installation;

    [ObservableProperty]
    private ObservableCollection<LoadOrderGameOption> _games = new();

    [ObservableProperty]
    private LoadOrderGameOption? _selectedGame;

    [ObservableProperty]
    private ObservableCollection<OrderRowModel> _installItems = new();

    [ObservableProperty]
    private ObservableCollection<OrderRowModel> _loadItems = new();

    [ObservableProperty]
    private OrderRowModel? _selectedInstallItem;

    [ObservableProperty]
    private OrderRowModel? _selectedLoadItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadOrder))]
    [NotifyPropertyChangedFor(nameof(SingleOrderNote))]
    private LoadOrderKind _kind;

    [ObservableProperty]
    private string _ruleText = string.Empty;

    [ObservableProperty]
    private string _installPositionText = string.Empty;

    [ObservableProperty]
    private string _loadPositionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Whether the game has a load order apart from the install order.</summary>
    public bool HasLoadOrder => Kind != LoadOrderKind.None;

    /// <summary>What to show in place of the load order for a game that has only one order.</summary>
    public string SingleOrderNote =>
        "This game applies mods in one order: the install order on the left decides everything.";

    /// <summary>Whether there are staged edits that can be applied now.</summary>
    public bool CanApply => IsDirty && !IsBusy;

    // Designer constructor
    public LoadOrderViewModel()
    {
        Games.Add(new LoadOrderGameOption("1086940", "Baldur's Gate 3"));
        SelectedGame = Games[0];
        Kind = LoadOrderKind.External;
        RuleText = "the order listed in modsettings.lsx; a later mod wins";
        InstallItems.Add(new OrderRowModel(1, new OrderItem("sbbf", "SBBF (pak)", "sbbf")));
        InstallItems.Add(new OrderRowModel(2, new OrderItem("tails", "Better Tails (pak)", "tails")));
        LoadItems.Add(new OrderRowModel(1, new OrderItem("gx", "GustavX", null, Locked: true)));
        LoadItems.Add(new OrderRowModel(2, new OrderItem("u1", "SBBF (pak)", "sbbf")));
        LoadItems.Add(new OrderRowModel(3, new OrderItem("u2", "Better Tails (pak)", "tails")));
    }

    // DI constructor
    public LoadOrderViewModel(ModInstallationService installService)
    {
        _installService = installService;
        _ = LoadGamesAsync();
    }

    partial void OnSelectedGameChanged(LoadOrderGameOption? value) => _ = RefreshAsync();

    partial void OnSelectedInstallItemChanged(OrderRowModel? value) => Link(value);

    partial void OnSelectedLoadItemChanged(OrderRowModel? value) => Link(value);

    /// <summary>Lists the games Modular has mods for.</summary>
    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        if (_installService == null)
            return;

        try
        {
            var previous = SelectedGame?.Key;
            var options = new List<LoadOrderGameOption>();

            foreach (var key in await _installService.Deployment.Mods.ListGameKeysAsync())
            {
                var installation = await _installService.Deployment.ResolveInstallationAsync(key);
                options.Add(new LoadOrderGameOption(key, installation?.DisplayName ?? key));
            }

            Games.Clear();
            foreach (var option in options.OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase))
                Games.Add(option);

            SelectedGame = Games.FirstOrDefault(g => g.Key == previous) ?? Games.FirstOrDefault();

            if (Games.Count == 0)
                StatusMessage = "No game has mods installed by Modular yet.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>Reads both orders again, discarding staged edits.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_installService == null || SelectedGame == null)
            return;

        IsBusy = true;
        try
        {
            _installation = await _installService.Deployment.ResolveInstallationAsync(SelectedGame.Key);
            if (_installation == null)
            {
                StatusMessage = $"Could not resolve {SelectedGame.DisplayName}.";
                return;
            }

            var orders = await _installService.LoadOrder.GetAsync(_installation);

            Kind = orders.Kind;
            RuleText = orders.Mechanism != null ? $"{orders.Rule} — {orders.Mechanism}" : orders.Rule;

            Fill(InstallItems, orders.Install);
            Fill(LoadItems, orders.Kind == LoadOrderKind.None ? [] : orders.Load);

            _storedInstall = InstallItems.Select(i => i.Item.Key).ToList();
            _storedLoad = LoadItems.Select(i => i.Item.Key).ToList();
            IsDirty = false;
            StatusMessage = $"{InstallItems.Count} mods" +
                            (HasLoadOrder ? $", {LoadItems.Count} load-order entries" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Discards staged edits.</summary>
    [RelayCommand]
    private Task RevertAsync() => RefreshAsync();

    /// <summary>Writes both orders and deploys.</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_installService == null || _installation == null || !IsDirty)
            return;

        var installKeys = InstallItems.Select(i => i.Item.Key).ToList();
        var loadKeys = LoadItems.Select(i => i.Item.Key).ToList();
        var installChanged = !installKeys.SequenceEqual(_storedInstall, StringComparer.OrdinalIgnoreCase);
        var loadChanged = HasLoadOrder && !loadKeys.SequenceEqual(_storedLoad, StringComparer.OrdinalIgnoreCase);

        IsBusy = true;
        StatusMessage = "Applying…";

        try
        {
            var service = _installService.LoadOrder;
            DeploymentResult? deployment = null;

            if (installChanged)
                await service.SetAsync(_installation, OrderListKind.Install, installKeys, deploy: false);

            // A load order Modular keeps is stored and then rendered by the one deploy; one
            // the game keeps is written after it, so the file has every entry it lists.
            if (loadChanged && Kind == LoadOrderKind.Managed)
                await service.SetAsync(_installation, OrderListKind.Load, loadKeys, deploy: false);

            if (installChanged || (loadChanged && Kind == LoadOrderKind.Managed))
                deployment = await _installService.Deployment.DeployAsync(_installation);

            if (loadChanged && Kind == LoadOrderKind.External)
                deployment = await service.SetAsync(_installation, OrderListKind.Load, loadKeys, deploy: true);

            await RefreshAsync();
            StatusMessage = deployment is { Success: false }
                ? $"Order saved, but the deploy failed: {deployment.Error}"
                : "Applied.";
        }
        catch (LoadOrderException ex)
        {
            // Whatever was written before the refusal is the state now; show it, and why.
            await RefreshAsync();
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void InstallTop() => Move(InstallItems, SelectedInstallItem, OrderPlacement.Top);

    [RelayCommand]
    private void InstallUp() => Step(InstallItems, SelectedInstallItem, -1);

    [RelayCommand]
    private void InstallDown() => Step(InstallItems, SelectedInstallItem, +1);

    [RelayCommand]
    private void InstallBottom() => Move(InstallItems, SelectedInstallItem, OrderPlacement.Bottom);

    [RelayCommand]
    private void InstallMoveTo() => MoveTo(InstallItems, SelectedInstallItem, InstallPositionText);

    [RelayCommand]
    private void LoadTop() => Move(LoadItems, SelectedLoadItem, OrderPlacement.Top);

    [RelayCommand]
    private void LoadUp() => Step(LoadItems, SelectedLoadItem, -1);

    [RelayCommand]
    private void LoadDown() => Step(LoadItems, SelectedLoadItem, +1);

    [RelayCommand]
    private void LoadBottom() => Move(LoadItems, SelectedLoadItem, OrderPlacement.Bottom);

    [RelayCommand]
    private void LoadMoveTo() => MoveTo(LoadItems, SelectedLoadItem, LoadPositionText);

    /// <summary>
    /// Moves a dragged row onto another row's place, for drag and drop.
    /// </summary>
    /// <param name="dragged">The row being dragged.</param>
    /// <param name="target">The row it was dropped on.</param>
    public void DropOnto(OrderRowModel dragged, OrderRowModel target)
    {
        var list = InstallItems.Contains(dragged) ? InstallItems : LoadItems;
        if (!list.Contains(target) || ReferenceEquals(dragged, target))
            return;

        var from = list.IndexOf(dragged);
        var to = list.IndexOf(target);

        // Dropping on a row below takes its place by going after it; above, before it.
        var placement = to > from
            ? OrderPlacement.After(target.Item.Key)
            : OrderPlacement.Before(target.Item.Key);

        Move(list, dragged, placement);
    }

    private void Step(ObservableCollection<OrderRowModel> list, OrderRowModel? row, int delta)
    {
        if (row == null)
            return;

        Move(list, row, OrderPlacement.At(list.IndexOf(row) + 1 + delta));
    }

    private void MoveTo(ObservableCollection<OrderRowModel> list, OrderRowModel? row, string text)
    {
        if (row == null)
            return;

        if (!int.TryParse(text.Trim().TrimStart('#'), out var position) || position < 1)
        {
            StatusMessage = "Type a position from 1 up.";
            return;
        }

        Move(list, row, OrderPlacement.At(position));
    }

    // Applies one placement to a staged list, keeping locked rows at the top.
    private void Move(ObservableCollection<OrderRowModel> list, OrderRowModel? row, OrderPlacement placement)
    {
        if (row == null)
            return;

        if (row.Item.Locked)
        {
            StatusMessage = $"'{row.Item.Name}' has to stay where it is.";
            return;
        }

        var locked = list.TakeWhile(r => r.Item.Locked).ToList();
        var movable = list.Skip(locked.Count).ToList();

        if (placement.Kind == PlacementKind.At)
            placement = OrderPlacement.At(Math.Max(1, placement.Position - locked.Count));
        else if (placement.Kind == PlacementKind.Before && locked.Any(r => r.Item.Key == placement.Anchor))
            placement = OrderPlacement.Top;
        else if (placement.Kind == PlacementKind.After && locked.Any(r => r.Item.Key == placement.Anchor))
            placement = OrderPlacement.Top;

        List<OrderRowModel> reordered;
        try
        {
            reordered = placement.Apply(movable, [row], r => r.Item.Key);
        }
        catch (ArgumentException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        var result = locked.Concat(reordered).ToList();
        list.Clear();
        for (var i = 0; i < result.Count; i++)
        {
            result[i].Position = i + 1;
            list.Add(result[i]);
        }

        if (ReferenceEquals(list, InstallItems))
            SelectedInstallItem = row;
        else
            SelectedLoadItem = row;

        IsDirty =
            !InstallItems.Select(i => i.Item.Key).SequenceEqual(_storedInstall, StringComparer.OrdinalIgnoreCase) ||
            !LoadItems.Select(i => i.Item.Key).SequenceEqual(_storedLoad, StringComparer.OrdinalIgnoreCase);
        StatusMessage = IsDirty ? "Changes not applied yet." : string.Empty;
    }

    // Highlights the rows in both lists that belong to the selected row's mod.
    private void Link(OrderRowModel? selected)
    {
        var modId = selected?.Item.ModId;

        foreach (var row in InstallItems.Concat(LoadItems))
        {
            row.IsLinked = modId != null &&
                           string.Equals(row.Item.ModId, modId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void Fill(ObservableCollection<OrderRowModel> target, IReadOnlyList<OrderItem> items)
    {
        target.Clear();
        for (var i = 0; i < items.Count; i++)
            target.Add(new OrderRowModel(i + 1, items[i]));
    }
}

/// <summary>
/// A game the Load Order tab can show.
/// </summary>
/// <param name="Key">Game key.</param>
/// <param name="DisplayName">Name shown in the selector.</param>
public sealed record LoadOrderGameOption(string Key, string DisplayName);

/// <summary>
/// One row of an order in the Load Order tab.
/// </summary>
public partial class OrderRowModel : ObservableObject
{
    /// <summary>Creates a row.</summary>
    /// <param name="position">1-based position.</param>
    /// <param name="item">The entry.</param>
    public OrderRowModel(int position, OrderItem item)
    {
        _position = position;
        Item = item;
    }

    /// <summary>The entry this row shows.</summary>
    public OrderItem Item { get; }

    /// <summary>1-based position in its list.</summary>
    [ObservableProperty]
    private int _position;

    /// <summary>Whether the row belongs to the mod selected in either list.</summary>
    [ObservableProperty]
    private bool _isLinked;

    /// <summary>A short note: locked, not Modular's, or disabled.</summary>
    public string Badge =>
        Item.Locked ? "locked" :
        !Item.Managed ? "not Modular's" :
        !Item.Enabled ? "disabled" :
        string.Empty;

    /// <summary>Whether there is a badge to show.</summary>
    public bool HasBadge => Badge.Length > 0;
}
