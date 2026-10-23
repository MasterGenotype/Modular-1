using System.Xml.Linq;

namespace Modular.Core.Installers.Fomod;

/// <summary>
/// A FOMOD installer script (<c>fomod/ModuleConfig.xml</c>): the files a mod always
/// installs, the wizard steps that let the user pick between alternatives, and the files
/// installed when the picks meet a condition.
/// </summary>
/// <remarks>
/// <para>
/// Element names are matched without their namespace, since authoring tools disagree on
/// whether to declare one. Paths keep the archive's spelling and may use either slash.
/// </para>
/// <para>
/// Where the schema leaves an order unstated it is <c>Ascending</c> by name, which is what
/// the reference installers do; <c>Explicit</c> keeps document order.
/// </para>
/// </remarks>
public sealed class FomodModuleConfig
{
    /// <summary>The mod's name as the script gives it, or null.</summary>
    public string? ModuleName { get; init; }

    /// <summary>Files installed whatever the user picks.</summary>
    public IReadOnlyList<FomodFile> RequiredFiles { get; init; } = [];

    /// <summary>The wizard's pages, in the order they are shown.</summary>
    public IReadOnlyList<FomodStep> Steps { get; init; } = [];

    /// <summary>Files installed when a condition on the picks holds.</summary>
    public IReadOnlyList<FomodConditionalInstall> ConditionalInstalls { get; init; } = [];

    /// <summary>Whether the script can install any file at all.</summary>
    public bool InstallsAnything =>
        RequiredFiles.Count > 0 ||
        ConditionalInstalls.Any(c => c.Files.Count > 0) ||
        Steps.Any(s => s.Groups.Any(g => g.Plugins.Any(p => p.Files.Count > 0)));

    /// <summary>Reads a <c>ModuleConfig.xml</c> document.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>The script; empty when the document has no root.</returns>
    public static FomodModuleConfig Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.Root;
        if (root == null)
            return new FomodModuleConfig();

        return new FomodModuleConfig
        {
            ModuleName = Child(root, "moduleName")?.Value.Trim(),
            RequiredFiles = ParseFiles(Child(root, "requiredInstallFiles")),
            Steps = Ordered(Child(root, "installSteps"), "installStep", ParseStep, s => s.Name),
            ConditionalInstalls = Children(Child(Child(root, "conditionalFileInstalls"), "patterns"), "pattern")
                .Select(p => new FomodConditionalInstall(
                    ParseCondition(Child(p, "dependencies")) ?? FomodCondition.Always,
                    ParseFiles(Child(p, "files"))))
                .ToList()
        };
    }

    private static FomodStep ParseStep(XElement step) => new()
    {
        Name = Attr(step, "name")?.Trim() ?? "Step",
        Visible = ParseCondition(Child(Child(step, "visible"), "dependencies")) ??
                  ParseCondition(Child(step, "visible")),
        Groups = Ordered(Child(step, "optionalFileGroups"), "group", ParseGroup, g => g.Name)
    };

    private static FomodGroup ParseGroup(XElement group) => new()
    {
        Name = Attr(group, "name")?.Trim() ?? "Options",
        Type = Enum.TryParse<FomodGroupType>(Attr(group, "type"), ignoreCase: true, out var type)
            ? type
            : FomodGroupType.SelectAny,
        Plugins = Ordered(Child(group, "plugins"), "plugin", ParsePlugin, p => p.Name)
    };

    private static FomodPlugin ParsePlugin(XElement plugin)
    {
        var descriptor = Child(plugin, "typeDescriptor");
        var dependencyType = Child(descriptor, "dependencyType");

        return new FomodPlugin
        {
            Name = Attr(plugin, "name")?.Trim() ?? "Option",
            Description = Child(plugin, "description")?.Value.Trim(),
            Files = ParseFiles(Child(plugin, "files")),
            Flags = Children(Child(plugin, "conditionFlags"), "flag")
                .Select(f => new FomodFlag(Attr(f, "name")?.Trim() ?? string.Empty, f.Value.Trim()))
                .Where(f => f.Name.Length > 0)
                .ToList(),
            DefaultType = ParseType(Attr(Child(descriptor, "type"), "name")) ??
                          ParseType(Attr(Child(dependencyType, "defaultType"), "name")) ??
                          FomodPluginType.Optional,
            TypePatterns = Children(Child(dependencyType, "patterns"), "pattern")
                .Select(p => (
                    Condition: ParseCondition(Child(p, "dependencies")),
                    Type: ParseType(Attr(Child(p, "type"), "name"))))
                .Where(p => p.Condition != null && p.Type != null)
                .Select(p => new FomodTypePattern(p.Condition!, p.Type!.Value))
                .ToList()
        };
    }

    private static List<FomodFile> ParseFiles(XElement? files) =>
        files == null
            ? []
            : files.Elements()
                .Where(e => e.Name.LocalName is "file" or "folder")
                .Select(e => new FomodFile(
                    Source: Attr(e, "source") ?? string.Empty,
                    Destination: Attr(e, "destination"),
                    IsFolder: e.Name.LocalName == "folder",
                    Priority: int.TryParse(Attr(e, "priority"), out var priority) ? priority : 0,
                    AlwaysInstall: IsTrue(Attr(e, "alwaysInstall")),
                    InstallIfUsable: IsTrue(Attr(e, "installIfUsable"))))
                .ToList();

    private static FomodCondition? ParseCondition(XElement? element)
    {
        if (element == null)
            return null;

        var parts = new List<FomodCondition>();
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "flagDependency":
                    parts.Add(new FomodFlagCondition(Attr(child, "flag")?.Trim() ?? string.Empty, Attr(child, "value")?.Trim() ?? string.Empty));
                    break;
                case "fileDependency":
                    parts.Add(new FomodFileCondition(
                        Attr(child, "file") ?? string.Empty,
                        Enum.TryParse<FomodFileState>(Attr(child, "state"), ignoreCase: true, out var state)
                            ? state
                            : FomodFileState.Active));
                    break;
                case "dependencies":
                    if (ParseCondition(child) is { } nested)
                        parts.Add(nested);
                    break;
                default:
                    // Game, script extender and manager versions: nothing here can be
                    // checked, and an installer that refused on them would refuse the mod.
                    parts.Add(FomodCondition.Always);
                    break;
            }
        }

        var isOr = string.Equals(Attr(element, "operator"), "Or", StringComparison.OrdinalIgnoreCase);
        return new FomodCompositeCondition(isOr, parts);
    }

    private static FomodPluginType? ParseType(string? name) =>
        Enum.TryParse<FomodPluginType>(name, ignoreCase: true, out var type) ? type : null;

    // Applies the container's order attribute: Explicit keeps document order, Ascending
    // (the default) and Descending sort by name.
    private static List<T> Ordered<T>(
        XElement? container, string childName, Func<XElement, T> parse, Func<T, string> name)
    {
        var items = Children(container, childName).Select(parse).ToList();

        return Attr(container, "order") switch
        {
            { } o when o.Equals("Explicit", StringComparison.OrdinalIgnoreCase) => items,
            { } o when o.Equals("Descending", StringComparison.OrdinalIgnoreCase) =>
                items.OrderByDescending(name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => items.OrderBy(name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static XElement? Child(XElement? element, string localName) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement? element, string localName) =>
        element?.Elements().Where(e => e.Name.LocalName == localName) ?? [];

    private static string? Attr(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
}

/// <summary>One page of the wizard.</summary>
public sealed class FomodStep
{
    /// <summary>The page's title. Not unique: scripts often repeat one per branch.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>When the page is shown, or null for always.</summary>
    public FomodCondition? Visible { get; init; }

    /// <summary>The page's groups of options.</summary>
    public IReadOnlyList<FomodGroup> Groups { get; init; } = [];
}

/// <summary>A group of options and the rule for how many of them may be picked.</summary>
public sealed class FomodGroup
{
    /// <summary>The group's caption.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How many options may be picked.</summary>
    public FomodGroupType Type { get; init; }

    /// <summary>The options.</summary>
    public IReadOnlyList<FomodPlugin> Plugins { get; init; } = [];
}

/// <summary>How many of a group's options may be picked.</summary>
public enum FomodGroupType
{
    /// <summary>Any number, including none.</summary>
    SelectAny,

    /// <summary>All of them; nothing to pick.</summary>
    SelectAll,

    /// <summary>Exactly one.</summary>
    SelectExactlyOne,

    /// <summary>One or none.</summary>
    SelectAtMostOne,

    /// <summary>One or more.</summary>
    SelectAtLeastOne
}

/// <summary>One option ("plugin") of a group.</summary>
public sealed class FomodPlugin
{
    /// <summary>The option's caption.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The script's description of the option.</summary>
    public string? Description { get; init; }

    /// <summary>Files installed when the option is picked.</summary>
    public IReadOnlyList<FomodFile> Files { get; init; } = [];

    /// <summary>Flags set when the option is picked, for later conditions to test.</summary>
    public IReadOnlyList<FomodFlag> Flags { get; init; } = [];

    /// <summary>The option's type when no pattern applies.</summary>
    public FomodPluginType DefaultType { get; init; }

    /// <summary>Types that apply while a condition holds, the first match winning.</summary>
    public IReadOnlyList<FomodTypePattern> TypePatterns { get; init; } = [];
}

/// <summary>How an option presents itself: forced on, suggested, available, or unavailable.</summary>
public enum FomodPluginType
{
    /// <summary>Always installed.</summary>
    Required,

    /// <summary>Available, off unless picked.</summary>
    Optional,

    /// <summary>Available and picked by default.</summary>
    Recommended,

    /// <summary>Cannot be picked.</summary>
    NotUsable,

    /// <summary>Available, with a caveat; treated as optional.</summary>
    CouldBeUsable
}

/// <summary>A file or folder to copy from the archive.</summary>
/// <param name="Source">Path in the archive, relative to the folder holding <c>fomod/</c>.</param>
/// <param name="Destination">Path in the game; null when the script gives none.</param>
/// <param name="IsFolder">Whether <paramref name="Source"/> is a folder copied with its contents.</param>
/// <param name="Priority">Higher wins when two entries write the same path.</param>
/// <param name="AlwaysInstall">Installed even when its option is not picked.</param>
/// <param name="InstallIfUsable">Installed whenever its option is usable, picked or not.</param>
public sealed record FomodFile(
    string Source,
    string? Destination,
    bool IsFolder,
    int Priority,
    bool AlwaysInstall,
    bool InstallIfUsable);

/// <summary>A flag an option sets.</summary>
public sealed record FomodFlag(string Name, string Value);

/// <summary>A type an option takes while a condition holds.</summary>
public sealed record FomodTypePattern(FomodCondition Condition, FomodPluginType Type);

/// <summary>Files installed while a condition holds.</summary>
public sealed record FomodConditionalInstall(FomodCondition Condition, IReadOnlyList<FomodFile> Files);

/// <summary>The states a file condition can require.</summary>
public enum FomodFileState
{
    /// <summary>The file is present and in use.</summary>
    Active,

    /// <summary>The file is present but not in use.</summary>
    Inactive,

    /// <summary>The file is absent.</summary>
    Missing
}

/// <summary>A condition on the picks so far and on the game's files.</summary>
public abstract record FomodCondition
{
    /// <summary>A condition that always holds.</summary>
    public static FomodCondition Always { get; } = new FomodCompositeCondition(false, []);

    /// <summary>Evaluates the condition.</summary>
    /// <param name="flags">Flags set by the options picked so far.</param>
    /// <param name="fileState">The state of a game file, by its path.</param>
    /// <returns>Whether the condition holds.</returns>
    public abstract bool Evaluate(
        IReadOnlyDictionary<string, string> flags, Func<string, FomodFileState> fileState);
}

/// <summary>Holds when a flag has a value. A flag never set has the value "".</summary>
public sealed record FomodFlagCondition(string Flag, string Value) : FomodCondition
{
    /// <inheritdoc />
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> flags, Func<string, FomodFileState> fileState) =>
        string.Equals(
            flags.TryGetValue(Flag, out var actual) ? actual : string.Empty,
            Value,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>Holds when a game file is in a state.</summary>
public sealed record FomodFileCondition(string File, FomodFileState State) : FomodCondition
{
    /// <inheritdoc />
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> flags, Func<string, FomodFileState> fileState) =>
        fileState(File) == State;
}

/// <summary>All of its parts, or with <c>operator="Or"</c> any of them. Empty holds.</summary>
public sealed record FomodCompositeCondition(bool IsOr, IReadOnlyList<FomodCondition> Parts) : FomodCondition
{
    /// <inheritdoc />
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> flags, Func<string, FomodFileState> fileState) =>
        Parts.Count == 0 ||
        (IsOr
            ? Parts.Any(p => p.Evaluate(flags, fileState))
            : Parts.All(p => p.Evaluate(flags, fileState)));
}
