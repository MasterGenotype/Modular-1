using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Downloads;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Core.Backends.ModIo;

/// <summary>
/// mod.io backend: the user's subscriptions, search, files and dependencies.
/// </summary>
/// <remarks>
/// <para>
/// mod.io is the hub behind several games' in-game mod managers, Baldur's Gate 3 among
/// them. A game there is a number (6715 for Baldur's Gate 3), and that number is what this
/// backend takes wherever another backend takes a game domain. With no game given, the
/// games in <see cref="AppSettings.ModIoGameIds"/> are used.
/// </para>
/// <para>
/// A mod has many uploaded files but one live file, the one the game installs. The live
/// file is reported as category "main" and every other upload as "old_version", so the
/// usual main-and-optional filter picks exactly what the game would.
/// </para>
/// </remarks>
public sealed class ModIoBackend : IModBackend, ISearchableBackend, IRequirementsBackend, ITrackingBackend, IDisposable
{
    /// <summary>Category given to a mod's live file.</summary>
    public const string LiveFileCategory = "main";

    /// <summary>Category given to every other upload.</summary>
    public const string OldFileCategory = "old_version";

    // mod.io marks explicit content with bit 8 of maturity_option.
    private const int ExplicitMaturityBit = 8;

    // Baldur's Gate 3, whose host answers game searches when no game is configured.
    private const long DirectoryHostGame = 6715;

    private readonly AppSettings _settings;
    private readonly ModIoClient _client;
    private readonly ResumableDownloader _downloader;
    private readonly ILogger<ModIoBackend>? _logger;

    // Which game each mod belongs to, learned from every response, so a mod id alone is
    // enough once the mod has been seen.
    private readonly ConcurrentDictionary<string, long> _gameOfMod = new(StringComparer.Ordinal);

    // Each game's name, for naming its folder in the mods directory.
    private readonly ConcurrentDictionary<long, string> _gameNames = new();

    // Each game's visible tag groups, in the game's order; the first one is its categories.
    private readonly ConcurrentDictionary<long, IReadOnlyList<ModIoTagOption>> _tagGroups = new();

    /// <summary>
    /// Creates the backend.
    /// </summary>
    /// <param name="settings">Application settings, read on every request.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="downloader">Shared resumable downloader.</param>
    /// <param name="client">The API client; created from the settings when omitted.</param>
    public ModIoBackend(
        AppSettings settings,
        ILogger<ModIoBackend>? logger = null,
        ResumableDownloader? downloader = null,
        ModIoClient? client = null)
    {
        _settings = settings;
        _logger = logger;
        _downloader = downloader ?? new ResumableDownloader(logger: null);
        _client = client ?? new ModIoClient(
            () => _settings.ModIoApiKey,
            () => _settings.ModIoAccessToken,
            () => _settings.ModIoPlatform,
            logger: logger);
    }

    /// <inheritdoc />
    public string Id => BackendIds.ModIo;

    /// <inheritdoc />
    public string DisplayName => "mod.io";

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="BackendCapabilities.GameDomains"/> is left out on purpose: the CLI would
    /// then prompt for a game every time, though the configured games already answer it.
    /// A game id passed as the domain is still honoured.
    /// </remarks>
    public BackendCapabilities Capabilities =>
        BackendCapabilities.Md5Verification |
        BackendCapabilities.Authentication |
        BackendCapabilities.Search |
        BackendCapabilities.Requirements |
        BackendCapabilities.Tracking;

    /// <inheritdoc />
    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();

        if (!_client.HasCredentials)
        {
            errors.Add(
                "mod.io API key or access token is not configured. Create one at https://mod.io/me/access " +
                "and set 'modio_api_key' or 'modio_access_token' in config, or MODIO_API_KEY / MODIO_ACCESS_TOKEN.");
        }

        // No configured game is not an error here: a game id passed with the request is
        // enough, and a request with neither says so itself.
        return errors;
    }

    /// <inheritdoc />
    /// <remarks>Subscriptions belong to a user, so this needs the access token.</remarks>
    public async Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        var subscribed = await GetSubscribedAsync(gameDomain, ct);
        var mods = new List<BackendMod>(subscribed.Count);

        foreach (var mod in subscribed)
        {
            var mapped = Map(mod);
            mapped.CategoryName = PickCategory(
                mod.Tags?.Select(t => t.Name).OfType<string>() ?? [],
                await GetTagGroupsAsync(mod.GameId, ct));
            mods.Add(mapped);
        }

        return mods;
    }

    /// <summary>
    /// Gets a game's visible tag groups, in the game's order.
    /// </summary>
    /// <param name="gameId">The mod.io game id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The groups; empty when the game has none or they cannot be read.</returns>
    /// <remarks>
    /// mod.io has no single category per mod, only tags in groups the game defines. The
    /// first group is the game's own primary split; Baldur's Gate 3's is "Type", holding
    /// Classes, Races, Equipment and the like.
    /// </remarks>
    public async Task<IReadOnlyList<ModIoTagOption>> GetTagGroupsAsync(long gameId, CancellationToken ct = default)
    {
        if (gameId <= 0)
            return [];

        if (_tagGroups.TryGetValue(gameId, out var known))
            return known;

        IReadOnlyList<ModIoTagOption> groups = [];
        try
        {
            var page = await _client.GetAsync<ModIoPage<ModIoTagOption>>(gameId, $"games/{gameId}/tags", ct: ct);
            groups = page.Data.Where(g => !g.Hidden && g.Tags.Count > 0).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not read the tag groups of mod.io game {GameId}", gameId);
        }

        return _tagGroups[gameId] = groups;
    }

    /// <summary>
    /// Picks a mod's category from its tags.
    /// </summary>
    /// <param name="modTags">The mod's tags.</param>
    /// <param name="groups">The game's tag groups, in order; the first holds the categories.</param>
    /// <returns>The category, or <c>null</c> when the tags give none.</returns>
    /// <remarks>
    /// <para>
    /// The category is the first of the first group's tags that the mod carries, in the
    /// game's order.
    /// </para>
    /// <para>
    /// A mod tagged only in a more specific group still has one when that group is named
    /// after a category: Baldur's Gate 3's "Character Customisation" group (Hairstyles,
    /// Tattoos…) gives Customisation, "Classes &amp; Subclasses" (Cleric, Wizard…) gives
    /// Classes. Groups are tried in the game's order, so the first fitting tag decides.
    /// </para>
    /// </remarks>
    public static string? PickCategory(IEnumerable<string> modTags, IReadOnlyList<ModIoTagOption> groups)
    {
        if (groups.Count == 0)
            return null;

        var carried = modTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categories = groups[0].Tags;

        var direct = categories.FirstOrDefault(carried.Contains);
        if (direct != null)
            return direct;

        foreach (var group in groups.Skip(1))
        {
            if (!group.Tags.Any(carried.Contains))
                continue;

            var named = categories.FirstOrDefault(c => Regex.IsMatch(
                group.Name, $@"\b{Regex.Escape(c)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (named != null)
                return named;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<List<BackendModFile>> GetModFilesAsync(
        string modId,
        string? gameDomain = null,
        FileFilter? filter = null,
        CancellationToken ct = default)
    {
        var mod = await GetModAsync(modId, gameDomain, ct);
        if (mod == null)
            return new List<BackendModFile>();

        var files = await _client.GetAllAsync<ModIoModfile>(
            mod.GameId,
            $"games/{mod.GameId}/mods/{mod.Id}/files",
            new Dictionary<string, string> { ["_sort"] = "-date_added" },
            ct: ct);

        var liveId = mod.Modfile?.Id;

        return files
            .Select(f => MapFile(f, mod, f.Id == liveId ? LiveFileCategory : OldFileCategory))
            .Where(f => Accepts(filter, f))
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Download links can carry a verification hash that expires, so this always asks for
    /// a fresh one.
    /// </remarks>
    public async Task<string?> ResolveDownloadUrlAsync(
        string modId,
        string fileId,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        var gameId = await ResolveGameForModAsync(modId, gameDomain, ct);
        if (gameId == null)
            return null;

        try
        {
            var file = await _client.GetAsync<ModIoModfile>(
                gameId.Value, $"games/{gameId}/mods/{modId}/files/{fileId}", ct: ct);
            return file.Download?.BinaryUrl;
        }
        catch (ModIoException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Downloads the live file of every subscribed mod, which is what the game's own mod
    /// manager would install. Archives land in
    /// <c>{output}/modio/{game}/{mod name}/</c>; see <see cref="ModLibraryLayout"/>.
    /// </remarks>
    public async Task DownloadModsAsync(
        string outputDirectory,
        string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= DownloadOptions.Default;
        FileUtils.EnsureDirectoryExists(outputDirectory);

        progress?.Report(DownloadProgress.Scanning("Fetching subscribed mods from mod.io..."));
        options.StatusCallback?.Invoke("Fetching subscribed mods from mod.io...");

        var mods = await GetSubscribedAsync(gameDomain, ct);
        options.StatusCallback?.Invoke($"Found {mods.Count} subscribed mods.");

        // The subscription listing already carries each mod's live file, so no request
        // per mod is needed to find what to download.
        var work = new List<(BackendMod Mod, BackendModFile File)>();
        foreach (var mod in mods)
        {
            if (mod.Modfile == null)
            {
                _logger?.LogWarning("mod.io mod {ModId} ({Name}) has no live file", mod.Id, mod.Name);
                continue;
            }

            var file = MapFile(mod.Modfile, mod, LiveFileCategory);
            if (Accepts(options.Filter, file))
                work.Add((Map(mod), file));
        }

        options.StatusCallback?.Invoke($"Ready to download {work.Count} files.");

        var completed = 0;
        var total = work.Count;

        // Up to MaxConcurrency files at once; counters are shared, so they are interlocked.
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = ct },
            async (entry, itemCt) =>
        {
            var (mod, file) = entry;
            itemCt.ThrowIfCancellationRequested();

            var gameName = await GetGameNameAsync(mod.GameDomain, itemCt);
            var modOutputDir = ModLibraryLayout.ModFolder(outputDirectory, Id, gameName, mod.Name);
            var outputPath = Path.Combine(modOutputDir, FileUtils.SanitizeFilename(file.FileName));

            // Partial downloads live in a separate .part file, so an existing file is complete.
            if (!options.Force && File.Exists(outputPath))
            {
                Interlocked.Increment(ref completed);
                progress?.Report(DownloadProgress.Downloading(
                    $"Skipping {file.FileName} (already exists)", Volatile.Read(ref completed), total, file.FileName));
                return;
            }

            if (options.DryRun)
            {
                _logger?.LogInformation("[DRY RUN] Would download: {File}", outputPath);
                Interlocked.Increment(ref completed);
                progress?.Report(DownloadProgress.Downloading(
                    $"[DRY RUN] {file.FileName}", Volatile.Read(ref completed), total, file.FileName));
                return;
            }

            try
            {
                var url = file.DirectDownloadUrl
                    ?? await ResolveDownloadUrlAsync(mod.ModId, file.FileId, mod.GameDomain, itemCt);

                if (string.IsNullOrEmpty(url))
                {
                    _logger?.LogWarning("No download URL for {Mod}/{File}", mod.Name, file.FileName);
                    Interlocked.Increment(ref completed);
                    progress?.Report(DownloadProgress.Failure(
                        file.FileName, "mod.io gave no download link", Volatile.Read(ref completed), total));
                    return;
                }

                FileUtils.EnsureDirectoryExists(modOutputDir);

                var resume = options.Resume && !options.Force;
                var completedSoFar = Volatile.Read(ref completed);
                var modId = mod.ModId;
                var fileId = file.FileId;
                var game = mod.GameDomain;

                progress?.Report(DownloadProgress.Downloading(
                    ResumableDownloader.GetPartialLength(outputPath) > 0 && resume
                        ? $"Resuming {file.FileName}"
                        : $"Downloading {file.FileName}",
                    completedSoFar, total, file.FileName));

                var result = await _downloader.DownloadAsync(
                    new ResumableDownloadRequest
                    {
                        Url = url,
                        DestinationPath = outputPath,
                        Identity = DownloadIdentity.ModIo(game ?? "0", modId, fileId),
                        // mod.io's filesize and filehash.md5 are exact.
                        ExpectedSize = file.SizeBytes,
                        ExpectedMd5 = file.Md5,
                        VerifyMd5 = options.VerifyDownloads,
                        Resume = resume,
                        RefreshUrlAsync = token => ResolveDownloadUrlAsync(modId, fileId, game, token)
                    },
                    progress == null ? null : new SyncProgress<(long downloaded, long total)>(p =>
                        progress.Report(new DownloadProgress
                        {
                            Status = $"Downloading {file.FileName}",
                            Completed = completedSoFar,
                            Total = total,
                            CurrentFile = file.FileName,
                            CurrentFileBytes = p.downloaded,
                            CurrentFileTotalBytes = p.total >= 0 ? p.total : file.SizeBytes,
                            Phase = DownloadPhase.Downloading
                        })),
                    itemCt);

                _logger?.LogInformation(
                    "Downloaded: {File}{Resumed}", outputPath, result.Resumed ? " (resumed)" : string.Empty);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Partial data is kept on disk; the next run resumes it.
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download {Mod}/{File}", mod.Name, file.FileName);
                Interlocked.Increment(ref completed);
                progress?.Report(DownloadProgress.Failure(file.FileName, ex.Message, Volatile.Read(ref completed), total));
                return;
            }

            Interlocked.Increment(ref completed);
            progress?.Report(DownloadProgress.Downloading(
                $"Downloaded {file.FileName}", Volatile.Read(ref completed), total, file.FileName));
        });

        progress?.Report(DownloadProgress.Done(total));
    }

    /// <inheritdoc />
    public async Task<BackendMod?> GetModInfoAsync(
        string modId,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        var mod = await GetModAsync(modId, gameDomain, ct);
        return mod == null ? null : Map(mod);
    }

    /// <inheritdoc />
    /// <remarks>
    /// mod.io has no relevance ranking, so <see cref="ModSortOrder.Relevance"/> sorts by
    /// subscriber count. "Endorsements" maps to mod.io's weighted rating.
    /// </remarks>
    public async Task<ModSearchResult> SearchModsAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var gameId = ResolveGameIds(query.GameDomain)[0];
        var pageSize = Math.Clamp(query.PageSize, 1, ModIoClient.MaxPageSize);
        var page = Math.Max(1, query.Page);

        var parameters = new Dictionary<string, string>
        {
            ["_limit"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["_offset"] = ((page - 1) * pageSize).ToString(CultureInfo.InvariantCulture),
            ["_sort"] = SortField(query.SortBy)
        };

        if (!string.IsNullOrWhiteSpace(query.Terms))
            parameters["_q"] = query.Terms.Trim();

        // Values below 8 lack the explicit bit; 8 and above have it.
        if (query.AdultContent == false)
            parameters["maturity_option-lt"] = ExplicitMaturityBit.ToString(CultureInfo.InvariantCulture);
        else if (query.AdultContent == true)
            parameters["maturity_option-gt"] = (ExplicitMaturityBit - 1).ToString(CultureInfo.InvariantCulture);

        var result = await _client.GetAsync<ModIoPage<ModIoMod>>(
            gameId, $"games/{gameId}/mods", parameters, ct);

        return new ModSearchResult
        {
            Mods = result.Data.Select(Map).ToList(),
            TotalCount = result.ResultTotal,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// mod.io dependencies always point at mods of the same game, on mod.io itself.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>>> GetRequirementsAsync(
        IReadOnlyList<string> modIds,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        var result = new Dictionary<string, IReadOnlyList<ModRequirement>>(StringComparer.Ordinal);

        foreach (var modId in modIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            if (!long.TryParse(modId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;

            var gameId = await ResolveGameForModAsync(modId, gameDomain, ct);
            if (gameId == null)
                continue;

            List<ModIoDependency> dependencies;
            try
            {
                dependencies = await _client.GetAllAsync<ModIoDependency>(
                    gameId.Value, $"games/{gameId}/mods/{modId}/dependencies", ct: ct);
            }
            catch (ModIoException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            result[modId] = dependencies
                .Select(d => new ModRequirement
                {
                    ModId = d.ModId.ToString(CultureInfo.InvariantCulture),
                    Name = string.IsNullOrWhiteSpace(d.Name) ? d.ModId.ToString(CultureInfo.InvariantCulture) : d.Name,
                    SameGame = true,
                    IsExternal = false
                })
                .ToList();
        }

        return result;
    }

    /// <inheritdoc />
    public TrackingTerms Terms => TrackingTerms.Subscribe;

    /// <inheritdoc />
    /// <remarks>
    /// A game with an in-game mod manager downloads every subscribed mod on its next
    /// launch; Baldur's Gate 3 does. Callers that also install the mod themselves have to
    /// account for that, or the game ends up with two copies.
    /// </remarks>
    public async Task<TrackResult> TrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default)
    {
        var (gameId, id) = await PrepareSubscriptionChangeAsync(modId, gameDomain, ct);

        var (status, mod) = await WriteSubscriptionAsync(HttpMethod.Post, gameId, id, ct);
        return new TrackResult(
            status == HttpStatusCode.Created ? TrackOutcome.Tracked : TrackOutcome.AlreadyTracked,
            mod == null ? null : Map(mod));
    }

    /// <inheritdoc />
    public async Task<TrackResult> UntrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default)
    {
        var (gameId, id) = await PrepareSubscriptionChangeAsync(modId, gameDomain, ct);

        try
        {
            await WriteSubscriptionAsync(HttpMethod.Delete, gameId, id, ct);
            return new TrackResult(TrackOutcome.Untracked);
        }
        catch (TrackingException ex) when (ex.InnerException is ModIoException { ErrorRef: NotSubscribedErrorRef })
        {
            return new TrackResult(TrackOutcome.NotTracked);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetTrackedIdsAsync(string? gameDomain = null, CancellationToken ct = default)
    {
        var subscribed = await GetSubscribedAsync(gameDomain, ct);
        return subscribed
            .Select(m => Remember(m).Id.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Finds a mod's id from the name in its page link (<c>mod.io/g/&lt;game&gt;/m/&lt;name&gt;</c>).
    /// </summary>
    /// <param name="nameId">The mod's name as its link spells it.</param>
    /// <param name="gameDomain">The mod.io game id, or null for the configured games.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mod id, or null when no configured game has the mod.</returns>
    public async Task<string?> FindModIdAsync(string nameId, string? gameDomain = null, CancellationToken ct = default)
    {
        foreach (var gameId in ResolveGameIds(gameDomain))
        {
            var page = await _client.GetAsync<ModIoPage<ModIoMod>>(
                gameId,
                $"games/{gameId}/mods",
                new Dictionary<string, string> { ["name_id"] = nameId.Trim(), ["_limit"] = "1" },
                ct);

            if (page.Data.FirstOrDefault() is { } mod)
                return Remember(mod).Id.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    // ── internals ──────────────────────────────────────────────────────────

    // mod.io's error references for subscription changes.
    private const int MissingScopeErrorRef = 11003;
    private const int InactiveAccountErrorRef = 11006;
    private const int TakenDownErrorRef = 15000;
    private const int HiddenModErrorRef = 15001;
    private const int NotSubscribedErrorRef = 15005;

    private async Task<(long GameId, string ModId)> PrepareSubscriptionChangeAsync(
        string modId, string? gameDomain, CancellationToken ct)
    {
        modId = modId.Trim();
        if (!long.TryParse(modId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException($"'{modId}' is not a mod.io mod id.", nameof(modId));

        if (!_client.HasAccessToken)
        {
            throw new TrackingException(
                TrackingFailure.NotAuthenticated,
                "Subscribing on mod.io needs an OAuth access token with write scope; an API key only reads. " +
                "Create a read+write token at https://mod.io/me/access and set 'modio_access_token' or MODIO_ACCESS_TOKEN.");
        }

        var gameId = await ResolveGameForModAsync(modId, gameDomain, ct)
            ?? throw new TrackingException(
                TrackingFailure.ModNotFound,
                $"mod.io mod {modId} is not in any configured game. Pass its game id.");
        return (gameId, modId);
    }

    private async Task<(HttpStatusCode Status, ModIoMod? Mod)> WriteSubscriptionAsync(
        HttpMethod method, long gameId, string modId, CancellationToken ct)
    {
        try
        {
            var (status, mod) = await _client.WriteAsync<ModIoMod>(
                method, gameId, $"games/{gameId}/mods/{modId}/subscribe", ct: ct);
            if (mod != null)
                Remember(mod);
            else
                _gameOfMod[modId] = gameId;

            _logger?.LogInformation("mod.io {Method} subscription to {ModId}: {Status}", method, modId, (int)status);
            return (status, mod);
        }
        catch (ModIoException ex)
        {
            var reason = ex switch
            {
                { ErrorRef: MissingScopeErrorRef } => TrackingFailure.MissingWriteScope,
                { ErrorRef: InactiveAccountErrorRef or TakenDownErrorRef or HiddenModErrorRef } => TrackingFailure.Unavailable,
                { StatusCode: HttpStatusCode.Unauthorized } => TrackingFailure.NotAuthenticated,
                { StatusCode: HttpStatusCode.NotFound } => TrackingFailure.ModNotFound,
                { StatusCode: HttpStatusCode.TooManyRequests } => TrackingFailure.RateLimited,
                _ => TrackingFailure.Other
            };
            var hint = reason == TrackingFailure.MissingWriteScope
                ? " The access token is read-only; create a read+write token at https://mod.io/me/access."
                : "";
            throw new TrackingException(reason, ex.Message + hint, ex);
        }
    }

    private async Task<List<ModIoMod>> GetSubscribedAsync(string? gameDomain, CancellationToken ct)
    {
        if (!_client.HasAccessToken)
        {
            throw new ModIoException(
                "Reading mod.io subscriptions needs an OAuth access token, not just an API key. " +
                "Create one at https://mod.io/me/access and set 'modio_access_token' or MODIO_ACCESS_TOKEN.",
                HttpStatusCode.Unauthorized);
        }

        var mods = new List<ModIoMod>();

        foreach (var gameId in ResolveGameIds(gameDomain))
        {
            var page = await _client.GetAllAsync<ModIoMod>(
                gameId,
                "me/subscribed",
                new Dictionary<string, string>
                {
                    ["game_id"] = gameId.ToString(CultureInfo.InvariantCulture),
                    ["_sort"] = "name"
                },
                ct: ct);

            mods.AddRange(page);
        }

        _logger?.LogInformation("Fetched {Count} subscribed mods from mod.io", mods.Count);
        return mods;
    }

    /// <summary>
    /// Gets a game's name, which names its folder in the mods directory.
    /// </summary>
    /// <param name="gameDomain">The mod.io game id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The name, or the id itself when the game cannot be looked up, so a download is
    /// never held up by it.
    /// </returns>
    public async Task<string?> GetGameNameAsync(string? gameDomain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameDomain) ||
            !long.TryParse(gameDomain.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var gameId) ||
            gameId <= 0)
        {
            return gameDomain;
        }

        if (_gameNames.TryGetValue(gameId, out var known))
            return known;

        try
        {
            var game = await _client.GetAsync<ModIoGame>(gameId, $"games/{gameId}", ct: ct);
            if (!string.IsNullOrWhiteSpace(game.Name))
                return _gameNames[gameId] = game.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not look up mod.io game {GameId}", gameId);
        }

        return gameDomain;
    }

    /// <summary>
    /// Finds mod.io games by name, so nobody has to know a game's number.
    /// </summary>
    /// <remarks>
    /// Every game's host serves the whole games list, so the first configured game's host
    /// is used, or Baldur's Gate 3's when none is configured.
    /// </remarks>
    /// <param name="name">Part of a game's name, or a game id.</param>
    /// <param name="maxResults">Most games to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching games, best match first.</returns>
    public async Task<IReadOnlyList<GameMatch>> SearchGamesAsync(
        string name,
        int maxResults = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return [];

        name = name.Trim();

        if (long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId) && gameId > 0)
        {
            var known = await GetGameNameAsync(name, ct);
            return known != null && known != name ? [new GameMatch(name, known)] : [];
        }

        var host = _settings.ModIoGameIds.Count > 0 ? _settings.ModIoGameIds[0] : DirectoryHostGame;
        var page = await _client.GetAsync<ModIoPage<ModIoGame>>(
            host,
            "games",
            new Dictionary<string, string>
            {
                ["_q"] = name,
                ["_limit"] = Math.Clamp(maxResults, 1, ModIoClient.MaxPageSize).ToString(CultureInfo.InvariantCulture)
            },
            ct);

        var games = page.Data.Where(g => g.Id > 0 && !string.IsNullOrWhiteSpace(g.Name)).ToList();
        foreach (var game in games)
            _gameNames[game.Id] = game.Name;

        return games
            .OrderByDescending(g => FuzzyMatcher.Score(name, g.Name))
            .Select(g => new GameMatch(g.Id.ToString(CultureInfo.InvariantCulture), g.Name))
            .ToList();
    }

    private async Task<ModIoMod?> GetModAsync(string modId, string? gameDomain, CancellationToken ct)
    {
        foreach (var gameId in CandidateGames(modId, gameDomain))
        {
            try
            {
                return await _client.GetAsync<ModIoMod>(gameId, $"games/{gameId}/mods/{modId}", ct: ct)
                    is { } mod ? Remember(mod) : null;
            }
            catch (ModIoException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Not this game's; try the next configured one.
            }
        }

        return null;
    }

    private async Task<long?> ResolveGameForModAsync(string modId, string? gameDomain, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(gameDomain))
            return ResolveGameIds(gameDomain)[0];

        if (_gameOfMod.TryGetValue(modId, out var known))
            return known;

        if (_settings.ModIoGameIds.Count == 1)
            return _settings.ModIoGameIds[0];

        return (await GetModAsync(modId, null, ct))?.GameId;
    }

    // The given game first, else what was learned about the mod, else every configured game.
    private IEnumerable<long> CandidateGames(string modId, string? gameDomain)
    {
        if (!string.IsNullOrWhiteSpace(gameDomain))
            return ResolveGameIds(gameDomain);

        return _gameOfMod.TryGetValue(modId, out var known)
            ? [known]
            : ResolveGameIds(null);
    }

    /// <summary>
    /// The games a request is for: the one given, or every configured game.
    /// </summary>
    /// <param name="gameDomain">A mod.io game id, or null.</param>
    /// <returns>At least one game id.</returns>
    internal IReadOnlyList<long> ResolveGameIds(string? gameDomain)
    {
        if (!string.IsNullOrWhiteSpace(gameDomain))
        {
            if (long.TryParse(gameDomain.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                return [id];

            throw new ArgumentException(
                $"'{gameDomain}' is not a mod.io game id. mod.io games are numbers, such as 6715 for Baldur's Gate 3.",
                nameof(gameDomain));
        }

        if (_settings.ModIoGameIds.Count == 0)
        {
            throw new InvalidOperationException(
                "No mod.io game given or configured. Pass the game's mod.io id, or add it to 'modio_game_ids'.");
        }

        return _settings.ModIoGameIds.Select(i => (long)i).ToList();
    }

    private ModIoMod Remember(ModIoMod mod)
    {
        if (mod.GameId > 0)
            _gameOfMod[mod.Id.ToString(CultureInfo.InvariantCulture)] = mod.GameId;
        return mod;
    }

    private BackendMod Map(ModIoMod mod)
    {
        Remember(mod);

        var images = new List<string>();
        if (!string.IsNullOrEmpty(mod.Logo?.Original))
            images.Add(mod.Logo.Original);
        if (mod.Media?.Images != null)
        {
            images.AddRange(mod.Media.Images
                .Select(i => i.Original)
                .Where(u => !string.IsNullOrEmpty(u))
                .Select(u => u!));
        }

        return new BackendMod
        {
            ModId = mod.Id.ToString(CultureInfo.InvariantCulture),
            Name = mod.Name,
            GameDomain = mod.GameId > 0 ? mod.GameId.ToString(CultureInfo.InvariantCulture) : null,
            BackendId = BackendIds.ModIo,
            Url = mod.ProfileUrl,
            Summary = mod.Summary,
            Author = mod.SubmittedBy?.Username,
            UpdatedAt = FromUnix(mod.DateUpdated),
            ThumbnailUrl = mod.Logo?.Thumb320x180 ?? mod.Logo?.Original,
            DownloadCount = mod.Stats?.DownloadsTotal,
            Version = mod.Modfile?.Version,
            IsAdult = (mod.MaturityOption & ExplicitMaturityBit) != 0,
            ImageUrls = images.Count > 0 ? images : null
        };
    }

    private static BackendModFile MapFile(ModIoModfile file, ModIoMod mod, string category)
    {
        var fileName = !string.IsNullOrWhiteSpace(file.Filename)
            ? file.Filename
            : $"{mod.NameId ?? mod.Id.ToString(CultureInfo.InvariantCulture)}-{file.Id}.zip";

        return new BackendModFile
        {
            FileId = file.Id.ToString(CultureInfo.InvariantCulture),
            FileName = fileName,
            DisplayName = string.IsNullOrWhiteSpace(file.Version) ? fileName : $"{fileName} ({file.Version})",
            SizeBytes = file.Filesize > 0 ? file.Filesize : null,
            Md5 = string.IsNullOrWhiteSpace(file.Filehash?.Md5) ? null : file.Filehash.Md5,
            Version = string.IsNullOrWhiteSpace(file.Version) ? null : file.Version,
            Category = category,
            DirectDownloadUrl = UsableUrl(file.Download),
            UploadedAt = FromUnix(file.DateAdded),
            Description = string.IsNullOrWhiteSpace(file.Changelog) ? null : file.Changelog,
            ModId = mod.Id.ToString(CultureInfo.InvariantCulture)
        };
    }

    // A link that has already expired, or will before a download can start, is worse than
    // none: leaving it out makes the caller resolve a fresh one.
    private static string? UsableUrl(ModIoDownload? download)
    {
        if (string.IsNullOrEmpty(download?.BinaryUrl))
            return null;

        if (download.DateExpires is > 0 &&
            DateTimeOffset.FromUnixTimeSeconds(download.DateExpires.Value) < DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return null;
        }

        return download.BinaryUrl;
    }

    private static bool Accepts(FileFilter? filter, BackendModFile file)
    {
        if (filter == null)
            return true;

        var categoryOk =
            filter.Categories is not { Count: > 0 } ||
            filter.Categories.Contains(file.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase) ||
            (filter.IncludeArchived && file.Category == OldFileCategory);

        var dateOk = filter.UploadedAfter == null ||
                     (file.UploadedAt != null && file.UploadedAt > filter.UploadedAfter);

        return categoryOk && dateOk;
    }

    private static string SortField(ModSortOrder order) => order switch
    {
        ModSortOrder.Endorsements => "-ratings_weighted_aggregate",
        ModSortOrder.Downloads => "-downloads_total",
        ModSortOrder.Updated => "-date_updated",
        ModSortOrder.Added => "-date_live",
        _ => "-subscribers_total"
    };

    private static DateTime? FromUnix(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime : null;
}
