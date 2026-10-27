using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Sdk.Compatibility;

namespace Modular.Core.Compatibility;

/// <summary>
/// Checks a mod's releases on its hub: anything uploaded since the game update counts as
/// a release for it.
/// </summary>
/// <remarks>
/// Upload date is the only signal every hub has. An upload after the update can still be
/// an unrelated fix, so <see cref="ModReleaseStatus.MentionsTargetVersion"/> says whether
/// any of the new files names the update's version.
/// </remarks>
public sealed class BackendReleaseCheck : IModReleaseCheck
{
    private readonly IReadOnlyList<IModBackend> _backends;

    /// <summary>Creates the check.</summary>
    /// <param name="backends">Backends to look mods up on.</param>
    public BackendReleaseCheck(IEnumerable<IModBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.ToList();
    }

    /// <inheritdoc />
    public bool CanCheck(InstalledMod mod) => mod.IsTraced && Find(mod) != null;

    /// <inheritdoc />
    public async Task<ModReleaseStatus> CheckAsync(InstalledMod mod, GameUpdateTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(target);

        var backend = Find(mod);
        if (backend == null || !mod.IsTraced)
            return new ModReleaseStatus(ReleaseVerdict.Unknown) { Note = "Not traced to a mod hub" };

        var files = (await backend.GetModFilesAsync(mod.ModId!, mod.GameDomain, ct: ct))
            .Where(f => f.UploadedAt != null)
            .ToList();

        if (files.Count == 0)
            return new ModReleaseStatus(ReleaseVerdict.Unknown) { Note = "The hub lists no files" };

        var latest = files.MaxBy(f => f.UploadedAt)!;
        var since = files.Where(f => Uploaded(f) >= target.ReleasedAt).ToList();
        var installed = mod.Versions.Select(NormalizeVersion).Where(v => v.Length > 0).ToHashSet();

        var verdict = since.Count == 0
            ? ReleaseVerdict.NoReleaseSinceTarget
            : since.Any(f => installed.Contains(NormalizeVersion(f.Version)))
                ? ReleaseVerdict.OnTargetRelease
                : ReleaseVerdict.UpdateAvailable;

        return new ModReleaseStatus(verdict)
        {
            LatestVersion = latest.Version,
            LatestUploadedAt = Uploaded(latest),
            MentionsTargetVersion = target.Version != null && since.Any(f => Names(f, target.Version)),
            Note = verdict == ReleaseVerdict.UpdateAvailable && installed.Count == 0
                ? "Installed version unknown"
                : null
        };
    }

    /// <summary>
    /// Puts a version in one form, so hub file names, which use dashes, compare equal to
    /// version fields, which use dots: <c>v1.2-3</c> becomes <c>1.2.3</c>.
    /// </summary>
    public static string NormalizeVersion(string? version) =>
        Regex.Replace((version ?? string.Empty).Trim().ToLowerInvariant(), "^v(?=\\d)", string.Empty).Replace('-', '.');

    private IModBackend? Find(InstalledMod mod) =>
        _backends.FirstOrDefault(b => string.Equals(b.Id, mod.BackendId, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset Uploaded(BackendModFile file) =>
        new(DateTime.SpecifyKind(file.UploadedAt!.Value, file.UploadedAt.Value.Kind == DateTimeKind.Unspecified
            ? DateTimeKind.Utc
            : file.UploadedAt.Value.Kind));

    // Whether a file's name says it is for the version: "2.5" but not "2.51" or "12.5".
    // The file's own version is left out, since a mod's version 2.5 says nothing about
    // the game's.
    private static bool Names(BackendModFile file, string version)
    {
        var pattern = $@"(?<![\d.]){Regex.Escape(version)}(?![\d])";
        return Regex.IsMatch(WithoutOwnVersion(file.FileName, file.Version), pattern) ||
               Regex.IsMatch(WithoutOwnVersion(file.DisplayName, file.Version), pattern);
    }

    // Hub file names usually carry the file's own version, "mod-2.5.zip", which is no
    // statement about the game's.
    private static string WithoutOwnVersion(string? text, string? ownVersion)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(ownVersion))
            return text ?? string.Empty;

        var own = Regex.Escape(ownVersion.Trim()).Replace(@"\.", @"[.\-_]");
        return Regex.Replace(text, $@"(?<![\d.])v?{own}(?![\d])", " ", RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// One core mod and where it stands against a game update.
/// </summary>
/// <param name="Entry">The core mod and why it is core.</param>
/// <param name="Status">Its releases against the update.</param>
public sealed record CoreModReadiness(CoreModEntry Entry, ModReleaseStatus Status);

/// <summary>
/// Where a game's core mods stand against an update.
/// </summary>
public sealed record GameUpdateReadinessReport
{
    /// <summary>The update checked against.</summary>
    public required GameUpdateTarget Target { get; init; }

    /// <summary>Each core mod with its release status, in the classifier's order.</summary>
    public IReadOnlyList<CoreModReadiness> Items { get; init; } = [];

    /// <summary>How many mods were looked at.</summary>
    public int ModsChecked { get; init; }

    /// <summary>Signals and lookups that failed, as messages fit to show.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Whether every core mod is on a release made for the update, or needs none.</summary>
    public bool Ready => Items.All(i => i.Status.Verdict is ReleaseVerdict.OnTargetRelease or ReleaseVerdict.NotAffected);
}

/// <summary>
/// Answers "which mods that others rely on are not yet on a release for this game
/// update": finds the core mods, then checks each one's releases.
/// </summary>
/// <remarks>
/// Both halves are open for extension: signals decide what is core, release checks
/// decide what counts as caught up. The first check that can look a mod up is used.
/// </remarks>
public sealed class GameUpdateReadinessService
{
    private readonly CoreModClassifier _classifier;
    private readonly IReadOnlyList<IModReleaseCheck> _checks;
    private readonly ILogger? _logger;

    /// <summary>Creates the service.</summary>
    public GameUpdateReadinessService(
        CoreModClassifier classifier,
        IEnumerable<IModReleaseCheck> checks,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(checks);
        _classifier = classifier;
        _checks = checks.ToList();
        _logger = logger;
    }

    /// <summary>Finds the core mods and checks each against the update.</summary>
    public async Task<GameUpdateReadinessReport> CheckAsync(
        CoreModContext context,
        GameUpdateTarget target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);

        var core = await _classifier.ClassifyAsync(context, ct);
        var sensitive = context.Module?.GetUpdateSensitivePaths()?.Select(PathPattern.Compile).ToList();
        var errors = core.Errors.ToList();
        var items = new List<CoreModReadiness>();

        foreach (var entry in core.Entries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(entry.Mod.Name);

            var check = _checks.FirstOrDefault(c => c.CanCheck(entry.Mod));
            ModReleaseStatus status;

            if (check == null)
            {
                status = new ModReleaseStatus(ReleaseVerdict.Unknown) { Note = "Not traced to a mod hub" };
            }
            else
            {
                try
                {
                    status = await check.CheckAsync(entry.Mod, target, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Release check failed for {Mod}", entry.Mod.Name);
                    errors.Add($"{entry.Mod.Name}: {ex.Message}");
                    status = new ModReleaseStatus(ReleaseVerdict.Unknown) { Note = "Lookup failed" };
                }
            }

            // Assets rarely break on an update, so a mod of only assets with nothing new
            // needs nothing. One with a new release still shows it: archives can carry
            // quest or scene data that an update does break.
            if (status.Verdict is ReleaseVerdict.NoReleaseSinceTarget or ReleaseVerdict.Unknown &&
                IsUnaffected(entry.Mod, sensitive))
            {
                status = status with
                {
                    Verdict = ReleaseVerdict.NotAffected,
                    Note = "Only files a game update rarely affects"
                };
            }

            items.Add(new CoreModReadiness(entry, status));
        }

        return new GameUpdateReadinessReport
        {
            Target = target,
            Items = items,
            ModsChecked = core.ModsChecked,
            Errors = errors
        };
    }

    // A mod is unaffected when its files are known and none of them is in a path the game
    // module says an update can break. With no files, or no module rules, it is checked.
    private static bool IsUnaffected(InstalledMod mod, IReadOnlyList<System.Text.RegularExpressions.Regex>? sensitive) =>
        sensitive != null &&
        mod.Files.Count > 0 &&
        !mod.Files.Any(f => sensitive.Any(p => p.IsMatch(f.Replace('\\', '/'))));
}
