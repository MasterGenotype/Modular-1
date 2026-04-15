namespace Modular.Sdk;

/// <summary>
/// Well-known game identifiers used to route mod installers.
/// Values match NexusMods game slugs. Steam AppIDs are noted in comments
/// and are accepted interchangeably by game-specific installers.
/// </summary>
public static class GameIds
{
    /// <summary>Cyberpunk 2077 (Steam AppID 1091500).</summary>
    public const string Cyberpunk2077 = "cyberpunk2077";

    /// <summary>Final Fantasy VII Remake Intergrade (Steam AppID 1462040).</summary>
    public const string FinalFantasy7Remake = "finalfantasy7remake";

    /// <summary>Horizon Zero Dawn (Steam AppID 1151640).</summary>
    public const string HorizonZeroDawn = "horizonzerodawn";
}
