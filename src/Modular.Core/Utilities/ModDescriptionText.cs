using System.Net;
using System.Text.RegularExpressions;

namespace Modular.Core.Utilities;

/// <summary>
/// Turns a mod page's description into text a terminal can show.
/// </summary>
public static partial class ModDescriptionText
{
    /// <summary>
    /// Converts BBCode and HTML markup to plain text.
    /// </summary>
    /// <param name="description">The description as the hub sends it; NexusMods mixes BBCode with <c>&lt;br /&gt;</c>.</param>
    /// <returns>The text, with list items as bullets and links as <c>text (url)</c>.</returns>
    /// <remarks>
    /// Images are dropped. Only known BBCode tags are removed, so bracketed text such as
    /// <c>[WIP]</c> is kept. Spacing inside lines is kept for <c>[code]</c> tables.
    /// </remarks>
    public static string ToPlainText(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        var text = description.Replace("\r\n", "\n").Replace('\r', '\n');

        // NexusMods sends each line break twice, as a newline followed by <br />.
        text = HtmlLineBreak().Replace(text, "\n");

        text = BbImage().Replace(text, string.Empty);
        text = BbYouTube().Replace(text, m => $"https://www.youtube.com/watch?v={m.Groups["id"].Value.Trim()}");
        text = BbLink().Replace(text, m => Link(m.Groups["text"].Value, m.Groups["url"].Value));
        text = BbBareLink().Replace(text, m => m.Groups["url"].Value.Trim());
        text = BbListItem().Replace(text, "\n• ");
        text = BbRule().Replace(text, "\n---\n");
        text = BbBlockTag().Replace(text, "\n");
        text = BbInlineTag().Replace(text, string.Empty);

        text = HtmlListItem().Replace(text, "\n• ");
        text = HtmlLink().Replace(text, m => Link(m.Groups["text"].Value, m.Groups["url"].Value));
        text = HtmlBlockTag().Replace(text, "\n");
        text = HtmlTag().Replace(text, string.Empty);

        text = WebUtility.HtmlDecode(text);

        var lines = text.Split('\n').Select(l => l.TrimEnd());
        text = string.Join('\n', lines);
        text = ExtraBlankLines().Replace(text, "\n\n");
        return text.Trim('\n', ' ');
    }

    private static string Link(string text, string url)
    {
        text = text.Trim();
        url = url.Trim().Trim('"', '\'');
        if (text.Length == 0 || string.Equals(text, url, StringComparison.OrdinalIgnoreCase))
            return url;
        return url.Length == 0 ? text : $"{text} ({url})";
    }

    [GeneratedRegex(@"\[img(?:=[^\]]*)?\].*?\[/img\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BbImage();

    [GeneratedRegex(@"\[youtube\](?<id>.*?)\[/youtube\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BbYouTube();

    [GeneratedRegex(@"\[url=(?<url>[^\]]*)\](?<text>.*?)\[/url\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BbLink();

    [GeneratedRegex(@"\[url\](?<url>.*?)\[/url\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BbBareLink();

    [GeneratedRegex(@"\s*\[\*\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbListItem();

    [GeneratedRegex(@"\[(?:line|hr)\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbRule();

    [GeneratedRegex(@"\[/?(?:center|left|right|justify|quote|code|spoiler|heading|list|indent|table|tr|th|td)(?:=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbBlockTag();

    [GeneratedRegex(@"\[/\*\]|\[/?(?:b|i|u|s|size|color|colour|font|sub|sup|email)(?:=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbInlineTag();

    [GeneratedRegex(@"\n?<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlLineBreak();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlListItem();

    [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""'](?<url>[^""']*)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlLink();

    [GeneratedRegex(@"</?(?:p|div|ul|ol|h[1-6]|blockquote|pre|table|tr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlockTag();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();
}
