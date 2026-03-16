using System.ComponentModel;
using Modular.Cli.UI;
using Modular.Core.GameDetection;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.GameDetection;

/// <summary>
/// Scans for installed Steam games and optionally detects their engines.
/// </summary>
public sealed class DetectGamesCommand : AsyncCommand<DetectGamesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--engines")]
        [Description("Also detect game engines")]
        public bool DetectEngines { get; init; }

        [CommandOption("--json")]
        [Description("Output as JSON")]
        public bool Json { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var scanner = new SteamGameScanner();
            var engineDetector = settings.DetectEngines ? new CompositeEngineDetector() : null;

            var games = await scanner.ScanAllAsync();

            if (games.Count == 0)
            {
                LiveProgressDisplay.ShowWarning("No Steam games found.");
                return 0;
            }

            LiveProgressDisplay.ShowSuccess($"Found {games.Count} installed Steam games:");
            Console.WriteLine();

            foreach (var game in games.OrderBy(g => g.DisplayName))
            {
                var sizeStr = game.SizeOnDisk > 0
                    ? $" ({game.SizeOnDisk / (1024.0 * 1024):F0} MB)"
                    : "";
                var status = game.IsFullyInstalled ? "" : " [INCOMPLETE]";

                Console.Write($"  [{game.AppId}] {game.DisplayName}{sizeStr}{status}");

                if (engineDetector != null && Directory.Exists(game.InstallPath))
                {
                    var result = engineDetector.Detect(game.InstallPath);
                    if (result != null)
                    {
                        Console.Write($" — Engine: {result.EngineFamily} ({result.Confidence:P0})");
                    }
                }

                Console.WriteLine();

                if (settings.Verbose)
                {
                    Console.WriteLine($"    Path: {game.InstallPath}");
                    Console.WriteLine($"    Library: {game.LibraryRoot}");
                }
            }

            Console.WriteLine();
            LiveProgressDisplay.ShowInfo($"Total: {games.Count} games across {games.Select(g => g.LibraryRoot).Distinct().Count()} library roots");
            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}

/// <summary>
/// Detects the game engine for a specific game path or AppID.
/// </summary>
public sealed class DetectEngineCommand : AsyncCommand<DetectEngineCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path-or-appid>")]
        [Description("Game install path or Steam AppID")]
        public string Target { get; init; } = string.Empty;

        [CommandOption("--all")]
        [Description("Show all detector results, not just the best")]
        public bool All { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            string installPath;

            // Check if target is an AppID
            if (int.TryParse(settings.Target, out var appId))
            {
                var scanner = new SteamGameScanner();
                var games = await scanner.ScanAllAsync();
                var game = games.FirstOrDefault(g => g.AppId == appId);
                if (game == null)
                {
                    LiveProgressDisplay.ShowError($"AppID {appId} not found in Steam libraries.");
                    return 1;
                }
                installPath = game.InstallPath;
                LiveProgressDisplay.ShowInfo($"Game: {game.DisplayName} ({installPath})");
            }
            else
            {
                installPath = settings.Target;
            }

            if (!Directory.Exists(installPath))
            {
                LiveProgressDisplay.ShowError($"Directory not found: {installPath}");
                return 1;
            }

            var detector = new CompositeEngineDetector();

            if (settings.All)
            {
                var results = detector.DetectAll(installPath);
                if (results.Count == 0)
                {
                    LiveProgressDisplay.ShowWarning("No engine detected.");
                    return 0;
                }

                Console.WriteLine("Detection results:");
                foreach (var result in results)
                {
                    Console.WriteLine($"  {result.EngineFamily}: {result.Confidence:P0}");
                    foreach (var evidence in result.Evidence)
                        Console.WriteLine($"    - {evidence}");
                }
            }
            else
            {
                var result = detector.Detect(installPath);
                if (result == null)
                {
                    LiveProgressDisplay.ShowWarning("No engine detected.");
                    return 0;
                }

                LiveProgressDisplay.ShowSuccess($"Engine: {result.EngineFamily} (Confidence: {result.Confidence:P0})");
                Console.WriteLine("Evidence:");
                foreach (var evidence in result.Evidence)
                    Console.WriteLine($"  - {evidence}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}
