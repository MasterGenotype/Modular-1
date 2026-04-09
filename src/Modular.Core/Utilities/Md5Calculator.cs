using System.Security.Cryptography;

namespace Modular.Core.Utilities;

/// <summary>
/// MD5 hash calculation utilities.
/// </summary>
public static class Md5Calculator
{
    /// <summary>
    /// Calculates the MD5 hash of a file asynchronously.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MD5 hash as lowercase hex string</returns>
    public static async Task<string> CalculateMd5Async(string filePath, CancellationToken cancellationToken = default)
    {
        using var md5 = MD5.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await md5.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

}
