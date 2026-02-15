using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Backends;
using Modular.Core.Services;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Downloads mods from a backend.
/// </summary>
public sealed class DownloadCommand : AsyncCommand<DownloadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[domain]")]
        [Description("Game domain (e.g., skyrimspecialedition, stardewvalley)")]
        public string? Domain { get; init; }

        [CommandOption("--backend")]
        [Description("Backend to use (e.g., nexusmods, gamebanana)")]
        public string? Backend { get; init; }

        [CommandOption("--all")]
        [Description("Download from all configured backends")]
        public bool All { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be downloaded without downloading")]
        public bool DryRun { get; init; }

        [CommandOption("--force")]
        [Description("Re-download existing files")]
        public bool Force { get; init; }

        [CommandOption("--categories")]
        [Description("Filter by file categories (e.g., main, optional)")]
        public string[]? Categories { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;

        try
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                LiveProgressDisplay.ShowWarning("Cancellation requested, cleaning up...");
            };
            Console.CancelKeyPress += cancelHandler;

            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();

            // Determine which backends to use
            IEnumerable<Modular.Core.Backends.IModBackend> backends;
            if (settings.All)
            {
                backends = registry.GetConfigured();
                if (!backends.Any())
                {
                    LiveProgressDisplay.ShowError("No backends are properly configured.");
                    var errors = registry.GetAllConfigurationErrors();
                    foreach (var (id, errs) in errors)
                        LiveProgressDisplay.ShowWarning($"  {id}: {string.Join(", ", errs)}");
                    return 1;
                }
            }
            else if (!string.IsNullOrEmpty(settings.Backend))
            {
                var backend = registry.Get(settings.Backend);
                if (backend == null)
                {
                    LiveProgressDisplay.ShowError($"Unknown backend: {settings.Backend}");
                    LiveProgressDisplay.ShowInfo($"Available backends: {string.Join(", ", registry.GetIds())}");
                    return 1;
                }
                backends = [backend];
            }
            else
            {
                // Interactive selection
                var configured = registry.GetConfigured();
                if (!configured.Any())
                {
                    LiveProgressDisplay.ShowError("No backends are properly configured.");
                    return 1;
                }

                var menuItems = configured.Select(b => b.DisplayName).ToArray();
                var choice = LiveProgressDisplay.ShowNumberedMenu("Select backend", menuItems);
                if (choice == 0) return 0;
                backends = [configured[choice - 1]];
            }

            // Build download options
            var options = new DownloadOptions
            {
                DryRun = settings.DryRun,
                Force = settings.Force,
                Filter = settings.Categories?.Length > 0
                    ? new FileFilter { Categories = settings.Categories.ToList() }
                    : FileFilter.MainAndOptional,
                VerifyDownloads = services.Settings.VerifyDownloads,
                AutoRename = services.Settings.AutoRename,
                OrganizeByCategory = services.Settings.OrganizeByCategory,
                StatusCallback = status => Console.WriteLine($"[SCAN] {status}")
            };

            foreach (var backend in backends)
            {
                // Validate backend configuration
                var errors = backend.ValidateConfiguration();
                if (errors.Count > 0)
                {
                    LiveProgressDisplay.ShowWarning($"Skipping {backend.DisplayName}: {string.Join(", ", errors)}");
                    continue;
                }

                // Determine game domain
                var gameDomain = settings.Domain;
                if (backend.Capabilities.HasFlag(BackendCapabilities.GameDomains) && string.IsNullOrEmpty(gameDomain))
                {
                    gameDomain = LiveProgressDisplay.AskString($"Enter game domain for {backend.DisplayName} (e.g., stardewvalley):");
                    if (string.IsNullOrWhiteSpace(gameDomain))
                    {
                        LiveProgressDisplay.ShowWarning($"Skipping {backend.DisplayName}: game domain is required.");
                        continue;
                    }
                }

                LiveProgressDisplay.ShowInfo($"Downloading from {backend.DisplayName}...");
                await RunBackendDownloadAsync(backend, services, gameDomain, options, cts.Token);

                // Auto-rename for NexusMods if enabled
                if (backend.Id == "nexusmods" && services.Settings.AutoRename && !settings.DryRun && !string.IsNullOrEmpty(gameDomain))
                {
                    var renameService = new RenameService(services.Settings, services.RateLimiter, services.MetadataCache,
                        services.LoggerFactory?.CreateLogger<RenameService>());
                    LiveProgressDisplay.ShowInfo($"Auto-organizing and renaming mods in {gameDomain}...");
                    var gameDomainPath = Path.Combine(services.Settings.ModsDirectory, gameDomain);
                    var renamed = await renameService.ReorganizeAndRenameModsAsync(gameDomainPath, services.Settings.OrganizeByCategory, cts.Token);
                    LiveProgressDisplay.ShowSuccess($"Successfully processed {renamed} mods in {gameDomain}");

                    var catRenamed = await renameService.RenameCategoryFoldersAsync(gameDomainPath, cts.Token);
                    LiveProgressDisplay.ShowSuccess($"Renamed {catRenamed} category folders");
                }
            }

            await services.SaveStateAsync();
            LiveProgressDisplay.ShowSuccess("Download complete.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Operation cancelled by user.");
            return 1;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RunBackendDownloadAsync(
        Modular.Core.Backends.IModBackend backend,
        RuntimeServices services,
        string? gameDomain,
        DownloadOptions options,
        CancellationToken ct)
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Phase == DownloadPhase.Downloading && p.Total > 0)
                Console.WriteLine($"[{p.Completed}/{p.Total}] {p.Status}");
            else if (p.Phase == DownloadPhase.Scanning)
                Console.WriteLine($"[SCAN] {p.Status}");
        });

        await backend.DownloadModsAsync(
            services.Settings.ModsDirectory,
            gameDomain,
            options,
            progress,
            ct);
    }
}
