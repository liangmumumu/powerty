using System.Text.RegularExpressions;

namespace Powerty.Core;

public sealed record DefinitionGroup(string PartOfSpeech, IReadOnlyList<string> Fragments);

public static partial class DefinitionFormatter
{
    private const int MaxGroups = 3;
    private const int MaxFragmentsPerGroup = 5;
    private const int MaxFragmentLength = 40;

    [GeneratedRegex(
        @"(?<![a-zA-Z0-9])((?:num|interj|abbr|adj|adv|prep|pron|conj|art|aux|phr|pl|vt|vi|n|v)\.(?:\s*&\s*(?:num|adj|adv|prep|pron|conj|n|vt|vi|v)\.)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PartOfSpeechPattern();

    [GeneratedRegex(
        @"[\(（]\s*((?:num|interj|abbr|adj|adv|prep|pron|conj|art|aux|phr|pl|vt|vi|n|v)\.(?:\s*&\s*(?:num|adj|adv|prep|pron|conj|n|vt|vi|v)\.)?)\s*[\)）]",
        RegexOptions.IgnoreCase)]
    private static partial Regex ParentheticalPartOfSpeechPattern();

    private static readonly char[] FragmentSeparators = ['，', ',', '；', ';', '、', '｜', '|', '/', '、'];

    private static readonly char[] FragmentTrimChars =
        ['，', ',', '；', ';', '、', '。', '.', '：', ':', '·', '…', '—', '－', '-', '"', '\'', '（', '）', '(', ')', '【', '】', '[', ']', ' ', '\u3000'];

    public static IReadOnlyList<DefinitionGroup> Format(IReadOnlyList<string> definitions)
    {
        var order = new List<(string PartOfSpeech, List<string> Fragments)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenPerPartOfSpeech = new HashSet<string>(StringComparer.Ordinal);
        var seenAnywhere = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition))
            {
                continue;
            }

            var text = ParentheticalPartOfSpeechPattern().Replace(definition, " $1 ");
            var markers = PartOfSpeechPattern().Matches(text);
            var position = 0;
            var partOfSpeech = string.Empty;

            for (var markerIndex = 0; markerIndex <= markers.Count; markerIndex++)
            {
                var segmentEnd = markerIndex < markers.Count ? markers[markerIndex].Index : text.Length;
                AddFragments(order, index, seenPerPartOfSpeech, seenAnywhere, partOfSpeech, text[position..segmentEnd]);
                if (markerIndex < markers.Count)
                {
                    partOfSpeech = NormalizePartOfSpeech(markers[markerIndex].Value);
                    position = markers[markerIndex].Index + markers[markerIndex].Length;
                }
            }
        }

        return order.Select(group => new DefinitionGroup(group.PartOfSpeech, group.Fragments)).ToArray();
    }

    private static string NormalizePartOfSpeech(string marker)
    {
        var first = marker.Split('&')[0].Trim();
        return first.Length == 0 ? string.Empty : first.ToLowerInvariant();
    }

    private static void AddFragments(
        List<(string PartOfSpeech, List<string> Fragments)> order,
        Dictionary<string, int> index,
        HashSet<string> seenPerPartOfSpeech,
        HashSet<string> seenAnywhere,
        string partOfSpeech,
        string segment)
    {
        foreach (var piece in segment.Split(FragmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fragment = piece.Trim(FragmentTrimChars);
            if (fragment.Length is 0 or > MaxFragmentLength || !fragment.Any(char.IsLetter))
            {
                continue;
            }

            var dedupKey = GlossTokenizer.DedupKey(fragment);
            if (dedupKey.Length == 0)
            {
                dedupKey = fragment.ToLowerInvariant();
            }

            var isNewAnywhere = seenAnywhere.Add(dedupKey);
            if (partOfSpeech.Length == 0)
            {
                if (!isNewAnywhere)
                {
                    continue;
                }
            }
            else if (!seenPerPartOfSpeech.Add(partOfSpeech + "\u0001" + dedupKey))
            {
                continue;
            }

            if (!index.TryGetValue(partOfSpeech, out var groupIndex))
            {
                if (order.Count >= MaxGroups)
                {
                    continue;
                }

                order.Add((partOfSpeech, []));
                index[partOfSpeech] = groupIndex = order.Count - 1;
            }

            var fragments = order[groupIndex].Fragments;
            if (fragments.Count < MaxFragmentsPerGroup)
            {
                fragments.Add(fragment);
            }
        }
    }
}
