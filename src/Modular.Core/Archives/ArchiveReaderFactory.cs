using Modular.Sdk.Archives;

namespace Modular.Core.Archives;

/// <summary>
/// Factory that creates the appropriate IArchiveReader based on archive file extension.
/// ZIP files use the built-in System.IO.Compression reader; all other supported formats
/// (7z, RAR, TAR, GZ, BZ2, XZ) use SharpCompress.
/// </summary>
public class ArchiveReaderFactory : IArchiveReaderFactory
{
    private static readonly HashSet<string> ZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip"
    };

    private static readonly HashSet<string> SharpCompressExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".lz", ".lzma", ".pak"
    };

    /// <inheritdoc />
    public IArchiveReader? Open(string archivePath)
    {
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
            return null;

        var extension = GetEffectiveExtension(archivePath);

        if (ZipExtensions.Contains(extension))
            return new ZipArchiveReader(archivePath);

        if (SharpCompressExtensions.Contains(extension))
            return new SharpCompressArchiveReader(archivePath);

        // Fallback: try SharpCompress which can auto-detect some formats
        try
        {
            return new SharpCompressArchiveReader(archivePath);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsSupported(string archivePath)
    {
        var extension = GetEffectiveExtension(archivePath);
        return ZipExtensions.Contains(extension) || SharpCompressExtensions.Contains(extension);
    }

    /// <summary>
    /// Gets the effective extension, handling double extensions like .tar.gz.
    /// </summary>
    private static string GetEffectiveExtension(string path)
    {
        var ext = Path.GetExtension(path);

        // Handle compound extensions: .tar.gz, .tar.bz2, .tar.xz
        if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bz2", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lzma", StringComparison.OrdinalIgnoreCase))
        {
            var innerExt = Path.GetExtension(Path.GetFileNameWithoutExtension(path));
            if (innerExt.Equals(".tar", StringComparison.OrdinalIgnoreCase))
                return ext; // Return the compression extension; SharpCompress handles .tar.* natively
        }

        return ext;
    }
}
