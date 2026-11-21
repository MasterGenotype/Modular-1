using System.Text;

namespace Modular.Core.Steam;

/// <summary>
/// Edits one block of a Steam text VDF file in place and leaves every other byte alone.
/// Re-serialising with SteamKit2 is not safe for <c>config.vdf</c>: it rewrites the raw newlines
/// inside values such as <c>SDL_GamepadBind</c> (controller mappings) as <c>\n</c>.
/// </summary>
public static class TextVdfPatcher
{
    private sealed record Token(bool IsString, string Text, int Start, int End);

    /// <summary>
    /// Sets the child block <paramref name="key"/> under <paramref name="path"/> (from the root key,
    /// e.g. InstallConfigStore/Software/Valve/Steam/CompatToolMapping) to the given string values,
    /// replacing an existing block or inserting a new one before the parent's closing brace.
    /// With null values the block is removed. Missing parents are an error.
    /// </summary>
    public static string SetBlock(string text, IReadOnlyList<string> path, string key, IReadOnlyList<(string Key, string Value)>? values)
    {
        var tokens = Tokenize(text);
        var (parentOpen, parentClose) = FindBlock(tokens, path)
            ?? throw new InvalidDataException($"Section {string.Join('/', path)} not found.");

        var existing = FindChild(tokens, parentOpen, parentClose, key);
        var indent = IndentOf(text, tokens[parentOpen].Start) + "\t";

        if (existing is { } e)
        {
            // Remove from the start of the key's line through the closing brace's line end.
            var lineStart = text.LastIndexOf('\n', tokens[e.KeyIndex].Start) + 1;
            var lineEnd = text.IndexOf('\n', tokens[e.Close].End);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd + 1;
            var replacement = values is null ? "" : Render(indent, key, values);
            return text[..lineStart] + replacement + text[lineEnd..];
        }
        if (values is null)
            return text;

        var closeLineStart = text.LastIndexOf('\n', tokens[parentClose].Start) + 1;
        return text[..closeLineStart] + Render(indent, key, values) + text[closeLineStart..];
    }

    private static string Render(string indent, string key, IReadOnlyList<(string Key, string Value)> values)
    {
        var sb = new StringBuilder();
        sb.Append(indent).Append('"').Append(key).Append("\"\n");
        sb.Append(indent).Append("{\n");
        foreach (var (k, v) in values)
            sb.Append(indent).Append('\t').Append('"').Append(k).Append("\"\t\t\"").Append(v.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
        sb.Append(indent).Append("}\n");
        return sb.ToString();
    }

    private static string IndentOf(string text, int pos)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        var i = lineStart;
        while (i < text.Length && (text[i] == '\t' || text[i] == ' '))
            i++;
        return text[lineStart..i];
    }

    /// <summary>Indexes of the block's '{' and '}' tokens for a key path from the root.</summary>
    private static (int Open, int Close)? FindBlock(List<Token> tokens, IReadOnlyList<string> path)
    {
        // Root: first string token followed by '{'.
        if (tokens.Count < 2 || !tokens[0].IsString || !string.Equals(tokens[0].Text, path[0], StringComparison.OrdinalIgnoreCase))
            return null;
        var open = 1;
        var close = MatchingClose(tokens, open);
        for (var depth = 1; depth < path.Count; depth++)
        {
            var child = FindChild(tokens, open, close, path[depth]);
            if (child is null)
                return null;
            (open, close) = (child.Value.Open, child.Value.Close);
        }
        return (open, close);
    }

    private static (int KeyIndex, int Open, int Close)? FindChild(List<Token> tokens, int open, int close, string key)
    {
        var i = open + 1;
        while (i < close)
        {
            var k = tokens[i];
            if (!k.IsString)
                throw new InvalidDataException("Malformed VDF: expected a key.");
            var next = tokens[i + 1];
            if (next.IsString)
            {
                i += 2; // key/value pair
                continue;
            }
            var childClose = MatchingClose(tokens, i + 1);
            if (string.Equals(k.Text, key, StringComparison.OrdinalIgnoreCase))
                return (i, i + 1, childClose);
            i = childClose + 1;
        }
        return null;
    }

    private static int MatchingClose(List<Token> tokens, int open)
    {
        if (tokens[open].IsString || tokens[open].Text != "{")
            throw new InvalidDataException("Malformed VDF: expected '{'.");
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].IsString)
                continue;
            depth += tokens[i].Text == "{" ? 1 : -1;
            if (depth == 0)
                return i;
        }
        throw new InvalidDataException("Malformed VDF: unbalanced braces.");
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c is '{' or '}')
            {
                tokens.Add(new Token(false, c.ToString(), i, i + 1));
                i++;
            }
            else if (c == '"')
            {
                var start = i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        i++;
                    sb.Append(text[i++]);
                }
                if (i >= text.Length)
                    throw new InvalidDataException("Malformed VDF: unterminated string.");
                i++;
                tokens.Add(new Token(true, sb.ToString(), start, i));
            }
            else
            {
                // Unquoted token (rare in Steam's files): up to whitespace or brace.
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                    i++;
                tokens.Add(new Token(true, text[start..i], start, i));
            }
        }
        return tokens;
    }
}
