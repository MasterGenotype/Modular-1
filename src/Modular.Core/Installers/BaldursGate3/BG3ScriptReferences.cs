using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// Reads which other mods a pak's Script Extender scripts use while the game is loading,
/// a dependency many mods never declare in their <c>meta.lsx</c>.
/// </summary>
/// <remarks>
/// <para>
/// Script Extender gives each mod's scripts a table named in its
/// <c>ScriptExtender/Config.json</c> (<c>ModTable</c>), reached by other mods as
/// <c>Mods.&lt;ModTable&gt;</c>. Scripts run in load order, so a script that uses another mod's
/// table at the top of a file, outside any function, fails if that mod loads later.
/// Community Library is the common case: Oath Framework and Tag Framework call
/// <c>Mods.CommunityLibrary.Import</c> as they load, and declare nothing.
/// </para>
/// <para>
/// Only top-level statements count, read as lines that start in the first column. A use
/// inside a function runs later, once every mod has loaded, and a line that opens with
/// <c>if</c> is a mod checking whether another is present. Neither says anything about
/// order, and counting them would invent loops between mods that support each other.
/// </para>
/// </remarks>
public static partial class BG3ScriptReferences
{
    /// <summary>
    /// Adds a pak's script table and load-time script references to its metadata.
    /// </summary>
    /// <param name="pakPath">Absolute path to the <c>.pak</c>.</param>
    /// <param name="meta">The metadata read from the pak's <c>meta.lsx</c>.</param>
    /// <returns>The metadata, with the script fields filled in when the pak has scripts.</returns>
    public static BG3PakMeta Apply(string pakPath, BG3PakMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var files = LspkReader.ReadFiles(pakPath, IsScriptFile);
        if (files.Count == 0)
            return meta;

        string? table = null;
        var sources = new List<string>();

        foreach (var (name, data) in files)
        {
            var text = Encoding.UTF8.GetString(data);

            if (name.EndsWith("/Config.json", StringComparison.OrdinalIgnoreCase))
                table ??= ReadModTable(text);
            else
                sources.Add(text);
        }

        var references = sources
            .SelectMany(LoadTimeReferences)
            .Where(r => !string.Equals(r, table, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return meta with { ScriptModTable = table, ScriptReferences = references };
    }

    /// <summary>
    /// Names the script tables a Lua file uses in its top-level statements.
    /// </summary>
    /// <param name="lua">The file's source.</param>
    /// <returns>Each <c>Mods.&lt;name&gt;</c> or <c>Mods["name"]</c> used outside functions and checks.</returns>
    public static IEnumerable<string> LoadTimeReferences(string lua)
    {
        ArgumentNullException.ThrowIfNull(lua);

        foreach (var line in StripComments(lua).Split('\n'))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || GuardLine().IsMatch(line))
                continue;

            foreach (Match match in ModsReference().Matches(line))
            {
                yield return match.Groups["dot"].Success
                    ? match.Groups["dot"].Value
                    : match.Groups["key"].Value;
            }
        }
    }

    // ScriptExtender/Config.json and the Lua beneath ScriptExtender/Lua, of the mod's own
    // folder under Mods/.
    private static bool IsScriptFile(string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 4 || !string.Equals(parts[0], "Mods", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[2], "ScriptExtender", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Length == 4
            ? string.Equals(parts[3], "Config.json", StringComparison.OrdinalIgnoreCase)
            : path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadModTable(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.TrimStart('﻿'), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("ModTable", out var value) &&
                   value.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Removes block and line comments, keeping line breaks so lines stay where they were.
    private static string StripComments(string lua)
    {
        var withoutBlocks = BlockComment().Replace(
            lua, m => new string('\n', m.Value.Count(c => c == '\n')));

        return LineComment().Replace(withoutBlocks.Replace("\r", string.Empty), string.Empty);
    }

    [GeneratedRegex(@"--\[(=*)\[.*?\]\1\]", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^(if|elseif)\b")]
    private static partial Regex GuardLine();

    [GeneratedRegex(@"\bMods\s*(?:\.\s*(?<dot>[A-Za-z_]\w*)|\[\s*[""'](?<key>[^""']+)[""']\s*\])")]
    private static partial Regex ModsReference();
}
