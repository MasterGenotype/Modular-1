using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Fomod;

/// <summary>
/// Runs a FOMOD wizard without a window: walks its pages in order, takes each pick from
/// the caller's answers or the script's default, and collects the files the picks install.
/// </summary>
/// <remarks>
/// <para>
/// Every decision the wizard would show becomes an <see cref="InstallChoice"/>. A group
/// allowing one pick is one choice between its options ("None" included when none is
/// allowed); a group allowing several is one install-or-skip choice per option. Options
/// the script forces on, and groups with a single usable option, are decided without
/// asking.
/// </para>
/// <para>
/// Pages are walked in order with the flags set so far, so a page shown only for an
/// earlier pick appears among the choices once that pick is made. Callers that can ask
/// keep asking until no new choice turns up.
/// </para>
/// </remarks>
public static class FomodPlanner
{
    /// <summary>Option id for "install it" in an install-or-skip choice, labelled with the option's name.</summary>
    public const string Install = "install";

    /// <summary>Option id for "skip it" in an install-or-skip choice.</summary>
    public const string Skip = "skip";

    /// <summary>Option id for picking nothing in a group that allows it.</summary>
    public const string None = "none";

    /// <summary>Plans an install of <paramref name="config"/>.</summary>
    /// <param name="config">The script.</param>
    /// <param name="selections">Answers given so far, as choice id to option id.</param>
    /// <param name="fileState">The state of a game file, for file conditions.</param>
    /// <param name="countFiles">How many archive files a set of entries installs, for labels.</param>
    /// <returns>The choices met on the way and the files to install, lowest priority first.</returns>
    public static FomodPlan Plan(
        FomodModuleConfig config,
        IReadOnlyDictionary<string, string>? selections,
        Func<string, FomodFileState> fileState,
        Func<IEnumerable<FomodFile>, int> countFiles)
    {
        ArgumentNullException.ThrowIfNull(config);
        selections ??= new Dictionary<string, string>();

        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FomodFile>(config.RequiredFiles);
        var choices = new List<InstallChoice>();

        for (var s = 0; s < config.Steps.Count; s++)
        {
            var step = config.Steps[s];
            if (step.Visible != null && !step.Visible.Evaluate(flags, fileState))
                continue;

            for (var g = 0; g < step.Groups.Count; g++)
            {
                var group = step.Groups[g];

                // Ids carry the page and group positions: scripts reuse page names for
                // the branches of one question ("Zoom" after each style).
                var groupId = $"{s + 1}:{step.Name}/{g + 1}:{group.Name}";
                var title = string.Equals(step.Name, group.Name, StringComparison.OrdinalIgnoreCase)
                    ? group.Name
                    : $"{step.Name}: {group.Name}";

                var typed = group.Plugins
                    .Select(p => (Plugin: p, Type: TypeOf(p, flags, fileState)))
                    .ToList();
                var usable = typed.Where(t => t.Type != FomodPluginType.NotUsable).ToList();

                var picked = Pick(group, groupId, title, usable, selections, choices, countFiles);

                foreach (var (plugin, type) in typed)
                {
                    if (picked.Contains(plugin))
                    {
                        foreach (var flag in plugin.Flags)
                            flags[flag.Name] = flag.Value;
                        files.AddRange(plugin.Files);
                    }
                    else
                    {
                        files.AddRange(plugin.Files.Where(f =>
                            f.AlwaysInstall || (f.InstallIfUsable && type != FomodPluginType.NotUsable)));
                    }
                }
            }
        }

        foreach (var conditional in config.ConditionalInstalls)
        {
            if (conditional.Condition.Evaluate(flags, fileState))
                files.AddRange(conditional.Files);
        }

        return new FomodPlan
        {
            Choices = choices,
            // Stable, so entries of equal priority keep script order and the later wins.
            Files = files.OrderBy(f => f.Priority).ToList(),
            Flags = flags
        };
    }

    // Decides which of a group's usable options are picked, adding a choice for anything
    // the user could decide differently.
    private static HashSet<FomodPlugin> Pick(
        FomodGroup group,
        string groupId,
        string title,
        List<(FomodPlugin Plugin, FomodPluginType Type)> usable,
        IReadOnlyDictionary<string, string> selections,
        List<InstallChoice> choices,
        Func<IEnumerable<FomodFile>, int> countFiles)
    {
        var picked = new HashSet<FomodPlugin>();
        var required = usable.Where(u => u.Type == FomodPluginType.Required).Select(u => u.Plugin).ToList();

        switch (group.Type)
        {
            case FomodGroupType.SelectAll:
                picked.UnionWith(usable.Select(u => u.Plugin));
                break;

            case FomodGroupType.SelectExactlyOne:
            case FomodGroupType.SelectAtMostOne:
            {
                var allowNone = group.Type == FomodGroupType.SelectAtMostOne;

                if (required.Count > 0)
                {
                    picked.Add(required[0]);
                    break;
                }

                if (usable.Count == 0 || (usable.Count == 1 && !allowNone))
                {
                    picked.UnionWith(usable.Select(u => u.Plugin));
                    break;
                }

                var ids = OptionIds(usable.Select(u => u.Plugin));
                var options = usable.Select((u, i) => new InstallChoiceOption
                {
                    Id = ids[i],
                    Label = u.Plugin.Name,
                    FileCount = countFiles(u.Plugin.Files)
                }).ToList();

                if (allowNone)
                    options.Insert(0, new InstallChoiceOption { Id = None, Label = "None", FileCount = 0 });

                var recommended = usable.FindIndex(u => u.Type == FomodPluginType.Recommended);
                var fallback = recommended >= 0 ? ids[recommended] : allowNone ? None : ids[0];

                var choice = Choice(groupId, title, options, fallback, selections);
                choices.Add(choice);

                var index = Array.IndexOf(ids, choice.SelectedOptionId);
                if (index >= 0)
                    picked.Add(usable[index].Plugin);
                break;
            }

            default:
            {
                // SelectAny and SelectAtLeastOne: each option is its own yes or no.
                picked.UnionWith(required);
                var ids = OptionIds(usable.Select(u => u.Plugin));

                for (var i = 0; i < usable.Count; i++)
                {
                    var (plugin, type) = usable[i];
                    if (type == FomodPluginType.Required)
                        continue;

                    // The only option of an "at least one" group is not optional.
                    if (group.Type == FomodGroupType.SelectAtLeastOne && usable.Count == 1)
                    {
                        picked.Add(plugin);
                        continue;
                    }

                    // Labelled with the option's name, so naming it picks it.
                    var choice = Choice(
                        $"{groupId}/{ids[i]}",
                        $"{title}: install {plugin.Name}?",
                        [
                            new InstallChoiceOption { Id = Install, Label = plugin.Name, FileCount = countFiles(plugin.Files) },
                            new InstallChoiceOption { Id = Skip, Label = $"Skip {plugin.Name}", FileCount = 0 }
                        ],
                        type == FomodPluginType.Recommended ? Install : Skip,
                        selections);
                    choices.Add(choice);

                    if (choice.SelectedOptionId == Install)
                        picked.Add(plugin);
                }

                // "At least one" with everything skipped installs the first option rather
                // than breaking the script's promise.
                if (group.Type == FomodGroupType.SelectAtLeastOne && picked.Count == 0 && usable.Count > 0)
                    picked.Add(usable[0].Plugin);
                break;
            }
        }

        return picked;
    }

    private static InstallChoice Choice(
        string id,
        string title,
        List<InstallChoiceOption> options,
        string fallback,
        IReadOnlyDictionary<string, string> selections)
    {
        // An answer may name an option by id or by label, as typed on a command line.
        var given = selections.TryGetValue(id, out var answer)
            ? options.FirstOrDefault(o =>
                string.Equals(o.Id, answer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.Label, answer, StringComparison.OrdinalIgnoreCase))
            : null;

        return new InstallChoice
        {
            Id = id,
            Title = title,
            Options = options,
            SelectedOptionId = given?.Id ?? fallback,
            IsResolved = given != null
        };
    }

    // Option ids are the option names, numbered when a group repeats one.
    private static string[] OptionIds(IEnumerable<FomodPlugin> plugins)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return plugins
            .Select(p =>
            {
                var count = seen[p.Name] = seen.GetValueOrDefault(p.Name) + 1;
                return count == 1 ? p.Name : $"{p.Name} ({count})";
            })
            .ToArray();
    }

    private static FomodPluginType TypeOf(
        FomodPlugin plugin,
        IReadOnlyDictionary<string, string> flags,
        Func<string, FomodFileState> fileState) =>
        plugin.TypePatterns.FirstOrDefault(p => p.Condition.Evaluate(flags, fileState))?.Type
        ?? plugin.DefaultType;
}

/// <summary>The outcome of walking a FOMOD wizard.</summary>
public sealed class FomodPlan
{
    /// <summary>The decisions met, each with the option the plan was built with.</summary>
    public IReadOnlyList<InstallChoice> Choices { get; init; } = [];

    /// <summary>What to copy, lowest priority first, so later entries overwrite earlier.</summary>
    public IReadOnlyList<FomodFile> Files { get; init; } = [];

    /// <summary>The flags the picks set.</summary>
    public IReadOnlyDictionary<string, string> Flags { get; init; } = new Dictionary<string, string>();
}
