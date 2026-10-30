using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Downloads;
using Modular.Core.Services;
using Modular.Sdk.Backends;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Downloads the file behind an <c>nxm://</c> link, the link the "Mod Manager Download"
/// button on nexusmods.com hands to a mod manager. This is how accounts without
/// Premium download: one click on the site per file, then the manager does the rest.
/// </summary>
public sealed class NxmCommand : AsyncCommand<NxmCommand.Settings>
{
    private const string DesktopFileName = "modular-nxm.desktop";
    private const string NxmMimeType = "x-scheme-handler/nxm";

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[url]")]
        [Description("The nxm:// link, e.g. nxm://cyberpunk2077/mods/107/files/12345?key=...&expires=...")]
        public string? Url { get; init; }

        [CommandOption("--register")]
        [Description("Make Modular the system handler for nxm:// links (Linux)")]
        public bool Register { get; init; }

        [CommandOption("--force")]
        [Description("Download again even if the file is already downloaded")]
        public bool Force { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }

        public override Spectre.Console.ValidationResult Validate() =>
            Register || !string.IsNullOrWhiteSpace(Url)
                ? Spectre.Console.ValidationResult.Success()
                : Spectre.Console.ValidationResult.Error("Give an nxm:// link, or --register.");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (settings.Register)
            return await RegisterAsync();

        try
        {
            NxmLink.Parse(settings.Url!);
        }
        catch (FormatException ex)
        {
            Fail(ex.Message);
            return 1;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        // Each click runs this. The link joins the shared queue; whichever process is the
        // worker downloads it, so clicks run side by side instead of waiting their turn.
        var queue = NxmLinkQueue.Default;
        queue.Add(settings.Url!);

        var failures = 0;
        try
        {
            while (true)
            {
                using (var worker = queue.TryBecomeWorker())
                {
                    if (worker == null)
                    {
                        Console.WriteLine("Another Modular process is downloading nxm links; this one joins its queue.");
                        return 0;
                    }

                    // Loaded while holding the lock, so the previous worker's records are seen.
                    var (taken, failed) = await DrainAsync(queue, settings, cts.Token);
                    failures += failed;
                    if (taken == 0)
                        break;
                }

                // Released first, then checked: a link added meanwhile is picked up here,
                // or by the process that added it.
                if (!queue.HasPending)
                    break;
            }

            return failures == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LiveProgressDisplay.ShowWarning("Cancelled. Click the links again to resume them.");
            return 130;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// Downloads queued links until none are left, up to max_concurrent_downloads at once.
    /// </summary>
    /// <returns>How many links were taken from the queue, and how many failed.</returns>
    private static async Task<(int Taken, int Failed)> DrainAsync(
        NxmLinkQueue queue, Settings settings, CancellationToken ct)
    {
        // Runs SSO first when no API key is configured.
        using var services = await RuntimeServices.InitializeAsync(settings.Verbose);
        var backend = new NexusModsBackend(
            services.Settings,
            services.RateLimiter,
            services.Database,
            services.MetadataCache,
            services.LoggerFactory?.CreateLogger<NexusModsBackend>(),
            services.Downloader);
        var options = new DownloadOptions
        {
            Force = settings.Force,
            VerifyDownloads = services.Settings.VerifyDownloads
        };

        var limit = Math.Max(1, services.Settings.MaxConcurrentDownloads);
        using var slots = new SemaphoreSlim(limit, limit);
        var running = new List<Task>();
        var downloadedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taken = 0;
        var failed = 0;

        async Task DownloadOneAsync(string url)
        {
            try
            {
                var link = NxmLink.Parse(url);
                var label = $"{link.GameDomain} mod {link.ModId}, file {link.FileId}";
                LiveProgressDisplay.ShowInfo($"Downloading {label}...");
                var lastPercent = -10;
                var (fileName, alreadyDownloaded) = await backend.DownloadNxmAsync(
                    link,
                    services.Settings.ModsDirectory,
                    options,
                    new SyncProgress<(long downloaded, long total)>(p =>
                    {
                        if (p.total <= 0) return;
                        var percent = (int)(p.downloaded * 100 / p.total);
                        if (percent / 10 == lastPercent / 10) return;
                        lastPercent = percent;
                        Console.WriteLine($"  {label}: {percent}%");
                    }),
                    ct);

                if (alreadyDownloaded)
                {
                    LiveProgressDisplay.ShowInfo($"{fileName} is already downloaded (use --force to download it again).");
                    Notify("Already downloaded", fileName);
                    return;
                }

                lock (downloadedGames)
                    downloadedGames.Add(link.GameDomain);
                LiveProgressDisplay.ShowSuccess($"Downloaded {fileName}");
                Notify("Downloaded", fileName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Fail(ex.Message);
            }
            finally
            {
                slots.Release();
            }
        }

        try
        {
            while (true)
            {
                foreach (var url in queue.TakeAll())
                {
                    taken++;
                    await slots.WaitAsync(ct);
                    running.Add(DownloadOneAsync(url));
                }

                running.RemoveAll(t => t.IsCompleted);
                if (running.Count == 0 && !queue.HasPending)
                    break;

                // Wake when a download finishes, or soon after, to take newly clicked links.
                await Task.WhenAny(running.Append(Task.Delay(TimeSpan.FromSeconds(1), ct)));
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(running);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Same follow-up as 'modular download', once per game for the whole batch.
        if (services.Settings.AutoRename)
        {
            var renameService = new RenameService(services.Settings, services.RateLimiter,
                services.MetadataCache, services.LoggerFactory?.CreateLogger<RenameService>());
            foreach (var game in downloadedGames)
            {
                var gameDomainPath = renameService.GetGameDomainPath(game);
                await renameService.ReorganizeAndRenameModsAsync(
                    gameDomainPath, services.Settings.OrganizeByCategory, ct);
                if (services.Settings.OrganizeByCategory)
                    await renameService.RenameCategoryFoldersAsync(gameDomainPath, ct);
            }
        }

        await services.SaveStateAsync();
        return (taken, failed);
    }

    private static async Task<int> RegisterAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            LiveProgressDisplay.ShowError("--register is only supported on Linux.");
            return 1;
        }

        var exec = HandlerCommand();
        if (exec == null)
        {
            LiveProgressDisplay.ShowError("Could not tell where the modular executable is.");
            return 1;
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var applications = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(applications);
        var desktopPath = Path.Combine(applications, DesktopFileName);

        await File.WriteAllTextAsync(desktopPath, $"""
            [Desktop Entry]
            Type=Application
            Name=Modular (nxm links)
            Comment=Downloads NexusMods "Mod Manager Download" links with Modular
            Exec={exec} nxm %u
            MimeType={NxmMimeType};
            NoDisplay=true
            Terminal=false

            """);

        var previous = RunTool("xdg-mime", "query", "default", NxmMimeType)?.Trim();
        if (RunTool("xdg-mime", "default", DesktopFileName, NxmMimeType) == null)
        {
            LiveProgressDisplay.ShowError($"Wrote {desktopPath}, but xdg-mime could not make it the default.");
            return 1;
        }
        RunTool("update-desktop-database", applications);

        LiveProgressDisplay.ShowSuccess($"Modular now handles nxm:// links ({desktopPath}).");
        if (!string.IsNullOrEmpty(previous) && previous != DesktopFileName)
            LiveProgressDisplay.ShowInfo(
                $"The previous handler was {previous}. To restore it: xdg-mime default {previous} {NxmMimeType}");
        return 0;
    }

    // The command line that runs this executable, quoted for a desktop entry's Exec key.
    private static string? HandlerCommand()
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrEmpty(process))
            return null;

        // Under 'dotnet modular.dll' the process is dotnet itself.
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "modular.dll");
            return File.Exists(dll) ? $"{Quote(process)} {Quote(dll)}" : null;
        }

        return Quote(process);

        // Quoted only when it has to be: xdg-mime's handler check reads the first word of
        // Exec literally, quotes included, and skips an entry whose program it can't find.
        static string Quote(string path) =>
            path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+')
                ? path
                : "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
    }

    private static void Fail(string message)
    {
        LiveProgressDisplay.ShowError(message);
        Notify("Download failed", message);
    }

    // Launched from a browser there is no terminal, so the result goes to a desktop notification.
    private static void Notify(string summary, string body)
    {
        if (!OperatingSystem.IsLinux())
            return;
        RunTool("notify-send", "--app-name=Modular", $"Modular: {summary}", body);
    }

    private static string? RunTool(string tool, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process == null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
