using System.IO;

namespace Qwaacty.Core;

public sealed class LexiconDictionaryProvider : ILocalDictionaryProvider
{
    private const int MaxReverseSuggestions = 5;
    private const int MaxReverseCandidatesPerToken = 24;

    private readonly Dictionary<string, LexiconWord> _words;
    private readonly Dictionary<string, List<ReverseCandidate>> _reverseIndex;

    private LexiconDictionaryProvider(
        Dictionary<string, LexiconWord> words,
        Dictionary<string, List<ReverseCandidate>> reverseIndex)
    {
        _words = words;
        _reverseIndex = reverseIndex;
    }

    public int WordCount => _words.Count;

    public static LexiconDictionaryProvider Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static LexiconDictionaryProvider Load(Stream stream)
    {
        var words = LexiconFormat.Load(stream);
        var byKey = new Dictionary<string, LexiconWord>(words.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            byKey[word.Key] = word;
        }

        var reverseIndex = new Dictionary<string, List<ReverseCandidate>>(StringComparer.Ordinal);
        // Reused across words: a fresh dictionary + set per word (432k of each) used to
        // dominate the reverse-index build.
        var hitsPerToken = new Dictionary<string, int>(StringComparer.Ordinal);
        var fragmentTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            // Count, per token, how many definition fragments contain it: a word whose
            // definitions are largely about the token is a better translation than a
            // frequent word that merely mentions the token in one secondary sense.
            hitsPerToken.Clear();
            foreach (var definition in word.Definitions)
            {
                fragmentTokens.Clear();
                foreach (var token in GlossTokenizer.Split(definition))
                {
                    if (fragmentTokens.Add(token))
                    {
                        hitsPerToken[token] = hitsPerToken.TryGetValue(token, out var count) ? count + 1 : 1;
                    }
                }
            }

            if (hitsPerToken.Count == 0)
            {
                continue;
            }

            var derived = IsDerivedForm(byKey, word.Key);
            foreach (var (token, hits) in hitsPerToken)
            {
                if (!reverseIndex.TryGetValue(token, out var keys))
                {
                    reverseIndex[token] = keys = new List<ReverseCandidate>(2);
                }

                AddReverseCandidate(keys, word, hits, derived);
            }
        }

        return new LexiconDictionaryProvider(byKey, reverseIndex);
    }

    private const double DominantGlossShare = 0.4;

    private static bool IsDerivedForm(Dictionary<string, LexiconWord> byKey, string key)
    {
        // Manual loop instead of LINQ Any: this runs once per lexicon word (432k) and
        // the delegate/enumerator allocations were measurable at startup.
        foreach (var candidate in EnglishMorphology.Candidates(key))
        {
            if (!string.Equals(candidate, key, StringComparison.Ordinal) && byKey.ContainsKey(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keeps the best <see cref="MaxReverseCandidatesPerToken"/> keys per gloss token.
    /// With a large lexicon the old "first 24 in file order" rule let alphabetically
    /// early long phrases crowd out the shorter, more useful words. Candidates rank
    /// as: single word before phrase, then words whose definitions are largely about
    /// this gloss (>= <see cref="DominantGlossShare"/>), then corpus frequency with a
    /// penalty for inflected forms, then length — so "覆盖" yields cover first instead
    /// of short obscure words (cap/cake/robe) or inflected forms (overwhelmed).
    /// The rank is computed once per candidate and stored: the eviction scans used to
    /// re-divide and dictionary-look-up every entry on every comparison.
    /// </summary>
    private static void AddReverseCandidate(List<ReverseCandidate> keys, LexiconWord word, int hits, bool derived)
    {
        foreach (var existing in keys)
        {
            if (string.Equals(existing.Key, word.Key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var rank = ComputeRank(word, hits, derived);
        if (keys.Count < MaxReverseCandidatesPerToken)
        {
            keys.Add(new ReverseCandidate(word.Key, rank));
            return;
        }

        var worstIndex = 0;
        var worstRank = keys[0].Rank;
        for (var index = 1; index < keys.Count; index++)
        {
            if (keys[index].Rank.CompareTo(worstRank) > 0)
            {
                worstRank = keys[index].Rank;
                worstIndex = index;
            }
        }

        if (rank.CompareTo(worstRank) < 0)
        {
            keys[worstIndex] = new ReverseCandidate(word.Key, rank);
        }
    }

    private static (int Phrase, int WeakShare, int FrequencyUnknown, long EffectiveFrequency, int Length) ComputeRank(
        LexiconWord word,
        int hits,
        bool derived)
    {
        var share = (double)hits / Math.Max(1, word.Definitions.Count);
        return (word.Name.Contains(' ') ? 1 : 0,
            share >= DominantGlossShare ? 0 : 1,
            word.Frequency > 0 ? 0 : 1,
            derived ? (long)word.Frequency * 4 : word.Frequency,
            word.Name.Length);
    }

    private readonly record struct ReverseCandidate(
        string Key,
        (int Phrase, int WeakShare, int FrequencyUnknown, long EffectiveFrequency, int Length) Rank);

    public static bool TryLoad(string path, out LexiconDictionaryProvider? provider, out string? error)
    {
        provider = null;
        error = null;
        if (!File.Exists(path))
        {
            error = $"未找到词库文件：{path}";
            return false;
        }

        try
        {
            provider = Load(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    public ValueTask<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = query.Trim();
        if (normalized.Length == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>([]);
        }

        if (direction == LookupDirection.EnglishToChinese)
        {
            if (_words.TryGetValue(normalized, out var word))
            {
                return ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>([ToEntry(word, direction)]);
            }

            return ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>([]);
        }

        if (!_reverseIndex.TryGetValue(normalized, out var keys) || keys.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>([]);
        }

        var entries = keys
            .Select(candidate => (Word: _words[candidate.Key], Candidate: candidate))
            .OrderBy(item => item.Candidate.Rank)
            .ThenBy(item => item.Word.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxReverseSuggestions)
            .Select(item => ToEntry(item.Word, direction))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>(entries);
    }

    private static DictionaryEntry ToEntry(LexiconWord word, LookupDirection direction)
    {
        var phonetics = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(word.UsPhonetic))
        {
            phonetics.Add($"美 [{word.UsPhonetic}]");
        }

        if (!string.IsNullOrWhiteSpace(word.UkPhonetic))
        {
            phonetics.Add($"英 [{word.UkPhonetic}]");
        }

        return new DictionaryEntry(
            word.Name,
            direction,
            word.Definitions,
            "本地词库",
            Phonetic: phonetics.Count > 0 ? string.Join("  ", phonetics) : null,
            Structured: true);
    }
}
