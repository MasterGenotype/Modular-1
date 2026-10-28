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

    // Browsers launch one handler per click; this keeps them from writing the
    // download database at the same time.
    private static readonly TimeSpan LockWait = TimeSpan.FromMinutes(30);

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

        NxmLink link;
        try
        {
            link = NxmLink.Parse(settings.Url!);
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

        try
        {
            using var hold = await AcquireLockAsync(cts.Token);

            // Loads state only once the lock is held, so it sees the previous click's records.
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

            LiveProgressDisplay.ShowInfo(
                $"Downloading {link.GameDomain} mod {link.ModId}, file {link.FileId}...");
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
                    Console.WriteLine($"  {percent}%");
                }),
                cts.Token);

            if (alreadyDownloaded)
            {
                LiveProgressDisplay.ShowInfo($"{fileName} is already downloaded (use --force to download it again).");
                Notify("Already downloaded", fileName);
                return 0;
            }

            // Same follow-up as 'modular download'.
            if (services.Settings.AutoRename)
            {
                var renameService = new RenameService(services.Settings, services.RateLimiter,
                    services.MetadataCache, services.LoggerFactory?.CreateLogger<RenameService>());
                var gameDomainPath = renameService.GetGameDomainPath(link.GameDomain);
                await renameService.ReorganizeAndRenameModsAsync(
                    gameDomainPath, services.Settings.OrganizeByCategory, cts.Token);
                if (services.Settings.OrganizeByCategory)
                    await renameService.RenameCategoryFoldersAsync(gameDomainPath, cts.Token);
            }

            await services.SaveStateAsync();

            LiveProgressDisplay.ShowSuccess($"Downloaded {fileName}");
            Notify("Downloaded", fileName);
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LiveProgressDisplay.ShowWarning("Cancelled. Run the same link again to resume.");
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

    private static async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "nxm.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var deadline = DateTime.UtcNow + LockWait;
        var announced = false;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                if (!announced)
                {
                    Console.WriteLine("Waiting for another nxm download to finish...");
                    announced = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
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
