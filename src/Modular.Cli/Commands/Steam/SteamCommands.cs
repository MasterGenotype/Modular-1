using System.ComponentModel;
using Modular.Cli.UI;
using Modular.Core.Security;
using Modular.Core.Steam;
using Spectre.Console;
using Spectre.Console.Cli;
using SteamKit2.Authentication;

namespace Modular.Cli.Commands.Steam;

/// <summary>
/// Logs Modular into Steam with its own session (password + Steam Guard) and keeps
/// the refresh token in the secret store. Never touches the Steam client.
/// </summary>
public sealed class SteamLoginCommand : AsyncCommand<SteamLoginCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--user|-u <ACCOUNT>")]
        [Description("Steam account name (asked for if omitted)")]
        public string? User { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        var accounts = new SteamAccountStore(new DefaultSecretStore());
        try
        {
            await using var session = new SteamSession();
            AnsiConsole.MarkupLine("[grey]Connecting to Steam…[/]");
            await session.ConnectAsync(cts.Token);

            var user = settings.User is { Length: > 0 } u ? u : AnsiConsole.Ask<string>("Steam account name:");
            var password = AnsiConsole.Prompt(new TextPrompt<string>($"Steam password for [bold]{Markup.Escape(user)}[/]:").Secret());
            var auth = await session.AuthenticateViaCredentialsAsync(user, password, new ConsoleSteamGuard(), cts.Token);

            await session.LogOnAsync(auth.AccountName, auth.RefreshToken, cts.Token);
            await accounts.SaveAsync(auth.AccountName, auth.RefreshToken, cts.Token);

            LiveProgressDisplay.ShowSuccess(
                $"Logged into Steam as {auth.AccountName} (SteamID {session.SteamId}). Token stored in {accounts.Secrets.Description}.");
            if (accounts.Secrets is DefaultSecretStore { LastFallbackReason: { } why })
                LiveProgressDisplay.ShowWarning($"Secret Service unavailable ({why}); used the file fallback.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Login cancelled.");
            return 1;
        }
        catch (Exception ex) when (ex is AuthenticationException or SteamLogOnException or SteamDisconnectedException or TimeoutException)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }

    private sealed class ConsoleSteamGuard : IAuthenticator
    {
        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            AnsiConsole.MarkupLine("[yellow]Approve this sign-in in the Steam mobile app…[/]");
            return Task.FromResult(true); // wait for the approval rather than asking for a code
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
            Task.FromResult(AnsiConsole.Ask<string>(
                previousCodeWasIncorrect ? "[red]Wrong code.[/] Steam Guard code from the app:" : "Steam Guard code from the app:"));

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
            Task.FromResult(AnsiConsole.Ask<string>(
                (previousCodeWasIncorrect ? "[red]Wrong code.[/] " : "") + $"Steam Guard code sent to {Markup.Escape(email)}:"));
    }
}

public sealed class SteamLogoutCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        await new SteamAccountStore(new DefaultSecretStore()).ClearAsync();
        LiveProgressDisplay.ShowSuccess("Removed the stored Steam login. (The session still shows under Steam's authorized devices until you revoke it there.)");
        return 0;
    }
}

public sealed class SteamStatusCommand : AsyncCommand<SteamStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--check")]
        [Description("Connect and log on to prove the stored token still works")]
        public bool Check { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        var accounts = new SteamAccountStore(new DefaultSecretStore());
        var stored = await accounts.LoadAsync(cts.Token);
        if (stored is null)
        {
            LiveProgressDisplay.ShowWarning("Not logged in. Run 'modular steam login'.");
            return 1;
        }
        AnsiConsole.MarkupLine($"Account: [bold]{Markup.Escape(stored.AccountName)}[/]  ·  token in {Markup.Escape(accounts.Secrets.Description)}");
        if (!settings.Check)
            return 0;

        try
        {
            await using var session = await SteamSession.OpenStoredAsync(accounts, cts.Token);
            LiveProgressDisplay.ShowSuccess($"Logged on. SteamID {session.SteamId}, cell {session.CellId}.");
            return 0;
        }
        catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException or TimeoutException)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}

internal static class ConsoleCancellation
{
    /// <summary>A token cancelled by Ctrl+C, which also stops the process from being killed outright.</summary>
    public static CancellationTokenSource Create()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        return cts;
    }
}
