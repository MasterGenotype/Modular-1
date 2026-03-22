using System.ComponentModel;
using System.Text.Json;
using Modular.Cli.UI;
using Modular.Core.Installers.Steam;
using Modular.Sdk.Installers;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// CLI command for installing Steam game mods with dependency resolution.
/// Reads a mod manifest (JSON array) and installs all mods in dependency order
/// into the specified Steam game directory.
/// </summary>
public sealed class SteamInstallCommand : AsyncCommand<SteamInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<game-directory>")]
        [Description("Path to the Steam game installation directory")]
        public string GameDirectory { get; init; } = string.Empty;

        [CommandOption("--manifest|-m")]
        [Description("Path to a JSON manifest file containing mod definitions")]
        public string? ManifestPath { get; init; }

        [CommandOption("--archive|-a")]
        [Description("Path to a single mod archive to install (without dependency resolution)")]
        public string? ArchivePath { get; init; }

        [CommandOption("--condition|-c")]
        [Description("Active conditions for conditional dependencies (e.g., 'linux', 'with-extras')")]
        public string[]? Conditions { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show install order and detected conflicts without installing")]
        public bool DryRun { get; init; }

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

            if (!string.IsNullOrEmpty(settings.ArchivePath))
                return await InstallSingleArchiveAsync(settings, cts.Token);

            if (string.IsNullOrEmpty(settings.ManifestPath))
            {
                LiveProgressDisplay.ShowError("Either --manifest or --archive must be specified.");
                return 1;
            }

            return await InstallFromManifestAsync(settings, cts.Token);
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

    private static async Task<int> InstallSingleArchiveAsync(Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.ArchivePath))
        {
            LiveProgressDisplay.ShowError($"Archive not found: {settings.ArchivePath}");
            return 1;
        }

        var installer = new SteamModInstaller();
        var installContext = new InstallContext
        {
            GameDirectory = settings.GameDirectory,
            CreateBackups = true,
            ConflictPolicy = ConflictPolicy.FailOnConflict
        };

        var plan = await installer.AnalyzeAsync(settings.ArchivePath, installContext, ct);
        LiveProgressDisplay.ShowInfo($"Found {plan.Operations.Count} files to install ({plan.TotalBytes:N0} bytes)");

        if (settings.DryRun)
        {
            LiveProgressDisplay.ShowInfo("Dry run — no files will be modified.");
            foreach (var op in plan.Operations)
                Console.WriteLine($"  {op.Type}: {op.SourcePath} -> {op.DestinationPath}");
            return 0;
        }

        var progress = new Progress<InstallProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.CurrentFile))
                Console.WriteLine($"  [{p.FilesProcessed}/{p.TotalFiles}] {p.CurrentFile}");
        });

        var result = await installer.InstallAsync(plan, progress, ct);

        if (result.Success)
        {
            LiveProgressDisplay.ShowSuccess($"Installed {result.InstalledFiles.Count} files successfully.");
            return 0;
        }

        LiveProgressDisplay.ShowError($"Installation failed: {result.Error}");
        return 1;
    }

    private static async Task<int> InstallFromManifestAsync(Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.ManifestPath))
        {
            LiveProgressDisplay.ShowError($"Manifest not found: {settings.ManifestPath}");
            return 1;
        }

        // Parse manifest JSON
        var manifestJson = await File.ReadAllTextAsync(settings.ManifestPath, ct);
        List<SteamModMetadata>? mods;

        try
        {
            mods = JsonSerializer.Deserialize<List<SteamModMetadata>>(manifestJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            LiveProgressDisplay.ShowError($"Invalid manifest JSON: {ex.Message}");
            return 1;
        }

        if (mods == null || mods.Count == 0)
        {
            LiveProgressDisplay.ShowError("Manifest contains no mods.");
            return 1;
        }

        LiveProgressDisplay.ShowInfo($"Loaded {mods.Count} mods from manifest");

        var activeConditions = new HashSet<string>(
            settings.Conditions ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var installer = new SteamModInstaller();

        // Dry run: just show resolution
        if (settings.DryRun)
        {
            var solver = new SteamConstraintSolver();
            var resolution = solver.Resolve(mods, activeConditions);

            if (!resolution.Success)
            {
                LiveProgressDisplay.ShowError("Dependency resolution failed:");
                foreach (var error in resolution.Errors)
                    Console.WriteLine($"  {error}");
                return 1;
            }

            LiveProgressDisplay.ShowInfo("Install order:");
            for (int i = 0; i < resolution.InstallOrder.Count; i++)
            {
                var mod = resolution.InstallOrder[i];
                Console.WriteLine($"  {i + 1}. {mod.Name} v{mod.Version} ({mod.ArchivePath})");
            }

            foreach (var warning in resolution.Warnings)
                LiveProgressDisplay.ShowWarning(warning);

            return 0;
        }

        // Full installation
        var progress = new Progress<InstallProgress>(p =>
        {
            if (settings.Verbose || !string.IsNullOrEmpty(p.CurrentOperation))
                Console.WriteLine($"  [{p.CurrentOperation}] {p.CurrentFile ?? ""}");
        });

        var result = await installer.InstallModsAsync(
            mods,
            settings.GameDirectory,
            activeConditions,
            progress,
            ct);

        // Report results
        foreach (var warning in result.Warnings)
            LiveProgressDisplay.ShowWarning(warning);

        if (!result.Success)
        {
            LiveProgressDisplay.ShowError("Installation failed:");
            foreach (var error in result.Errors)
                Console.WriteLine($"  {error}");

            if (result.FileConflicts.Count > 0)
            {
                LiveProgressDisplay.ShowError("File conflicts:");
                foreach (var conflict in result.FileConflicts)
                    Console.WriteLine($"  {conflict.FilePath}: {string.Join(", ", conflict.ConflictingMods)}");
            }

            return 1;
        }

        LiveProgressDisplay.ShowSuccess(
            $"Installation complete: {result.InstalledFiles.Count} files installed " +
            $"({result.BackedUpFiles.Count} backed up)");
        LiveProgressDisplay.ShowInfo($"Install order: {string.Join(" -> ", result.InstallOrder)}");

        if (result.BackupDirectory != null)
            LiveProgressDisplay.ShowInfo($"Backups saved to: {result.BackupDirectory}");

        return 0;
    }
}
