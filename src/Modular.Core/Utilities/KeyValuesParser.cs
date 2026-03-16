namespace Modular.Core.Utilities;

/// <summary>
/// Lightweight parser for Valve's KeyValues text format (.vdf, .acf).
/// Handles nested "key" { ... } blocks, "key" "value" pairs, and // comments.
/// </summary>
public static class KeyValuesParser
{
    /// <summary>
    /// Parses a KeyValues-format string into a tree of nodes.
    /// </summary>
    /// <param name="input">The VDF/ACF text content.</param>
    /// <returns>The root node containing all parsed key-value pairs and children.</returns>
    public static KeyValuesNode Parse(string input)
    {
        var root = new KeyValuesNode("root");
        var tokenizer = new KeyValuesTokenizer(input);
        ParseChildren(tokenizer, root);
        return root;
    }

    /// <summary>
    /// Parses a KeyValues file.
    /// </summary>
    /// <param name="filePath">Path to the .vdf or .acf file.</param>
    /// <returns>The root node.</returns>
    public static KeyValuesNode ParseFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        return Parse(content);
    }

    private static void ParseChildren(KeyValuesTokenizer tokenizer, KeyValuesNode parent)
    {
        while (tokenizer.HasMore())
        {
            var token = tokenizer.NextToken();
            if (token == null || token == "}")
                return;

            // This token is a key
            var key = token;
            var next = tokenizer.NextToken();

            if (next == "{")
            {
                // Nested block
                var child = new KeyValuesNode(key);
                ParseChildren(tokenizer, child);
                parent.Children.Add(child);
            }
            else if (next != null)
            {
                // Key-value pair
                parent.Values[key] = next;
            }
        }
    }

    /// <summary>
    /// Simple tokenizer for KeyValues format.
    /// </summary>
    private class KeyValuesTokenizer
    {
        private readonly string _input;
        private int _pos;

        public KeyValuesTokenizer(string input)
        {
            _input = input;
            _pos = 0;
        }

        public bool HasMore() => _pos < _input.Length;

        public string? NextToken()
        {
            SkipWhitespaceAndComments();

            if (_pos >= _input.Length)
                return null;

            var ch = _input[_pos];

            // Structural tokens
            if (ch == '{' || ch == '}')
            {
                _pos++;
                return ch.ToString();
            }

            // Quoted string
            if (ch == '"')
            {
                _pos++; // skip opening quote
                var start = _pos;
                while (_pos < _input.Length && _input[_pos] != '"')
                {
                    if (_input[_pos] == '\\' && _pos + 1 < _input.Length)
                        _pos++; // skip escaped char
                    _pos++;
                }
                var value = _input[start.._pos];
                if (_pos < _input.Length)
                    _pos++; // skip closing quote
                return value;
            }

            // Unquoted token (until whitespace or structural char)
            {
                var start = _pos;
                while (_pos < _input.Length &&
                       !char.IsWhiteSpace(_input[_pos]) &&
                       _input[_pos] != '{' && _input[_pos] != '}' &&
                       _input[_pos] != '"')
                {
                    _pos++;
                }
                return _input[start.._pos];
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < _input.Length)
            {
                if (char.IsWhiteSpace(_input[_pos]))
                {
                    _pos++;
                    continue;
                }

                // Skip // comments
                if (_pos + 1 < _input.Length && _input[_pos] == '/' && _input[_pos + 1] == '/')
                {
                    while (_pos < _input.Length && _input[_pos] != '\n')
                        _pos++;
                    continue;
                }

                break;
            }
        }
    }
}

/// <summary>
/// Represents a node in a Valve KeyValues tree.
/// </summary>
public class KeyValuesNode
{
    /// <summary>
    /// Node key/name.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Scalar key-value pairs at this level.
    /// </summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Child nodes (nested blocks).
    /// </summary>
    public List<KeyValuesNode> Children { get; } = new();

    public KeyValuesNode(string key)
    {
        Key = key;
    }

    /// <summary>
    /// Gets a value by key, or null if not found.
    /// </summary>
    public string? GetValue(string key) =>
        Values.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Gets the first child node with the specified key.
    /// </summary>
    public KeyValuesNode? GetChild(string key) =>
        Children.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets all child nodes with the specified key.
    /// </summary>
    public IEnumerable<KeyValuesNode> GetChildren(string key) =>
        Children.Where(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
