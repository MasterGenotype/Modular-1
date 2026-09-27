namespace Modular.Core.Downloads;

/// <summary>
/// Builds the stable identity keys recorded in partial-download sidecars.
/// All callers (backends, CLI, GUI) must use these so a download started in
/// one place can be resumed from another.
/// </summary>
public static class DownloadIdentity
{
    /// <summary>Identity for a NexusMods file.</summary>
    public static string NexusMods(string gameDomain, string modId, string fileId) =>
        $"nexusmods:{gameDomain}:{modId}:{fileId}";

    /// <summary>Identity for a GameBanana file.</summary>
    public static string GameBanana(string modId, string fileId) =>
        $"gamebanana:{modId}:{fileId}";
}
