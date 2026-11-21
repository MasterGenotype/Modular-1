using System.Text.Json;

namespace Modular.Core.Depots;

/// <summary>
/// Which build of a Steam game was installed when something was recorded (a deploy, a snapshot,
/// a mod list): the true manifest of each depot, plus branch and build number when known.
/// </summary>
public sealed record BuildStamp(string? Branch, uint? BuildId, Dictionary<uint, ulong> Depots, DateTimeOffset StampedAt)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>"legacy · build 322538", "build 3406487", or the depots' manifests when nothing else is known.</summary>
    public string Label =>
        string.Join(" · ", new[]
        {
            Branch is { Length: > 0 } b && b != "public" ? b : null,
            BuildId is { } id ? $"build {id}" : null,
        }.Where(x => x is not null)) is { Length: > 0 } s ? s : string.Join(", ", Depots.Select(d => $"{d.Key}={d.Value}"));

    /// <summary>Same build when every depot both stamps know has the same manifest.</summary>
    public bool SameBuildAs(BuildStamp other)
    {
        var shared = Depots.Keys.Intersect(other.Depots.Keys).ToList();
        return shared.Count > 0 && shared.All(d => Depots[d] == other.Depots[d]);
    }

    /// <summary>True when this stamp's depots are exactly these manifests (for matching a listed version).</summary>
    public bool Matches(IReadOnlyDictionary<uint, ulong> manifests)
    {
        var shared = Depots.Keys.Intersect(manifests.Keys).ToList();
        return shared.Count > 0 && shared.All(d => Depots[d] == manifests[d]);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BuildStamp? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<BuildStamp>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The build installed now, without hashing: the pin's true manifests when Modular pinned the
    /// game (its acf may claim a newer build), else Steam's own record. Null when the app isn't
    /// installed or Steam is mid-update.
    /// </summary>
    public static BuildStamp? ForApp(uint appId, BuildPinStore? pins = null)
    {
        var acf = SteamAppManifest.Find(appId);
        if (acf is null)
            return null;
        var pin = (pins ?? new BuildPinStore()).Load(appId);
        if (pin is not null)
        {
            return new BuildStamp(pin.Branch, pin.ReportsCurrent ? pin.BuildId : pin.BuildId ?? acf.BuildId,
                new Dictionary<uint, ulong>(pin.Depots), DateTimeOffset.UtcNow);
        }
        // Steam's record is true only once an update has finished (StateFlags 4 = fully installed).
        if (acf.StateFlags != 4)
            return null;
        var depots = acf.InstalledDepots.Where(d => d.ManifestId is not null).ToDictionary(d => d.DepotId, d => d.ManifestId!.Value);
        if (depots.Count == 0)
            return null;
        var branch = Depots_BetaKey(acf);
        return new BuildStamp(branch, acf.BuildId, depots, DateTimeOffset.UtcNow);
    }

    private static string? Depots_BetaKey(SteamAppManifest acf)
    {
        try
        {
            return SteamAcfEditor.Load(acf.AcfPath).Get("UserConfig", "BetaKey");
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
