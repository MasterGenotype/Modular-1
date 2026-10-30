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
/// Runs the interactive menu mode (default command when no arguments provided).
/// </summary>
public sealed class InteractiveCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        while (true)
        {
            try
            {
                using var services = await RuntimeServices.InitializeMinimalAsync();
                var registry = services.CreateBackendRegistry();
                // NexusMods is offered before login too: choosing it logs in through the browser.
                var configured = registry.GetAll()
                    .Where(b => b.ValidateConfiguration().Count == 0 ||
                                (b.Id == BackendIds.NexusMods && services.Settings.NexusSsoEnabled))
                    .ToList();

                // Build menu dynamically from configured backends + Rename option
                var menuItems = configured.Select(b => b.DisplayName).Append("Rename").ToArray();
                var choice = LiveProgressDisplay.ShowNumberedMenu("Modular", menuItems);

                if (choice == 0)
                    return 0;

                if (choice <= configured.Count)
                {
                    // Selected a backend
                    var backend = configured[choice - 1];
                    if (backend.Id == BackendIds.NexusMods && !await services.EnsureNexusLoginAsync())
                        continue;
                    string? gameDomain = null;

                    // Prompt for game domain if backend requires it
                    if (backend.Capabilities.HasFlag(BackendCapabilities.GameDomains))
                    {
                        gameDomain = LiveProgressDisplay.AskString("Enter game domain (e.g., stardewvalley):");
                        if (string.IsNullOrWhiteSpace(gameDomain))
                        {
                            LiveProgressDisplay.ShowWarning("Game domain is required for this backend.");
                            continue;
                        }
                    }

                    await RunBackendDownloadAsync(backend, services, gameDomain);

                    // Auto-rename for NexusMods if enabled
                    if (backend.Id == BackendIds.NexusMods && services.Settings.AutoRename && !string.IsNullOrEmpty(gameDomain))
                    {
                        var renameService = new RenameService(services.Settings, services.RateLimiter, services.MetadataCache, null);
                        LiveProgressDisplay.ShowInfo($"Auto-organizing and renaming mods in {gameDomain}...");
                        var gameDomainPath = renameService.GetGameDomainPath(gameDomain);
                        var renamed = await renameService.ReorganizeAndRenameModsAsync(gameDomainPath, services.Settings.OrganizeByCategory, CancellationToken.None);
                        LiveProgressDisplay.ShowSuccess($"Successfully processed {renamed} mods in {gameDomain}");
                    }
                    else if (backend.Id is BackendIds.GameBanana or BackendIds.ModIo)
                    {
                        // Named after the mod already; new downloads only need sorting.
                        await services.SortBackendDownloadsAsync(backend, CancellationToken.None);
                    }

                    await services.SaveStateAsync();
                }
                else if (choice == configured.Count + 1)
                {
                    // Rename option
                    var renameDomain = LiveProgressDisplay.AskString("Enter game domain to rename:");
                    await RunRenameAsync(services, renameDomain);
                }
            }
            catch (Exception ex)
            {
                LiveProgressDisplay.ShowError(ex.Message);
            }
        }
    }

    private static async Task RunBackendDownloadAsync(
        Modular.Core.Backends.IModBackend backend,
        RuntimeServices services,
        string? gameDomain)
    {
        var options = new DownloadOptions
        {
            DryRun = false,
            Force = false,
            OrganizeByCategory = services.Settings.OrganizeByCategory,
            AutoRename = services.Settings.AutoRename,
            MaxConcurrency = services.Settings.MaxConcurrentDownloads,
            Filter = FileFilter.MainAndOptional
        };

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Phase == DownloadPhase.Failed)
                LiveProgressDisplay.ShowError($"[{p.Completed}/{p.Total}] {p.Status}");
            else if (p.Phase == DownloadPhase.Downloading && p.Total > 0)
                Console.WriteLine($"[{p.Completed}/{p.Total}] {p.Status}");
            else if (p.Phase == DownloadPhase.Scanning)
                Console.WriteLine($"[SCAN] {p.Status}");
        });

        await backend.DownloadModsAsync(
            services.Settings.ModsDirectory,
            gameDomain,
            options,
            progress,
            CancellationToken.None);
    }

    private static async Task RunRenameAsync(RuntimeServices services, string? domain)
    {
        var renameService = new RenameService(services.Settings, services.RateLimiter, services.MetadataCache,
            services.LoggerFactory?.CreateLogger<RenameService>());

        IEnumerable<string> domains;
        if (!string.IsNullOrEmpty(domain))
        {
            domains = [domain];
        }
        else
        {
            domains = renameService.GetGameDomainNames();
        }

        foreach (var d in domains)
        {
            var gameDomainPath = renameService.GetGameDomainPath(d);
            if (!Directory.Exists(gameDomainPath))
            {
                LiveProgressDisplay.ShowWarning($"Directory not found: {gameDomainPath}");
                continue;
            }

            LiveProgressDisplay.ShowInfo($"Processing {d}...");
            var renamed = await renameService.ReorganizeAndRenameModsAsync(gameDomainPath, services.Settings.OrganizeByCategory, CancellationToken.None);
            LiveProgressDisplay.ShowSuccess($"Renamed {renamed} mods in {d}");
        }

        await services.SaveStateAsync();
    }
}
