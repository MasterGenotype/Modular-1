using Modular.Cli.UI;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Profile;

/// <summary>
/// Lists available mod profiles.
/// </summary>
public sealed class ProfileListCommand : AsyncCommand
{
    public override Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "profiles");

            if (!Directory.Exists(configDir))
            {
                LiveProgressDisplay.ShowInfo("No profiles found");
                return Task.FromResult(0);
            }

            var profiles = Directory.GetFiles(configDir, "*.json")
                .Concat(Directory.GetFiles(configDir, "*.zip"))
                .Concat(Directory.GetFiles(configDir, "*.modpack"));

            if (!profiles.Any())
            {
                LiveProgressDisplay.ShowInfo("No profiles found");
                return Task.FromResult(0);
            }

            Console.WriteLine("Available profiles:");
            foreach (var profile in profiles)
            {
                var name = Path.GetFileNameWithoutExtension(profile);
                var size = new FileInfo(profile).Length;
                Console.WriteLine($"  - {name} ({size} bytes)");
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to list profiles: {ex.Message}");
            return Task.FromResult(1);
        }
    }
}
