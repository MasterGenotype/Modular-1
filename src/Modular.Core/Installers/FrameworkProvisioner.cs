using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Modular.Sdk.Modules;

namespace Modular.Core.Installers;

/// <summary>
/// Downloads the loaders a game's mods need (<see cref="IGameModule.GetRequiredFrameworks"/>)
/// and hands them to the installer as ordinary archives.
/// </summary>
/// <remarks>
/// Every download is pinned to a SHA-256; a mismatch is an error, never a warning. Verified
/// files are kept under <c>&lt;store&gt;/frameworks/&lt;id&gt;/&lt;version&gt;/</c>, so a
/// reinstall needs no network.
/// </remarks>
public sealed class FrameworkProvisioner
{
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz" };

    private readonly string _storeDirectory;
    private readonly Func<Uri, CancellationToken, Task<Stream>> _download;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates the provisioner.
    /// </summary>
    /// <param name="storeDirectory">Where verified downloads are kept.</param>
    /// <param name="download">
    /// Opens a download; defaults to a plain HTTP GET. Tests pass their own.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public FrameworkProvisioner(
        string storeDirectory,
        Func<Uri, CancellationToken, Task<Stream>>? download = null,
        ILogger? logger = null)
    {
        _storeDirectory = storeDirectory;
        _download = download ?? HttpDownloadAsync;
        _logger = logger;
    }

    /// <summary>
    /// Gets an installable archive for a framework, downloading it when it is not stored.
    /// </summary>
    /// <param name="framework">The framework.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to an archive the installers can read.</returns>
    /// <exception cref="InvalidDataException">The download does not match its pinned hash.</exception>
    public async Task<string> GetArchiveAsync(RequiredFramework framework, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(framework);

        var directory = Path.Combine(_storeDirectory, "frameworks", framework.Id, framework.Version);
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, framework.FileName);

        if (!File.Exists(file) || !await MatchesAsync(file, framework.Sha256, ct))
            await DownloadAsync(framework, file, ct);

        if (ArchiveExtensions.Contains(Path.GetExtension(file)))
            return file;

        // A bare file installs at the game root: wrap it in an archive holding just it.
        var archive = Path.Combine(directory, $"{framework.Id}-{framework.Version}.zip");
        if (!File.Exists(archive))
        {
            var temporary = archive + ".part";
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(file, framework.FileName, CompressionLevel.Fastest);
            File.Move(temporary, archive, overwrite: true);
        }

        return archive;
    }

    private async Task DownloadAsync(RequiredFramework framework, string file, CancellationToken ct)
    {
        _logger?.LogInformation(
            "Downloading {Framework} {Version} from {Url}",
            framework.DisplayName, framework.Version, framework.DownloadUrl);

        var temporary = file + ".part";

        await using (var source = await _download(new Uri(framework.DownloadUrl), ct))
        await using (var target = File.Create(temporary))
        {
            await source.CopyToAsync(target, ct);
        }

        if (!await MatchesAsync(temporary, framework.Sha256, ct))
        {
            File.Delete(temporary);
            throw new InvalidDataException(
                $"{framework.DisplayName} {framework.Version} from {framework.DownloadUrl} does not match " +
                "its pinned SHA-256; it was not installed.");
        }

        File.Move(temporary, file, overwrite: true);
    }

    private static async Task<bool> MatchesAsync(string file, string sha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static async Task<Stream> HttpDownloadAsync(Uri uri, CancellationToken ct)
    {
        var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }
}
