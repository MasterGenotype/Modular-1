using System.Text.Json;

namespace Modular.Core.Installers;

/// <summary>
/// The answers a mod was installed with: installer question id → option id, covering FOMOD
/// pages (each checkbox is a yes/no question), BG3 variant folders, Cyberpunk presets and the
/// like. Saved with the installed mod so updates, reinstalls, restores and mod lists repeat them.
/// </summary>
public static class InstallChoices
{
    public static string? ToJson(IReadOnlyDictionary<string, string>? choices) =>
        choices is { Count: > 0 } ? JsonSerializer.Serialize(choices) : null;

    public static IReadOnlyDictionary<string, string>? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map is { Count: > 0 } ? new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
