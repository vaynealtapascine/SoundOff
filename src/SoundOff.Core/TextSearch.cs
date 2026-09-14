namespace SoundOff.Core;

// Plain ordinal, case-insensitive search over paragraph texts. No Unicode normalization: the draft is
// matched exactly as typed, and a match always spans exactly Query.Length UTF-16 code units.
public readonly record struct TextMatch(int Paragraph, int Offset);

public static class TextSearch
{
    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    public static IReadOnlyList<TextMatch> FindAll(IReadOnlyList<string> paragraphs, string query)
    {
        var matches = new List<TextMatch>();
        if (query.Length == 0) return matches;
        for (var paragraph = 0; paragraph < paragraphs.Count; paragraph++)
        {
            var text = paragraphs[paragraph]; var offset = 0;
            while (offset <= text.Length - query.Length)
            {
                var found = text.IndexOf(query, offset, Comparison);
                if (found < 0) break;
                matches.Add(new TextMatch(paragraph, found)); offset = found + query.Length; // non-overlapping
            }
        }
        return matches;
    }

    // First match at or after the position; wraps to the first match. Returns the match's index in the list.
    public static int Next(IReadOnlyList<TextMatch> matches, int paragraph, int offset)
    {
        if (matches.Count == 0) throw new ArgumentException("No matches to step through.", nameof(matches));
        for (var i = 0; i < matches.Count; i++)
            if (matches[i].Paragraph > paragraph || (matches[i].Paragraph == paragraph && matches[i].Offset >= offset)) return i;
        return 0;
    }

    public static bool MatchesAt(string text, int offset, string query) =>
        query.Length > 0 && offset >= 0 && offset + query.Length <= text.Length && string.Equals(text.Substring(offset, query.Length), query, Comparison);

    public static string ReplaceAll(string text, string query, string replacement, out int count)
    {
        count = 0;
        if (query.Length == 0) return text;
        var result = new System.Text.StringBuilder(); var offset = 0;
        while (true)
        {
            var found = offset <= text.Length - query.Length ? text.IndexOf(query, offset, Comparison) : -1;
            if (found < 0) { result.Append(text, offset, text.Length - offset); return result.ToString(); }
            result.Append(text, offset, found - offset).Append(replacement); offset = found + query.Length; count++;
        }
    }
}
