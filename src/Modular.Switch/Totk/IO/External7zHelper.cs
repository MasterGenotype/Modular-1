using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using TkSharp.Core;

namespace Modular.Switch.Totk.IO;

/// <summary>
/// Port of TKMM v2.0.0-rc1 <c>Tkmm.Core/Helpers/External7zHelper.cs</c>: extracting .7z
/// mods with an external 7-Zip executable. The executable path comes from
/// <see cref="TotkSettings.SevenZipPath"/> (rc1's <c>Config.SevenZipPath</c>). Behaviour is
/// unchanged.
/// </summary>
public static class External7zHelper
{
    /// <summary>Extracts <paramref name="file"/> into <paramref name="output"/> with 7-Zip.</summary>
    public static async ValueTask ExtractToFolder(TotkSettings settings, string file, string output, CancellationToken ct = default)
    {
        ProcessStartInfo info = new()
        {
            FileName = settings.SevenZipPath,
            Arguments = $"""
                x -o"{output}" -- "{file}"
                """,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"Failed to start 7z from '{settings.SevenZipPath}'");
        await process.WaitForExitAsync(ct);

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        TkLog.Instance.LogDebug("[External 7z Output] {StandardOutput}", stdout);
    }

    /// <summary>
    /// True when a 7-Zip executable is configured or can be found on <c>PATH</c> (which
    /// then sets <see cref="TotkSettings.SevenZipPath"/>) and starts.
    /// </summary>
    public static bool CanUseExternal(TotkSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SevenZipPath))
            TryAutoSetSevenZipPath(settings);

        return File.Exists(settings.SevenZipPath)
               && Process.Start(new ProcessStartInfo(settings.SevenZipPath)) is not null;
    }

    private static void TryAutoSetSevenZipPath(TotkSettings settings)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var location in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                var candidate = Path.Combine(Environment.GetEnvironmentVariable(location)!, "7-Zip", "7z.exe");

                if (!File.Exists(candidate))
                    continue;

                settings.SevenZipPath = candidate;
                return;
            }
        }
        else
        {
            foreach (var binary in new[] { "7zz", "7z" })
            {
                var resolved = Which(binary);

                if (resolved is null || !File.Exists(resolved))
                    continue;

                settings.SevenZipPath = resolved;
                return;
            }
        }
    }

    private static string? Which(string name)
    {
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo("which", name)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            );

            if (p is not null)
            {
                p.WaitForExit();
                var output = p.StandardOutput.ReadToEnd().Trim();
                return string.IsNullOrWhiteSpace(output) ? null : output;
            }
        }
        catch (Exception ex)
        {
            TkLog.Instance.LogWarning("Failed to run 'which' command for {Name}: {Exception}", name, ex);
        }
        return null;
    }
}
