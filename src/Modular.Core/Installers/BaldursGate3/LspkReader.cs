using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// Reads the file table of a Larian <c>.pak</c> (LSPK) package, and small files from it.
/// </summary>
/// <remarks>
/// <para>
/// Only the table is needed: knowing which virtual paths a pak provides is what lets a loose
/// file under <c>Data/</c> be checked against it. The table is a single LZ4 block of
/// fixed-size entries, so listing a multi-gigabyte pak is one seek and one small read.
/// </para>
/// <para>
/// Versions 15, 16 and 18 are understood, which covers every Baldur's Gate 3 release build.
/// Anything else, and any malformed package, yields an empty list rather than an exception:
/// the caller is running an advisory check and must not fail an install over it.
/// </para>
/// </remarks>
public static class LspkReader
{
    private const uint Signature = 0x4B50534C; // "LSPK"
    private const int NameLength = 256;

    // A table this large is not a real mod; refuse rather than allocate for a corrupt header.
    private const int MaxEntries = 2_000_000;

    // Extraction is for small metadata files; anything bigger is refused, not buffered.
    private const long MaxFileSize = 16 * 1024 * 1024;

    // Low nibble of an entry's flags.
    private const int CompressionNone = 0;
    private const int CompressionZlib = 1;
    private const int CompressionLz4 = 2;

    /// <summary>
    /// Whether a file starts with the LSPK signature. The later parts of a split pak
    /// (<c>Name_1.pak</c>, …) hold only file data and do not.
    /// </summary>
    /// <param name="pakPath">Absolute path to the file.</param>
    /// <returns><c>true</c> for a pak with its own header; <c>false</c> otherwise or when unreadable.</returns>
    public static bool HasHeader(string pakPath)
    {
        try
        {
            using var stream = new FileStream(
                pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[4];
            return stream.Read(head) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Signature;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists the virtual paths packed into a pak.
    /// </summary>
    /// <param name="pakPath">Absolute path to the <c>.pak</c>.</param>
    /// <returns>Paths using forward slashes, or an empty list when the pak is unreadable.</returns>
    public static IReadOnlyList<string> ReadFileNames(string pakPath)
    {
        try
        {
            using var stream = new FileStream(
                pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadFileNames(stream);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Lists the virtual paths packed into a pak.
    /// </summary>
    /// <param name="stream">A seekable stream positioned anywhere in the pak.</param>
    /// <returns>Paths using forward slashes, or an empty list when the pak is unreadable.</returns>
    public static IReadOnlyList<string> ReadFileNames(Stream stream) =>
        ReadEntries(stream).Select(e => e.Name).ToList();

    /// <summary>
    /// Extracts the first packed file whose virtual path satisfies a predicate.
    /// </summary>
    /// <param name="pakPath">Absolute path to the <c>.pak</c>.</param>
    /// <param name="match">Chooses the file by its forward-slash virtual path.</param>
    /// <returns>
    /// The file's decompressed bytes, or <c>null</c> when nothing matched, the pak is
    /// unreadable, or the file uses a compression method this reader does not decode.
    /// </returns>
    public static byte[]? ReadFile(string pakPath, Func<string, bool> match)
    {
        try
        {
            using var stream = new FileStream(
                pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadFile(stream, match);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the first packed file whose virtual path satisfies a predicate.
    /// </summary>
    /// <param name="stream">A seekable stream positioned anywhere in the pak.</param>
    /// <param name="match">Chooses the file by its forward-slash virtual path.</param>
    /// <returns>The file's decompressed bytes, or <c>null</c>; see the path overload.</returns>
    /// <remarks>
    /// Only files in the pak's first part are read, which is where every mod keeps
    /// everything. Stored, zlib and LZ4 block data are decoded; zstd is not.
    /// </remarks>
    public static byte[]? ReadFile(Stream stream, Func<string, bool> match)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(match);

        foreach (var entry in ReadEntries(stream))
        {
            if (match(entry.Name))
                return Decode(stream, entry);
        }

        return null;
    }

    /// <summary>
    /// Extracts every packed file whose virtual path satisfies a predicate.
    /// </summary>
    /// <param name="pakPath">Absolute path to the <c>.pak</c>.</param>
    /// <param name="match">Chooses files by their forward-slash virtual paths.</param>
    /// <returns>
    /// Each matching file's path and decompressed bytes, in pak order. A file that cannot be
    /// decoded is left out; an unreadable pak gives an empty list.
    /// </returns>
    public static IReadOnlyList<(string Name, byte[] Data)> ReadFiles(string pakPath, Func<string, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        var files = new List<(string Name, byte[] Data)>();
        try
        {
            using var stream = new FileStream(
                pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            foreach (var entry in ReadEntries(stream))
            {
                if (match(entry.Name) && Decode(stream, entry) is { } data)
                    files.Add((entry.Name, data));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return files;
    }

    // The decompressed bytes of one entry, or null when it is out of bounds, too big or
    // compressed in a way this reader does not decode.
    private static byte[]? Decode(Stream stream, Entry entry)
    {
        if (entry.Part != 0 || entry.SizeOnDisk <= 0 || entry.SizeOnDisk > MaxFileSize ||
            entry.UncompressedSize > MaxFileSize ||
            entry.Offset < 0 || entry.Offset + entry.SizeOnDisk > stream.Length)
        {
            return null;
        }

        var data = new byte[entry.SizeOnDisk];
        stream.Position = entry.Offset;
        if (!TryReadExactly(stream, data))
            return null;

        switch (entry.Compression)
        {
            case CompressionNone:
                return data;

            case CompressionZlib:
                try
                {
                    using var zlib = new ZLibStream(
                        new MemoryStream(data), CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    return output.ToArray();
                }
                catch (InvalidDataException)
                {
                    return null;
                }

            case CompressionLz4:
                var decoded = new byte[entry.UncompressedSize];
                return TryDecodeLz4Block(data, decoded) ? decoded : null;

            default:
                return null;
        }
    }

    private static List<Entry> ReadEntries(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var none = new List<Entry>();

        Span<byte> header = stackalloc byte[20];
        stream.Position = 0;

        if (!TryReadExactly(stream, header) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != Signature)
        {
            return none;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var entrySize = version switch
        {
            18 => 272,
            15 or 16 => 296,
            _ => 0
        };

        if (entrySize == 0)
            return none;

        var listOffset = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        if (listOffset <= 0 || listOffset + 8 > stream.Length)
            return none;

        Span<byte> listHeader = stackalloc byte[8];
        stream.Position = listOffset;
        if (!TryReadExactly(stream, listHeader))
            return none;

        var fileCount = BinaryPrimitives.ReadInt32LittleEndian(listHeader);
        var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(listHeader[4..]);

        if (fileCount <= 0 || fileCount > MaxEntries ||
            compressedSize <= 0 || listOffset + 8 + compressedSize > stream.Length)
        {
            return none;
        }

        var compressed = new byte[compressedSize];
        if (!TryReadExactly(stream, compressed))
            return none;

        var table = new byte[fileCount * entrySize];
        if (!TryDecodeLz4Block(compressed, table))
            return none;

        var entries = new List<Entry>(fileCount);

        for (var i = 0; i < fileCount; i++)
        {
            var raw = table.AsSpan(i * entrySize, entrySize);
            var name = raw[..NameLength];
            var end = name.IndexOf((byte)0);
            if (end < 0)
                end = NameLength;

            if (end == 0)
                continue;

            var fields = raw[NameLength..];
            long offset, sizeOnDisk, uncompressedSize;
            uint part, flags;

            if (version == 18)
            {
                offset = BinaryPrimitives.ReadUInt32LittleEndian(fields) |
                         ((long)BinaryPrimitives.ReadUInt16LittleEndian(fields[4..]) << 32);
                part = fields[6];
                flags = fields[7];
                sizeOnDisk = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
                uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]);
            }
            else
            {
                offset = BinaryPrimitives.ReadInt64LittleEndian(fields);
                sizeOnDisk = BinaryPrimitives.ReadInt64LittleEndian(fields[8..]);
                uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(fields[16..]);
                part = BinaryPrimitives.ReadUInt32LittleEndian(fields[24..]);
                flags = BinaryPrimitives.ReadUInt32LittleEndian(fields[28..]);
            }

            entries.Add(new Entry(
                Encoding.UTF8.GetString(name[..end]).Replace('\\', '/'),
                offset,
                sizeOnDisk,
                uncompressedSize,
                part,
                (int)(flags & 0x0F)));
        }

        return entries;
    }

    private readonly record struct Entry(
        string Name, long Offset, long SizeOnDisk, long UncompressedSize, uint Part, int Compression);

    /// <summary>
    /// Decodes one raw LZ4 block into a buffer of exactly the decompressed size.
    /// </summary>
    /// <param name="source">The compressed block.</param>
    /// <param name="destination">Buffer sized to the decompressed length.</param>
    /// <returns>True when the block decoded to exactly fill the destination.</returns>
    internal static bool TryDecodeLz4Block(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int src = 0, dst = 0;

        while (src < source.Length)
        {
            var token = source[src++];

            var literalLength = token >> 4;
            if (literalLength == 15 && !TryReadLength(source, ref src, ref literalLength))
                return false;

            if (literalLength > source.Length - src || literalLength > destination.Length - dst)
                return false;

            source.Slice(src, literalLength).CopyTo(destination[dst..]);
            src += literalLength;
            dst += literalLength;

            // The last sequence is literals only.
            if (src >= source.Length)
                break;

            if (source.Length - src < 2)
                return false;

            var offset = source[src] | (source[src + 1] << 8);
            src += 2;

            if (offset == 0 || offset > dst)
                return false;

            var matchLength = token & 0x0F;
            if (matchLength == 15 && !TryReadLength(source, ref src, ref matchLength))
                return false;

            matchLength += 4;

            if (matchLength > destination.Length - dst)
                return false;

            // Byte by byte: a match may overlap its own output.
            for (var i = 0; i < matchLength; i++, dst++)
                destination[dst] = destination[dst - offset];
        }

        return dst == destination.Length;
    }

    private static bool TryReadLength(ReadOnlySpan<byte> source, ref int position, ref int length)
    {
        byte next;
        do
        {
            if (position >= source.Length)
                return false;

            next = source[position++];
            length += next;

            if (length < 0)
                return false;
        }
        while (next == 255);

        return true;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
                return false;

            read += n;
        }

        return true;
    }
}
