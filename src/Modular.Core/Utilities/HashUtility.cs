using System.Security.Cryptography;

namespace Modular.Core.Utilities;

/// <summary>
/// Shared utility for computing file and stream hashes.
/// Supports MD5, SHA1, and SHA256.
/// </summary>
public static class HashUtility
{
    /// <summary>
    /// Computes a hash of the specified file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lowercase hex string of the hash.</returns>
    public static async Task<string> ComputeFileHashAsync(
        string filePath,
        HashAlgorithmKind algorithm = HashAlgorithmKind.SHA256,
        CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await ComputeStreamHashAsync(stream, algorithm, ct);
    }

    /// <summary>
    /// Computes a hash from a stream.
    /// </summary>
    /// <param name="stream">The stream to hash.</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lowercase hex string of the hash.</returns>
    public static async Task<string> ComputeStreamHashAsync(
        Stream stream,
        HashAlgorithmKind algorithm = HashAlgorithmKind.SHA256,
        CancellationToken ct = default)
    {
        using var hashAlg = CreateAlgorithm(algorithm);
        var hashBytes = await hashAlg.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies that a file matches an expected hash.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="expectedHash">Expected hash (hex string, case-insensitive).</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the hashes match.</returns>
    public static async Task<bool> VerifyFileHashAsync(
        string filePath,
        string expectedHash,
        HashAlgorithmKind algorithm = HashAlgorithmKind.SHA256,
        CancellationToken ct = default)
    {
        var actual = await ComputeFileHashAsync(filePath, algorithm, ct);
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static HashAlgorithm CreateAlgorithm(HashAlgorithmKind kind) => kind switch
    {
        HashAlgorithmKind.MD5 => MD5.Create(),
        HashAlgorithmKind.SHA1 => SHA1.Create(),
        HashAlgorithmKind.SHA256 => SHA256.Create(),
        _ => SHA256.Create()
    };
}

/// <summary>
/// Supported hash algorithm types.
/// </summary>
public enum HashAlgorithmKind
{
    MD5,
    SHA1,
    SHA256
}
