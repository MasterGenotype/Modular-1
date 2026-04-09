namespace Modular.Core.Utilities;

/// <summary>
/// Lightweight fuzzy string matching for mod search result ranking.
/// Returns a score from 0 (no match) to 100 (exact match).
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Computes a fuzzy match score between a query and a target string.
    /// </summary>
    /// <param name="query">The search query typed by the user.</param>
    /// <param name="target">The string to score against (e.g. mod name).</param>
    /// <returns>Score from 0 (no match) to 100 (exact match).</returns>
    public static int Score(string query, string target)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(target))
            return 0;

        var q = query.Trim().ToLowerInvariant();
        var t = target.Trim().ToLowerInvariant();

        if (q.Length == 0) return 0;

        // Exact match
        if (t == q) return 100;

        // Prefix match (target starts with the full query)
        if (t.StartsWith(q, StringComparison.Ordinal)) return 90;

        // Word-boundary prefix: a word in target starts with query
        if (HasWordBoundaryPrefix(q, t)) return 80;

        // Substring match
        if (t.Contains(q, StringComparison.Ordinal)) return 70;

        // Subsequence match — all chars of query appear in order in target
        return SubsequenceScore(q, t);
    }

    /// <summary>
    /// Scores a list of items by fuzzy match against the query and returns them sorted best-first.
    /// Items that score 0 are excluded.
    /// </summary>
    public static IEnumerable<T> Rank<T>(string query, IEnumerable<T> items, Func<T, string> getText)
    {
        if (string.IsNullOrWhiteSpace(query))
            return items;

        return items
            .Select(item => (item, score: Score(query, getText(item)), text: getText(item)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.text.Length)
            .ThenBy(x => x.text, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.item);
    }

    /// <summary>
    /// Returns true if any word in <paramref name="target"/> starts with <paramref name="query"/>.
    /// Words are split on spaces and underscores.
    /// </summary>
    private static bool HasWordBoundaryPrefix(string query, string target)
    {
        var words = target.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.StartsWith(query, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Computes a score (0–60) for how well query characters appear as a subsequence in target.
    /// Returns 0 if not all query characters can be found in order.
    /// </summary>
    private static int SubsequenceScore(string query, string target)
    {
        int qi = 0;
        int firstMatchIndex = -1;
        int lastMatchIndex = -1;
        int consecutive = 0;
        int maxConsecutive = 0;
        int prevMatchIndex = -1;

        for (int ti = 0; ti < target.Length && qi < query.Length; ti++)
        {
            if (target[ti] == query[qi])
            {
                if (firstMatchIndex < 0) firstMatchIndex = ti;
                lastMatchIndex = ti;

                if (prevMatchIndex == ti - 1)
                    consecutive++;
                else
                    consecutive = 1;

                maxConsecutive = Math.Max(maxConsecutive, consecutive);
                prevMatchIndex = ti;
                qi++;
            }
        }

        // Not all query characters were found
        if (qi < query.Length) return 0;

        // Score components (sum ≤ 60):
        // - Coverage: how tightly the matched chars span within target (closer = higher)
        int span = lastMatchIndex - firstMatchIndex + 1;
        double coverageScore = (double)query.Length / span * 25.0;

        // - Consecutive bonus: reward runs of adjacent matches
        double consecutiveScore = (double)maxConsecutive / query.Length * 25.0;

        // - Position bonus: matches near start of target score higher
        double positionScore = (1.0 - (double)firstMatchIndex / target.Length) * 10.0;

        return (int)(coverageScore + consecutiveScore + positionScore);
    }
}
