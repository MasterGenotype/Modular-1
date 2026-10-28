using System.Globalization;
using System.Web;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// An <c>nxm://</c> link from a "Mod Manager Download" button on nexusmods.com:
/// <c>nxm://{game}/mods/{mod}/files/{file}?key={key}&amp;expires={unix}&amp;user_id={id}</c>.
/// </summary>
/// <remarks>
/// The key and expiry are what let an account without Premium download through the
/// API: Nexus issues them for the one file the user clicked, and
/// <c>download_link.json</c> accepts them in place of a Premium membership.
/// </remarks>
public sealed record NxmLink(
    string GameDomain,
    int ModId,
    int FileId,
    string? Key,
    DateTimeOffset? Expires,
    long? UserId)
{
    /// <summary>
    /// Whether the link's key has expired; Nexus then refuses it and the user has to
    /// click the button again.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) => Expires is { } expires && expires <= now;

    /// <summary>
    /// Parses a mod file link.
    /// </summary>
    /// <exception cref="FormatException">The text is not an nxm mod file link.</exception>
    public static NxmLink Parse(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Not an nxm:// link: {text}");

        // The game domain is the host; Uri lowercases it, which Nexus accepts.
        var game = uri.Host;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 1 && segments[0].Equals("collections", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(
                "Collection links are not supported yet. Use 'modular collection' for Nexus collections.");

        if (string.IsNullOrEmpty(game) || segments.Length != 4 ||
            !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var modId) ||
            !int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var fileId))
            throw new FormatException(
                $"Not an nxm mod file link (expected nxm://<game>/mods/<id>/files/<id>): {text}");

        var query = HttpUtility.ParseQueryString(uri.Query);
        var key = query["key"];
        DateTimeOffset? expires =
            long.TryParse(query["expires"], NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : null;
        long? userId =
            long.TryParse(query["user_id"], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                ? uid
                : null;

        return new NxmLink(game, modId, fileId, string.IsNullOrEmpty(key) ? null : key, expires, userId);
    }
}
