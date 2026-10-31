using Modular.Core.Utilities;
using Modular.Sdk.Modules;

namespace Modular.Core.Deployment;

/// <summary>
/// Which of a game's two orders an operation is about.
/// </summary>
public enum OrderListKind
{
    /// <summary>
    /// Modular's install order: which mod's file is deployed when two mods ship the same one.
    /// </summary>
    Install,

    /// <summary>
    /// The game's load order: the order the game applies mods in, for games that have one
    /// apart from the install order.
    /// </summary>
    Load
}

/// <summary>
/// One entry in an order, as both front ends show it.
/// </summary>
/// <param name="Key">
/// What identifies the entry when setting an order: the mod id for the install order and
/// for a load order Modular keeps, the engine's own id for one the game keeps.
/// </param>
/// <param name="Name">Display name.</param>
/// <param name="ModId">The Modular mod the entry belongs to; <c>null</c> for one Modular did not install.</param>
/// <param name="Enabled">Whether the mod is deployed.</param>
/// <param name="Locked">Whether the entry has to stay where it is.</param>
public sealed record OrderItem(string Key, string Name, string? ModId, bool Enabled = true, bool Locked = false)
{
    /// <summary>Whether Modular installed this entry.</summary>
    public bool Managed => ModId != null;
}

/// <summary>
/// A game's install order and load order, first to last; the last wins in both.
/// </summary>
/// <param name="Kind">Whether the game has a load order of its own, and who keeps it.</param>
/// <param name="Install">The install order, every mod Modular has for the game.</param>
/// <param name="Load">
/// The load order. For <see cref="LoadOrderKind.None"/> it is the install order itself; for
/// <see cref="LoadOrderKind.External"/> it also lists entries Modular did not install.
/// </param>
/// <param name="Rule">How the game applies the load order, in one phrase.</param>
/// <param name="Mechanism">Where the load order is written, such as a file path.</param>
public sealed record GameOrders(
    LoadOrderKind Kind,
    IReadOnlyList<OrderItem> Install,
    IReadOnlyList<OrderItem> Load,
    string Rule,
    string? Mechanism);

/// <summary>
/// What a move did.
/// </summary>
/// <param name="Order">The order afterwards.</param>
/// <param name="Moved">The entries that moved.</param>
/// <param name="Changed">Whether anything actually moved.</param>
/// <param name="Deployment">The deployment that applied the change, when one ran.</param>
public sealed record OrderMoveResult(
    IReadOnlyList<OrderItem> Order,
    IReadOnlyList<OrderItem> Moved,
    bool Changed,
    DeploymentResult? Deployment);

/// <summary>
/// An order change that cannot be made, with a message fit to show the user.
/// </summary>
public sealed class LoadOrderException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong, for the user.</param>
    /// <param name="inner">The underlying error, when there is one.</param>
    public LoadOrderException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads and changes a game's install order and load order, for every game module.
/// </summary>
/// <remarks>
/// <para>
/// Every game has an install order, kept in <c>game_mod.order_index</c>. A game whose
/// module reports a <see cref="LoadOrderKind"/> other than <see cref="LoadOrderKind.None"/>
/// also has a load order, set separately: Modular keeps it in <c>load_index</c> and the
/// module renders it (<see cref="LoadOrderKind.Managed"/>), or the game keeps it in a file
/// of its own that the module edits in place (<see cref="LoadOrderKind.External"/>).
/// </para>
/// <para>
/// The two orders never reshuffle each other. A new mod takes a default place in each,
/// last in Modular's lists and the module's usual slot in a game's own, until it is moved.
/// </para>
/// </remarks>
public sealed class LoadOrderService
{
    private readonly DeploymentService _deployment;

    /// <summary>Creates the service.</summary>
    /// <param name="deployment">The deployment service of the database to work on.</param>
    public LoadOrderService(DeploymentService deployment)
    {
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
    }

    /// <summary>
    /// Reads a game's two orders.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Both orders, first to last.</returns>
    /// <exception cref="LoadOrderException">The game keeps a load order Modular cannot find.</exception>
    public async Task<GameOrders> GetAsync(GameInstallation installation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var module = _deployment.GetModule(installation);
        var records = await _deployment.Mods.ListAsync(installation.GameKey, enabledOnly: false, ct);
        var deployed = Deployed(installation, records);

        var install = records
            .Select(r => new OrderItem(r.ModId, r.DisplayName ?? r.ModId, r.ModId, r.Enabled))
            .ToList();

        IReadOnlyList<OrderItem> load;
        string? mechanism = null;

        switch (module.LoadOrderKind)
        {
            case LoadOrderKind.Managed:
                load = records
                    .OrderBy(r => r.LoadIndex ?? r.OrderIndex ?? 0)
                    .ThenBy(r => r.OrderIndex ?? 0)
                    .Select(r => new OrderItem(r.ModId, r.DisplayName ?? r.ModId, r.ModId, r.Enabled))
                    .ToList();
                break;

            case LoadOrderKind.External:
                var engine = module.ReadEngineOrder(installation, deployed)
                    ?? throw new LoadOrderException(
                        $"{installation.DisplayName ?? installation.GameId}'s load order file could not be found.");

                load = engine.Entries
                    .Select(e => new OrderItem(e.Id, e.Name, e.ModId, Enabled: true, e.Locked))
                    .ToList();
                mechanism = engine.Mechanism;
                break;

            default:
                load = install;
                break;
        }

        var description = EngineLoadOrder.Describe(module, installation, deployed);

        return new GameOrders(
            module.LoadOrderKind,
            install,
            load,
            description?.Rule ?? DefaultRule(module.LoadOrderKind),
            mechanism ?? description?.Mechanism);
    }

    /// <summary>
    /// Moves one entry, or one mod's entries, to a new place in one of a game's orders.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="list">Which order to change.</param>
    /// <param name="reference">
    /// What to move: a mod id, a name, a position such as <c>#3</c>, or, for a load order the
    /// game keeps, an entry's own id. Partial names match when only one entry fits.
    /// </param>
    /// <param name="placement">Where to put it; an anchor is a reference like <paramref name="reference"/>.</param>
    /// <param name="deploy">Whether to deploy afterwards, which a change to an order Modular renders needs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The order afterwards, and the deployment when one ran.</returns>
    /// <exception cref="LoadOrderException">
    /// The reference or anchor matches nothing or more than one entry, the entry is locked,
    /// the game has no load order of its own, or the game refused the order.
    /// </exception>
    /// <remarks>
    /// Giving a mod id for a load order the game keeps moves every entry that mod owns
    /// together, keeping their order among themselves. Any other reference moves just the
    /// entry it names.
    /// </remarks>
    public async Task<OrderMoveResult> MoveAsync(
        GameInstallation installation,
        OrderListKind list,
        string reference,
        OrderPlacement placement,
        bool deploy = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(placement);

        var orders = await GetAsync(installation, ct);
        var current = Select(orders, list, installation);

        var target = Resolve(current, reference);
        var block = list == OrderListKind.Load &&
                    orders.Kind == LoadOrderKind.External &&
                    target.ModId != null &&
                    string.Equals(reference.Trim(), target.ModId, StringComparison.OrdinalIgnoreCase)
            ? current.Where(i => string.Equals(i.ModId, target.ModId, StringComparison.OrdinalIgnoreCase)).ToList()
            : [target];

        if (block.FirstOrDefault(i => i.Locked) is { } locked)
            throw new LoadOrderException($"'{locked.Name}' has to stay where it is, so it cannot be moved.");

        // Locked entries lead the list and stay there; the rest is what moves around them.
        var lockedPrefix = current.TakeWhile(i => i.Locked).ToList();
        var movable = current.Skip(lockedPrefix.Count).ToList();

        var resolvedPlacement = placement;

        if (placement.Kind is PlacementKind.Before or PlacementKind.After)
        {
            var anchor = Resolve(current, placement.Anchor ?? string.Empty);

            if (anchor.Locked)
            {
                if (placement.Kind == PlacementKind.Before)
                    throw new LoadOrderException($"Nothing can load before '{anchor.Name}'.");

                resolvedPlacement = OrderPlacement.Top;
            }
            else
            {
                resolvedPlacement = placement with { Anchor = anchor.Key };
            }
        }
        else if (placement.Kind == PlacementKind.At)
        {
            resolvedPlacement = OrderPlacement.At(placement.Position - lockedPrefix.Count);
        }

        List<OrderItem> reordered;
        try
        {
            reordered = resolvedPlacement.Apply(movable, block, i => i.Key);
        }
        catch (ArgumentException ex)
        {
            throw new LoadOrderException(ex.Message, ex);
        }

        var order = lockedPrefix.Concat(reordered).ToList();
        var changed = !order.Select(i => i.Key).SequenceEqual(current.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);

        DeploymentResult? deployment = null;
        if (changed)
            deployment = await SetAsync(installation, list, order.Select(i => i.Key).ToList(), deploy, ct);

        return new OrderMoveResult(order, block, changed, deployment);
    }

    /// <summary>
    /// Sets one of a game's orders in full.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="list">Which order to set.</param>
    /// <param name="keys">Every entry's <see cref="OrderItem.Key"/>, first to last.</param>
    /// <param name="deploy">Whether to deploy afterwards.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deployment, when one ran.</returns>
    /// <exception cref="LoadOrderException">
    /// The game has no load order of its own, or the game refused the order.
    /// </exception>
    public async Task<DeploymentResult?> SetAsync(
        GameInstallation installation,
        OrderListKind list,
        IReadOnlyList<string> keys,
        bool deploy = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(keys);

        var module = _deployment.GetModule(installation);

        if (list == OrderListKind.Install)
        {
            await _deployment.Mods.SetOrderAsync(installation.GameKey, keys, ct);
        }
        else
        {
            switch (module.LoadOrderKind)
            {
                case LoadOrderKind.None:
                    throw new LoadOrderException(SingleOrderMessage(installation));

                case LoadOrderKind.Managed:
                    await _deployment.Mods.SetLoadOrderAsync(installation.GameKey, keys, ct);
                    break;

                case LoadOrderKind.External:
                    try
                    {
                        await module.SetEngineOrderAsync(installation, keys, ct);
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new LoadOrderException(ex.Message, ex);
                    }

                    // Modular's own record follows the game's list, so anything it derives
                    // from the load order, such as file name prefixes, agrees with it.
                    var records = await _deployment.Mods.ListAsync(installation.GameKey, enabledOnly: false, ct);
                    var engine = module.ReadEngineOrder(installation, Deployed(installation, records));
                    if (engine != null)
                    {
                        var modIds = engine.Entries
                            .Select(e => e.ModId)
                            .OfType<string>()
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        await _deployment.Mods.SetLoadOrderAsync(installation.GameKey, modIds, ct);
                    }

                    break;
            }
        }

        return deploy ? await _deployment.DeployAsync(installation, ct: ct) : null;
    }

    /// <summary>
    /// Finds the one entry a reference means.
    /// </summary>
    /// <param name="items">The order to look in.</param>
    /// <param name="reference">A key, mod id, name, position (<c>3</c> or <c>#3</c>), or part of a name.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="LoadOrderException">Nothing matches, or more than one entry does.</exception>
    public static OrderItem Resolve(IReadOnlyList<OrderItem> items, string reference)
    {
        ArgumentNullException.ThrowIfNull(items);

        var wanted = (reference ?? string.Empty).Trim();
        if (wanted.Length == 0)
            throw new LoadOrderException("No mod was named.");

        // Exact matches first: a name that happens to be a number still means that mod.
        var exact = items.Where(i =>
                string.Equals(i.Key, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.ModId, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count > 0)
            return One(exact, wanted);

        var number = wanted.TrimStart('#');
        if (int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position))
        {
            if (position < 1 || position > items.Count)
                throw new LoadOrderException($"There is no #{position}; the list has {items.Count} entries.");

            return items[position - 1];
        }

        var scored = items
            .Select(i => (Item: i, Score: new[] { i.Name, i.ModId ?? string.Empty, i.Key }
                .Max(t => FuzzyMatcher.Score(wanted, t))))
            .Where(x => x.Score >= 70)
            .ToList();

        if (scored.Count == 0)
            throw new LoadOrderException($"Nothing in the list matches '{wanted}'.");

        var best = scored.Max(x => x.Score);
        return One(scored.Where(x => x.Score == best).Select(x => x.Item).ToList(), wanted);
    }

    /// <summary>
    /// The message for asking a game without a load order of its own to change one.
    /// </summary>
    /// <param name="installation">The game.</param>
    /// <returns>The message.</returns>
    public static string SingleOrderMessage(GameInstallation installation) =>
        $"{installation.DisplayName ?? installation.GameId} applies mods in one order, the install order. " +
        "Change that order instead.";

    // Entries owned by one mod count as one match, so naming a mod with several entries
    // is not ambiguous.
    private static OrderItem One(List<OrderItem> matches, string wanted)
    {
        var distinct = matches
            .GroupBy(i => i.ModId ?? i.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 1)
            return matches[0];

        var names = distinct.Take(8).Select(g => $"'{g.First().Name}'");
        throw new LoadOrderException(
            $"'{wanted}' matches {distinct.Count} entries: {string.Join(", ", names)}" +
            (distinct.Count > 8 ? ", …" : string.Empty) + ". Be more specific.");
    }

    private static IReadOnlyList<OrderItem> Select(GameOrders orders, OrderListKind list, GameInstallation installation)
    {
        if (list == OrderListKind.Install)
            return orders.Install;

        if (orders.Kind == LoadOrderKind.None)
            throw new LoadOrderException(SingleOrderMessage(installation));

        return orders.Load;
    }

    private static string DefaultRule(LoadOrderKind kind) => kind switch
    {
        LoadOrderKind.None => "one order: the install order, later wins",
        LoadOrderKind.Managed => "kept by Modular, later wins",
        _ => "kept by the game, later wins"
    };

    // The game's mods as deployed right now, for a module that needs to look at their files.
    private List<DeployedMod> Deployed(GameInstallation installation, IReadOnlyList<GameModRecord> records)
    {
        var manifest = _deployment.LoadManifest(installation);
        var enabled = records.Where(r => r.Enabled).ToList();

        var loadRank = enabled
            .Select((r, i) => (r.ModId, Load: r.LoadIndex ?? r.OrderIndex ?? i, Index: i))
            .OrderBy(x => x.Load)
            .ThenBy(x => x.Index)
            .Select((x, rank) => (x.ModId, rank))
            .ToDictionary(x => x.ModId, x => x.rank, StringComparer.OrdinalIgnoreCase);

        return enabled
            .Select((r, i) => new DeployedMod
            {
                ModId = r.ModId,
                DisplayName = r.DisplayName,
                OrderIndex = i,
                LoadIndex = loadRank[r.ModId],
                Enabled = true,
                DeployedFiles = manifest.Files
                    .Where(f => string.Equals(f.ModId, r.ModId, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new StagedFile(f.Root, f.Path))
                    .ToList()
            })
            .ToList();
    }
}
