using System.Text;

namespace Modular.Core.Steam;

/// <summary>
/// A node of Steam's binary KeyValues format (<c>shortcuts.vdf</c>). Unlike SteamKit2's KeyValue it
/// keeps each value's type, because Steam reads <c>appid</c>, <c>IsHidden</c> etc. as int32 and
/// ignores them when written as strings.
/// </summary>
public sealed class BinaryVdfNode
{
    private const byte TypeNode = 0, TypeString = 1, TypeInt32 = 2, TypeEnd = 8;

    public BinaryVdfNode(string name) => Name = name;

    public string Name { get; set; }

    /// <summary>Children in file order: each is a <see cref="BinaryVdfNode"/>, a string or an int.</summary>
    public List<KeyValuePair<string, object>> Children { get; } = new();

    public object? this[string key]
    {
        get => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        set
        {
            var i = Children.FindIndex(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                if (i >= 0)
                    Children.RemoveAt(i);
                return;
            }
            if (value is not (BinaryVdfNode or string or int))
                throw new ArgumentException($"Unsupported value type {value.GetType().Name}.", nameof(value));
            if (i >= 0)
                Children[i] = new(Children[i].Key, value);
            else
                Children.Add(new(key, value));
        }
    }

    public string? GetString(string key) => this[key] as string;
    public int? GetInt(string key) => this[key] as int?;
    public BinaryVdfNode? GetNode(string key) => this[key] as BinaryVdfNode;

    public static BinaryVdfNode Read(byte[] data)
    {
        var pos = 0;
        if (data.Length == 0)
            return new BinaryVdfNode("shortcuts");
        if (data[pos++] != TypeNode)
            throw new InvalidDataException("Not a binary VDF file: the root is not a node.");
        var root = new BinaryVdfNode(ReadString(data, ref pos));
        ReadChildren(data, ref pos, root);
        return root;
    }

    private static void ReadChildren(byte[] data, ref int pos, BinaryVdfNode node)
    {
        while (true)
        {
            if (pos >= data.Length)
                throw new InvalidDataException("Binary VDF ends inside a node.");
            var type = data[pos++];
            if (type == TypeEnd)
                return;
            var key = ReadString(data, ref pos);
            switch (type)
            {
                case TypeNode:
                    var child = new BinaryVdfNode(key);
                    ReadChildren(data, ref pos, child);
                    node.Children.Add(new(key, child));
                    break;
                case TypeString:
                    node.Children.Add(new(key, ReadString(data, ref pos)));
                    break;
                case TypeInt32:
                    if (pos + 4 > data.Length)
                        throw new InvalidDataException("Binary VDF ends inside an int32.");
                    node.Children.Add(new(key, BitConverter.ToInt32(data, pos)));
                    pos += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported binary VDF value type {type} at offset {pos - 1}.");
            }
        }
    }

    private static string ReadString(byte[] data, ref int pos)
    {
        var end = Array.IndexOf(data, (byte)0, pos);
        if (end < 0)
            throw new InvalidDataException("Unterminated string in binary VDF.");
        var s = Encoding.UTF8.GetString(data, pos, end - pos);
        pos = end + 1;
        return s;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(TypeNode);
        WriteString(ms, Name);
        WriteChildren(ms, this);
        ms.WriteByte(TypeEnd); // closes the file, after the root's own end marker
        return ms.ToArray();
    }

    private static void WriteChildren(Stream s, BinaryVdfNode node)
    {
        foreach (var (key, value) in node.Children)
        {
            switch (value)
            {
                case BinaryVdfNode child:
                    s.WriteByte(TypeNode);
                    WriteString(s, key);
                    WriteChildren(s, child);
                    break;
                case string str:
                    s.WriteByte(TypeString);
                    WriteString(s, key);
                    WriteString(s, str);
                    break;
                case int i:
                    s.WriteByte(TypeInt32);
                    WriteString(s, key);
                    s.Write(BitConverter.GetBytes(i));
                    break;
            }
        }
        s.WriteByte(TypeEnd);
    }

    private static void WriteString(Stream s, string value)
    {
        s.Write(Encoding.UTF8.GetBytes(value));
        s.WriteByte(0);
    }
}
