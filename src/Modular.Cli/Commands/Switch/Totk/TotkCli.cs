using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Switch.Totk;
using Spectre.Console;
using TkSharp.Core.Models;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// The TotK merge service as the CLI uses it: settings from <c>config.json</c>, TkSharp's
/// log on the console, and helpers shared by the <c>switch totk</c> commands.
/// </summary>
internal sealed class TotkCli
{
    private readonly ConfigurationService _configService;
    private readonly AppSettings _config;

    private TotkCli(ConfigurationService configService, AppSettings config, TotkMergeService service)
    {
        _configService = configService;
        _config = config;
        Service = service;
    }

    public TotkMergeService Service { get; }

    public TotkSettings Settings => _config.SwitchTotk;

    /// <summary>Loads the settings and opens the mod store.</summary>
    /// <param name="verbose">Also print TkSharp's informational messages.</param>
    public static async Task<TotkCli> OpenAsync(bool verbose = false)
    {
        var configService = new ConfigurationService();
        var config = await configService.LoadAsync();
        var logger = new ConsoleTkLogger(verbose ? LogLevel.Information : LogLevel.Warning);
        return new TotkCli(configService, config, new TotkMergeService(config.SwitchTotk, logger: logger));
    }

    /// <summary>Saves the settings (the service may have filled some in, e.g. the 7-Zip path).</summary>
    public Task SaveSettingsAsync() => _configService.SaveAsync(_config);

    /// <summary>Prints each step of an install, merge or export.</summary>
    public static IProgress<TotkProgress> ConsoleProgress { get; } = new SyncProgress<TotkProgress>(p =>
        AnsiConsole.MarkupLine(p.Detail is null
            ? $"[grey]{Markup.Escape(p.Phase)}…[/]"
            : $"[grey]{Markup.Escape(p.Phase)}:[/] {Markup.Escape(p.Detail)}"));

    /// <summary>
    /// Finds an installed mod by ID or name, printing why when there isn't exactly one.
    /// </summary>
    public TkMod? FindModOrReport(string idOrName)
    {
        try
        {
            if (Service.FindMod(idOrName) is { } mod)
                return mod;

            AnsiConsole.MarkupLine($"[red]No installed mod named[/] {Markup.Escape(idOrName)}. " +
                                   "[grey]See: modular switch totk list --all[/]");
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        }

        return null;
    }

    /// <summary>
    /// Finds <paramref name="idOrName"/> in the current profile, printing why when it isn't
    /// there.
    /// </summary>
    public TkProfileMod? FindProfileModOrReport(string idOrName)
    {
        if (FindModOrReport(idOrName) is not { } mod)
            return null;

        var profile = Service.CurrentProfile;
        if (TotkMergeService.FindProfileMod(profile, mod) is { } profileMod)
            return profileMod;

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(mod.Name)} is not in the profile[/] {Markup.Escape(profile.Name)}.");
        return null;
    }

    /// <summary>Prints whether the game can be read with the current settings.</summary>
    /// <returns>True when it can.</returns>
    public bool ReportRom()
    {
        using var rom = Service.TryGetTkRom(out var hasBaseGame, out var hasUpdate, out var error);
        if (rom != null)
        {
            AnsiConsole.MarkupLine($"[green]Game found[/] [grey](version {rom.GameVersion})[/]");
            return true;
        }

        AnsiConsole.MarkupLine($"[red]The game could not be read:[/] {Markup.Escape(error ?? "unknown error")}");
        AnsiConsole.MarkupLine($"[grey]Base game: {(hasBaseGame ? "found" : "missing")}, update: {(hasUpdate ? "found" : "missing")}. " +
                               "Configure it with: modular switch totk config[/]");
        return false;
    }

    /// <summary>Runs <paramref name="action"/>, reporting a failure instead of throwing.</summary>
    public static async Task<int> RunAsync(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.GetType().Name)}:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    /// <summary>A token cancelled by Ctrl+C.</summary>
    public static CancellationToken CancelOnCtrlC()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts.Token;
    }

    /// <summary>Reports on the calling thread, keeping console output in order.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>TkSharp's log on the console, at or above <paramref name="minimum"/>.</summary>
    private sealed class ConsoleTkLogger(LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var color = logLevel switch
            {
                >= LogLevel.Error => "red",
                LogLevel.Warning => "yellow",
                _ => "grey"
            };
            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(formatter(state, exception))}[/]");
        }
    }
}
