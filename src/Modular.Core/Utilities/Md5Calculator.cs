using System.Security.Cryptography;

namespace Modular.Core.Utilities;

/// <summary>
/// MD5 hash calculation utilities.
/// </summary>
public static class Md5Calculator
{
    /// <summary>
    /// Calculates the MD5 hash of a file.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <returns>MD5 hash as lowercase hex string</returns>
    public static string CalculateMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

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

    /// <summary>
    /// Verifies that a file matches the expected MD5 hash.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <param name="expectedMd5">Expected MD5 hash (case-insensitive)</param>
    /// <returns>True if the hash matches</returns>
    public static bool VerifyMd5(string filePath, string expectedMd5)
    {
        if (string.IsNullOrEmpty(expectedMd5))
            return true; // No expected hash, consider verified

        var actual = CalculateMd5(filePath);
        return actual.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that a file matches the expected MD5 hash asynchronously.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <param name="expectedMd5">Expected MD5 hash (case-insensitive)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the hash matches</returns>
    public static async Task<bool> VerifyMd5Async(string filePath, string expectedMd5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(expectedMd5))
            return true; // No expected hash, consider verified

        var actual = await CalculateMd5Async(filePath, cancellationToken);
        return actual.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
    }
}
