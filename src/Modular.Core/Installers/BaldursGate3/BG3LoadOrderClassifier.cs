namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// A band in the pak load order.
/// </summary>
/// <param name="Rank">Two-digit sort key; lower loads earlier.</param>
/// <param name="Name">Short name of the band.</param>
public readonly record struct BG3LoadTier(int Rank, string Name);

/// <summary>
/// Places a pak in a load-order band, so that similar mods sit together and a mod that
/// defines something loads before the mods that build on it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here reads a file or archive name. A name says what an author felt like
/// calling a mod; two better sources say what it is. The category NexusMods files the mod
/// under is the author's own statement, so it decides when it is known. Otherwise the
/// pak's contents decide: a pak that packs <c>Races.lsx</c> defines a race whatever it is
/// called.
/// </para>
/// <para>
/// Bands only order mods that have no stated relationship. A declared dependency always
/// wins over a band, and that is applied where the order is written.
/// </para>
/// </remarks>
public static class BG3LoadOrderClassifier
{
    /// <summary>Libraries and tools other mods are built on.</summary>
    public static readonly BG3LoadTier Framework = new(10, "framework");

    /// <summary>Interface changes.</summary>
    public static readonly BG3LoadTier Interface = new(20, "interface");

    /// <summary>Playable races and their features.</summary>
    public static readonly BG3LoadTier Race = new(30, "race");

    /// <summary>Classes, subclasses and spells.</summary>
    public static readonly BG3LoadTier Class = new(40, "class");

    /// <summary>Everything without a more specific band.</summary>
    public static readonly BG3LoadTier Gameplay = new(50, "gameplay");

    /// <summary>Equipment and other items.</summary>
    public static readonly BG3LoadTier Item = new(60, "item");

    /// <summary>Character appearance: heads, hair, textures.</summary>
    public static readonly BG3LoadTier Appearance = new(70, "appearance");

    /// <summary>Mods that exist to adjust other mods.</summary>
    public static readonly BG3LoadTier Patch = new(90, "patch");

    // NexusMods' category names for Baldur's Gate 3, as its API returns them.
    private static readonly Dictionary<string, BG3LoadTier> NexusCategories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Utilities"] = Framework,
            ["Resources"] = Framework,
            ["User Interface"] = Interface,
            ["Photo Mode"] = Interface,
            ["Dice"] = Interface,
            ["Races"] = Race,
            ["Classes"] = Class,
            ["Spells"] = Class,
            ["Gameplay"] = Gameplay,
            ["Quests"] = Gameplay,
            ["Companions"] = Gameplay,
            ["Maps"] = Gameplay,
            ["Audio"] = Gameplay,
            ["Animations"] = Gameplay,
            ["Miscellaneous"] = Gameplay,
            ["Equipment"] = Item,
            ["Armor"] = Item,
            ["Weapons"] = Item,
            ["Clothing"] = Item,
            ["Accessories"] = Item,
            ["Character Customisation"] = Appearance,
            ["Visuals"] = Appearance,
        };

    // Stat files that define items, as opposed to passives, statuses or spells.
    private static readonly string[] ItemStatFiles = ["/Armor.txt", "/Weapon.txt", "/Object.txt"];

    /// <summary>
    /// Places a pak in a band.
    /// </summary>
    /// <param name="profile">The pak's metadata and contents, when it could be read.</param>
    /// <param name="nexusCategory">The category NexusMods files the mod under, when known.</param>
    /// <returns>
    /// The band for the category when there is one, otherwise the band the contents point
    /// to, otherwise <see cref="Gameplay"/>.
    /// </returns>
    public static BG3LoadTier Classify(BG3PakProfile? profile, string? nexusCategory = null)
    {
        var tier = FromNexusCategory(nexusCategory)
                   ?? (profile != null ? FromContents(profile) : Gameplay);

        // A mod with nothing more specific to say for itself, which depends on another
        // mod, is there to change that mod.
        if (tier == Gameplay && profile?.Meta?.ModDependencies.Any() == true)
            return Patch;

        return tier;
    }

    /// <summary>
    /// Maps a NexusMods category onto a band.
    /// </summary>
    /// <param name="category">Category name as the API returns it.</param>
    /// <returns>The band, or <c>null</c> for no category or one that is not known.</returns>
    public static BG3LoadTier? FromNexusCategory(string? category) =>
        !string.IsNullOrWhiteSpace(category) &&
        NexusCategories.TryGetValue(category.Trim(), out var tier)
            ? tier
            : null;

    /// <summary>
    /// Places a pak in a band from what it packs.
    /// </summary>
    /// <param name="profile">The pak's metadata and contents.</param>
    /// <returns>The band its contents point to.</returns>
    /// <remarks>
    /// Checked from the most defining content down. Most race and class mods also ship
    /// character-creation entries and items, so those must not be what decides.
    /// </remarks>
    public static BG3LoadTier FromContents(BG3PakProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.HasPath("/Races/Races.lsx"))
            return Race;

        if (profile.HasPath("/ClassDescriptions/"))
            return Class;

        if (profile.HasPath("/CharacterCreation/") || profile.HasPath("/CharacterCreationPresets/"))
            return Appearance;

        var hasStats = profile.HasPath("/Stats/Generated/");

        if (ItemStatFiles.Any(profile.HasPath) || profile.HasPath("/TreasureTable.txt"))
            return Item;

        if (profile.HasPath("/Stats/Generated/Data/Spell_"))
            return Class;

        // Models and textures with no rules attached are a visual replacement or addition.
        if (!hasStats && !profile.HasPath("/Progressions/") &&
            (profile.HasPath("/Content/") || profile.Files.Any(f =>
                f.StartsWith("Generated/", StringComparison.OrdinalIgnoreCase))))
        {
            return Appearance;
        }

        // Nearly every pak carries a thumbnail under GUI/, so only a pak that is nothing
        // but interface files counts as an interface mod.
        if (profile.Files.Count > 0 && profile.Files.All(IsInterfaceOrBookkeeping) &&
            profile.HasPath("/GUI/"))
        {
            return Interface;
        }

        return Gameplay;
    }

    private static bool IsInterfaceOrBookkeeping(string path) =>
        path.Contains("/GUI/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/Localization/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("Localization/", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/meta.lsx", StringComparison.OrdinalIgnoreCase);
}
