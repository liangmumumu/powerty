namespace Qwaacty.Core;

/// <summary>
/// Suffixes of a Chinese query, longest first. While typing Chinese there are no
/// word boundaries, so the captured window around the caret often carries extra
/// leading characters (e.g. "上午开会"). Trying the shorter suffixes against the
/// local dictionary recovers the actual word ("开会") without any network call.
/// </summary>
public static class ChineseQueryLadder
{
    public const int MinimumSuffixLength = 2;

    public static IReadOnlyList<string> Suffixes(string query)
    {
        if (query.Length <= MinimumSuffixLength || query.Length > DictionaryLookup.MaxTypedQueryLength)
        {
            return [];
        }

        var results = new List<string>(query.Length - MinimumSuffixLength);
        for (var start = 1; start <= query.Length - MinimumSuffixLength; start++)
        {
            results.Add(query[start..]);
        }

        return results;
    }
}
