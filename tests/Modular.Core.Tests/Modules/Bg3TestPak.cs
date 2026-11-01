using System.IO.Compression;
using System.Text;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// Builds small version-18 LSPK packages with real file data, for tests that read files
/// out of a pak rather than just listing it.
/// </summary>
internal static class Bg3TestPak
{
    public const int Stored = 0;
    public const int Zlib = 1;
    public const int Lz4 = 2;

    private const int HeaderSize = 40;
    private const int EntrySize = 272;

    /// <summary>
    /// Writes a pak holding the given files.
    /// </summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="files">Virtual path, contents and compression method of each file.</param>
    public static void Write(string path, params (string Name, byte[] Data, int Compression)[] files)
    {
        var table = new byte[files.Length * EntrySize];
        var body = new MemoryStream();

        for (var i = 0; i < files.Length; i++)
        {
            var (name, data, compression) = files[i];
            var packed = compression switch
            {
                Zlib => Deflate(data),
                Lz4 => Lz4Literals(data),
                _ => data
            };

            var entry = table.AsSpan(i * EntrySize, EntrySize);
            Encoding.UTF8.GetBytes(name).CopyTo(entry);

            var offset = HeaderSize + body.Position;
            BitConverter.TryWriteBytes(entry[256..], (uint)offset);
            BitConverter.TryWriteBytes(entry[260..], (ushort)(offset >> 32));
            entry[262] = 0;                         // part
            entry[263] = (byte)(0x40 | compression); // level bits, as real paks carry
            BitConverter.TryWriteBytes(entry[264..], (uint)packed.Length);

            // A stored file may record no uncompressed size at all.
            BitConverter.TryWriteBytes(entry[268..], compression == Stored ? 0u : (uint)data.Length);

            body.Write(packed);
        }

        var block = Lz4Literals(table);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("LSPK"));
        writer.Write(18u);
        writer.Write(HeaderSize + body.Length); // file list offset: after the file data
        writer.Write(block.Length + 8);         // file list size
        writer.Write((byte)0);                  // flags
        writer.Write((byte)0);                  // priority
        writer.Write(new byte[16]);             // md5
        writer.Write((ushort)1);                // parts
        writer.Write(body.ToArray());
        writer.Write(files.Length);
        writer.Write(block.Length);
        writer.Write(block);
    }

    /// <summary>
    /// Writes a pak whose only content is a <c>meta.lsx</c> for the given module.
    /// </summary>
    public static void WriteMod(
        string path,
        string uuid,
        string name,
        params (string Uuid, string Name)[] dependencies) =>
        Write(path, ($"Mods/{name}/meta.lsx", Encoding.UTF8.GetBytes(Meta(uuid, name, dependencies)), Lz4));

    /// <summary>
    /// Writes a pak holding a <c>meta.lsx</c> for the given module plus empty files at the
    /// given paths, so its contents look like a particular kind of mod.
    /// </summary>
    public static void WriteModWith(
        string path,
        string uuid,
        string name,
        string[] contents,
        params (string Uuid, string Name)[] dependencies) =>
        Write(
            path,
            contents
                .Select(c => (c, new byte[] { 1 }, Stored))
                .Prepend(($"Mods/{name}/meta.lsx", Encoding.UTF8.GetBytes(Meta(uuid, name, dependencies)), Lz4))
                .ToArray());

    /// <summary>
    /// Writes a pak the way mod.io publishes one: its <c>meta.lsx</c> names the mod.io mod
    /// (<c>PublishHandle</c>), the version and the upload hash.
    /// </summary>
    public static void WriteModIoMod(
        string path, string uuid, string name, long publishHandle, long version64, string md5 = "") =>
        Write(path, ($"Mods/{name}/meta.lsx", Encoding.UTF8.GetBytes(Meta(uuid, name)
            .Replace("value=\"4321\"", $"value=\"{publishHandle}\"")
            .Replace("""<attribute id="Version64" type="int64" value="36028797018963968"/>""",
                $"""<attribute id="MD5" type="LSString" value="{md5}"/><attribute id="Version64" type="int64" value="{version64}"/>""")), Lz4));

    /// <summary>
    /// Renders a <c>meta.lsx</c> in the shape current game versions write.
    /// </summary>
    public static string Meta(string uuid, string name, params (string Uuid, string Name)[] dependencies)
    {
        var deps = string.Concat(dependencies.Select(d => $"""
                        <node id="ModuleShortDesc">
                          <attribute id="Folder" type="LSString" value="{d.Name}"/>
                          <attribute id="MD5" type="LSString" value=""/>
                          <attribute id="Name" type="LSString" value="{d.Name}"/>
                          <attribute id="PublishHandle" type="uint64" value="0"/>
                          <attribute id="UUID" type="guid" value="{d.Uuid}"/>
                          <attribute id="Version64" type="int64" value="36028797018963968"/>
                        </node>

            """));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
              <version major="4" minor="8" revision="0" build="500"/>
              <region id="Config">
                <node id="root">
                  <children>
                    <node id="Dependencies">
                      <children>
            {deps}          </children>
                    </node>
                    <node id="ModuleInfo">
                      <attribute id="Folder" type="LSString" value="{name}"/>
                      <attribute id="Name" type="LSString" value="{name}"/>
                      <attribute id="PublishHandle" type="uint64" value="4321"/>
                      <attribute id="UUID" type="guid" value="{uuid}"/>
                      <attribute id="Version64" type="int64" value="36028797018963968"/>
                    </node>
                  </children>
                </node>
              </region>
            </save>
            """;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);

        return output.ToArray();
    }

    // One sequence of literals and no match, which is valid LZ4 and needs no compressor.
    private static byte[] Lz4Literals(byte[] data)
    {
        var block = new List<byte>();

        if (data.Length < 15)
        {
            block.Add((byte)(data.Length << 4));
        }
        else
        {
            block.Add(0xF0);
            var remaining = data.Length - 15;
            for (; remaining >= 255; remaining -= 255)
                block.Add(255);
            block.Add((byte)remaining);
        }

        block.AddRange(data);
        return block.ToArray();
    }
}
