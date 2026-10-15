using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Switch.Totk.Helpers;
using Modular.Switch.Totk.IO;
using TkSharp;
using TkSharp.Core;
using TkSharp.Core.Models;
using TkSharp.Extensions.LibHac;
using TkSharp.IO.Writers;
using TkSharp.Merging;

namespace Modular.Switch.Totk;

/// <summary>A step of a TotK install, merge or export.</summary>
/// <param name="Phase">What is happening, e.g. <c>Merging</c>.</param>
/// <param name="Detail">The mod, file or folder involved, when there is one.</param>
public sealed record TotkProgress(string Phase, string? Detail = null);

/// <summary>
/// Tears of the Kingdom's installer: imports mods into a TkSharp store, diffs each
/// against the game (changelogs) and merges the enabled mods of a profile, in priority
/// order, into one output folder. Port of TKMM v2.0.0-rc1 <c>Tkmm.Core/TKMM.cs</c> on
/// TkSharp 797e76ec.
/// </summary>
/// <remarks>
/// <para>TkSharp keeps process-wide state (the changelog builder's ROM provider, the
/// profile <c>StateChanged</c> event, <c>TkLog</c>), so create one service per process.</para>
/// <para>Not ported from rc1: the TotK Optimizer, GameBanana, thumbnails, the legacy
/// <c>.data</c> upgrade and Windows registry/file associations.</para>
/// </remarks>
public sealed class TotkMergeService
{
    private static bool _loggerRegistered;

    private readonly TotkSettings _settings;
    private readonly TotkPaths _paths;
    private readonly ILogger? _logger;
    private readonly TkModReaderProvider _readerProvider;

    /// <summary>
    /// Opens (or creates) the mod store under <paramref name="paths"/> and reads the game
    /// as <paramref name="settings"/> describe. The settings object is read live, so
    /// later changes to it take effect; saving it is the caller's job.
    /// </summary>
    public TotkMergeService(TotkSettings settings, TotkPaths? paths = null, ILogger? logger = null)
    {
        _settings = settings;
        _paths = paths ?? TotkPaths.Default;
        _logger = logger;
        Snapshot = new TotkMergeSnapshot(_paths);

        if (logger != null && !_loggerRegistered)
        {
            TkLog.Instance.Register(logger);
            _loggerRegistered = true;
        }

        TkChangelogBuilder.Init(RomProvider);

        ModManager = TkModManager.Create(_paths.DataFolder);
        ModManager.CurrentProfile = ModManager.GetCurrentProfile();

        ModManager.PropertyChanged += OnModManagerPropertyChanged;

        _readerProvider = new TkModReaderProvider(ModManager, RomProvider);
        _readerProvider.Register(new External7zModReader(ModManager, RomProvider, _settings));
    }

    /// <summary>TkSharp's mod store: installed mods and profiles.</summary>
    public TkModManager ModManager { get; }

    /// <summary>The snapshot of the merge output taken before the latest merge.</summary>
    public TotkMergeSnapshot Snapshot { get; }

    /// <summary>The settings this service reads.</summary>
    public TotkSettings Settings => _settings;

    /// <summary>
    /// The folder merges write to: <see cref="TotkSettings.MergeOutput"/>, or
    /// <see cref="TotkPaths.DefaultMergeOutput"/> when that is empty.
    /// </summary>
    public string MergedOutputFolder => string.IsNullOrWhiteSpace(_settings.MergeOutput)
        ? _paths.DefaultMergeOutput
        : FileUtils.ExpandPath(_settings.MergeOutput);

    /// <summary>A new ROM provider for the current settings (rc1 builds one per use).</summary>
    private TkExtensibleRomProvider RomProvider => TotkRomProviderFactory.Create(_settings);

    /// <summary>Opens the game, throwing when it can't be read.</summary>
    public ITkRom GetTkRom() => RomProvider.GetRom();

    /// <summary>Opens the game, or returns null and why when it can't be read.</summary>
    public ITkRom? TryGetTkRom(out bool hasBaseGame, out bool hasUpdate, out string? error)
        => RomProvider.TryGetRom(out hasBaseGame, out hasUpdate, out error);

    // ── Install / uninstall ────────────────────────────────────────────────

    /// <summary>
    /// True when a reader exists for <paramref name="input"/>: a folder, .zip, .rar, .7z
    /// or .tkcl.
    /// </summary>
    public bool CanInstall(string input) => _readerProvider.CanRead(input);

    /// <summary>
    /// Reads the mod at <paramref name="input"/> (rc1 <c>TKMM.Install</c>) and imports it
    /// into <paramref name="profile"/> (the current profile by default). A mod with the
    /// same ID replaces the installed one.
    /// </summary>
    /// <returns>The mod, or null when no reader could read it (the reason is logged).</returns>
    public async ValueTask<TkMod?> InstallAsync(
        string input,
        TkProfile? profile = null,
        IProgress<TotkProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new TotkProgress("Reading", input));

        if (_readerProvider.GetReader(input) is not ITkModReader reader)
        {
            TkLog.Instance.LogError("Could not locate mod reader for input: '{Input}'", input);
            return null;
        }

        await using var stream = File.Exists(input) ? File.OpenRead(input) : null;

        if (await reader.ReadMod(input, stream, context: null, ct) is not TkMod mod)
        {
            TkLog.Instance.LogError("Failed to parse mod from input: '{Input}'", input);
            return null;
        }

        progress?.Report(new TotkProgress("Importing", mod.Name));
        ModManager.Import(mod, profile);
        return mod;
    }

    /// <summary>
    /// Removes <paramref name="mod"/> from the store and every profile (TkSharp
    /// <c>TkModManager.Uninstall</c>), then merges the current profile again.
    /// </summary>
    public async ValueTask UninstallAsync(TkMod mod, IProgress<TotkProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new TotkProgress("Uninstalling", mod.Name));
        ModManager.Uninstall(mod);
        await MergeAsync(progress: progress, ct: ct);
    }

    // ── Merge ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges the enabled mods of <paramref name="profile"/> (the current profile by
    /// default) into <paramref name="mergeOutput"/> (<see cref="MergedOutputFolder"/> by
    /// default). rc1 <c>TKMM.Merge</c>: deletes only <c>romfs</c>, <c>romfslite</c>,
    /// <c>exefs</c>, <c>cheats</c> and <c>romfs_metadata.bin</c> from the output, merges,
    /// and renames <c>romfs</c> to <c>romfslite</c> when <see cref="TotkSettings.UseRomfslite"/>
    /// is on. Before that, the output's merge targets are snapshotted for rollback.
    /// </summary>
    /// <param name="ipsOutputPath">Where IPS patches go; inside the output by default.</param>
    public async ValueTask MergeAsync(
        TkProfile? profile = null,
        string? ipsOutputPath = null,
        string? mergeOutput = null,
        IProgress<TotkProgress>? progress = null,
        CancellationToken ct = default)
    {
        profile ??= ModManager.GetCurrentProfile();
        mergeOutput ??= MergedOutputFolder;

        progress?.Report(new TotkProgress("Snapshotting", mergeOutput));
        Snapshot.Take(mergeOutput);

        progress?.Report(new TotkProgress("Cleaning", mergeOutput));
        TotkDirectoryHelper.DeleteTargetsFromDirectory(mergeOutput, TotkMergeSnapshot.MergeTargets, recursive: true);

        FolderModWriter writer = new(mergeOutput);

        using var tkRom = GetTkRom();
        TkMerger merger = new(writer, tkRom, _settings.GameLanguage, ipsOutputPath);

        var startTime = Stopwatch.GetTimestamp();

        progress?.Report(new TotkProgress("Merging", profile.Name));
        await merger.MergeAsync(GetMergeTargets(profile), ct);

        // For atmosphere 20.0 support with TotK Optimizer
        if (_settings.UseRomfslite)
        {
            try
            {
                var romfsPath = Path.Combine(mergeOutput, "romfs");
                if (Directory.Exists(romfsPath))
                    Directory.Move(romfsPath, Path.Combine(mergeOutput, "romfslite"));
            }
            catch (Exception ex)
            {
                TkLog.Instance.LogError(ex, "Failed to rename romfs to romfslite");
            }
        }

        var delta = Stopwatch.GetElapsedTime(startTime);
        TkLog.Instance.LogInformation("Elapsed time: {TotalMilliseconds}ms", delta.TotalMilliseconds);
        progress?.Report(new TotkProgress("Merged", mergeOutput));
    }

    /// <summary>
    /// Merges only cheats, exefs and subsdk (rc1 <c>TKMM.MergeBasic</c>), keeping the
    /// output's existing IPS patches. Runs whenever the current profile changes.
    /// </summary>
    public void MergeBasic(TkProfile? profile = null, CancellationToken ct = default)
    {
        TotkDirectoryHelper.DeleteTargetsFromDirectory(MergedOutputFolder, ["cheats", "exefs"],
            target => Path.GetExtension(target.AsSpan()) is not ".ips", recursive: true);

        ITkModWriter writer = new FolderModWriter(MergedOutputFolder);

        var startTime = Stopwatch.GetTimestamp();

        var targets = GetMergeTargets(profile)
            .ToArray();

        TkMerger.MergeCheats(writer, targets);
        TkMerger.MergeExeFs(writer, targets);
        TkMerger.MergeSubSdk(writer, targets);

        var delta = Stopwatch.GetElapsedTime(startTime);
        TkLog.Instance.LogInformation("Elapsed time: {TotalMilliseconds}ms", delta.TotalMilliseconds);
    }

    /// <summary>
    /// Puts back the merge output as it was before the latest merge.
    /// </summary>
    /// <returns>False when there is no snapshot of the current merge output.</returns>
    public bool RollbackMerge() => Snapshot.Restore(MergedOutputFolder);

    /// <summary>
    /// Points merges at <paramref name="path"/> (creating it if missing), or back at the
    /// default when it is null or empty. Neither the old nor the new folder is moved,
    /// deleted or overwritten; the next merge writes to the new one. Save the settings
    /// afterwards.
    /// </summary>
    public void SetMergeOutput(string? path)
    {
        _settings.MergeOutput = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(FileUtils.ExpandPath(path));
        Directory.CreateDirectory(MergedOutputFolder);
    }

    // ── Profiles and mods ──────────────────────────────────────────────────

    /// <summary>The selected profile.</summary>
    public TkProfile CurrentProfile => ModManager.GetCurrentProfile();

    /// <summary>Finds a profile by name (case-insensitive).</summary>
    public TkProfile? FindProfile(string name) =>
        ModManager.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds an empty profile named <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">A profile with that name exists.</exception>
    public TkProfile CreateProfile(string name)
    {
        if (FindProfile(name) != null)
            throw new InvalidOperationException($"A profile named '{name}' already exists.");

        var profile = new TkProfile { Name = name };
        ModManager.Profiles.Add(profile);
        ModManager.Save();
        return profile;
    }

    /// <summary>
    /// Makes <paramref name="profile"/> current, which runs <see cref="MergeBasic"/> as in
    /// rc1.
    /// </summary>
    public void SelectProfile(TkProfile profile) => ModManager.CurrentProfile = profile;

    /// <summary>
    /// Finds an installed mod by ID, or by name (case-insensitive) when exactly one mod
    /// has it.
    /// </summary>
    /// <exception cref="InvalidOperationException">More than one mod has that name.</exception>
    public TkMod? FindMod(string idOrName)
    {
        if (Ulid.TryParse(idOrName, out var id) && ModManager.Mods.FirstOrDefault(m => m.Id == id) is { } byId)
            return byId;

        var byName = ModManager.Mods.Where(m => m.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            0 => null,
            1 => byName[0],
            _ => throw new InvalidOperationException(
                $"{byName.Count} mods are named '{idOrName}'; use the ID instead: " +
                string.Join(", ", byName.Select(m => m.Id)))
        };
    }

    /// <summary><paramref name="mod"/>'s entry in <paramref name="profile"/>, if it has one.</summary>
    public static TkProfileMod? FindProfileMod(TkProfile profile, TkMod mod) =>
        profile.Mods.FirstOrDefault(pm => pm.Mod.Id == mod.Id);

    /// <summary>
    /// Moves <paramref name="mod"/> to <paramref name="index"/> (0 = top = highest
    /// priority) in <paramref name="profile"/>.
    /// </summary>
    public static void Move(TkProfile profile, TkProfileMod mod, int index)
    {
        var from = profile.Mods.IndexOf(mod);
        if (from < 0)
            throw new InvalidOperationException($"'{mod.Mod.Name}' is not in the profile '{profile.Name}'.");

        index = Math.Clamp(index, 0, profile.Mods.Count - 1);
        if (from != index)
            profile.Mods.Move(from, index);
    }

    /// <summary>
    /// Turns <paramref name="option"/> of <paramref name="group"/> on or off for
    /// <paramref name="mod"/>, with TkSharp's group rules (single groups switch the
    /// others off; required groups keep one).
    /// </summary>
    public static void SetOptionEnabled(TkProfile profile, TkProfileMod mod, TkModOption option, bool enabled)
    {
        profile.RebaseOptions(mod);
        option.IsEnabled = enabled;
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private void OnModManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TkModManager.CurrentProfile))
            MergeBasic();
    }

    /// <summary>
    /// rc1 <c>TKMM.GetMergeTargets</c> without the TotK Optimizer: every enabled mod's
    /// changelogs in priority order, after the "Mods Enabled" title-screen changelog.
    /// </summary>
    private IEnumerable<TkChangelog> GetMergeTargets(TkProfile? profile = null)
    {
        profile ??= ModManager.GetCurrentProfile();

        // rc1 filters out the Optimizer's mod here; TkModManager returns nothing at all
        // when given no predicate, so pass one that keeps everything.
        var targets = TkModManager.GetMergeTargets(profile, static _ => true);

        if (ModsEnabledMalsProvider.CreateDefaultMalsChangelog(_settings.GameLanguage) is { } defaultMalsChangelog)
            targets = targets.Prepend(defaultMalsChangelog);

        return targets;
    }
}
