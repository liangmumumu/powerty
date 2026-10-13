namespace Qwaacty.Core;

public sealed class LocalDictionaryProvider : ILocalDictionaryProvider
{
    private static readonly Dictionary<(string Query, LookupDirection Direction), DictionaryEntry> SampleEntries =
        new(new LookupKeyComparer())
        {
            [("apple", LookupDirection.EnglishToChinese)] =
                new("apple", LookupDirection.EnglishToChinese, ["苹果"], "内置演示词条", "名词"),
            [("book", LookupDirection.EnglishToChinese)] =
                new("book", LookupDirection.EnglishToChinese, ["书；书籍", "预订"], "内置演示词条", "名词；动词"),
            [("good morning", LookupDirection.EnglishToChinese)] =
                new("good morning", LookupDirection.EnglishToChinese, ["早上好"], "内置演示词条", "短语"),
            [("hello", LookupDirection.EnglishToChinese)] =
                new("hello", LookupDirection.EnglishToChinese, ["你好；喂"], "内置演示词条", "感叹词"),
            [("learn", LookupDirection.EnglishToChinese)] =
                new("learn", LookupDirection.EnglishToChinese, ["学习；学会"], "内置演示词条", "动词"),
            [("谢谢", LookupDirection.ChineseToEnglish)] =
                new("谢谢", LookupDirection.ChineseToEnglish, ["thank you", "thanks"], "内置演示词条"),
            [("苹果", LookupDirection.ChineseToEnglish)] =
                new("苹果", LookupDirection.ChineseToEnglish, ["apple"], "内置演示词条"),
            [("早上好", LookupDirection.ChineseToEnglish)] =
                new("早上好", LookupDirection.ChineseToEnglish, ["good morning"], "内置演示词条")
        };

    public ValueTask<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = query.Trim();

        if (SampleEntries.TryGetValue((normalized, direction), out var entry))
        {
            return ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>([entry]);
        }

        return ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>([]);
    }

    private sealed class LookupKeyComparer : IEqualityComparer<(string Query, LookupDirection Direction)>
    {
        public bool Equals(
            (string Query, LookupDirection Direction) x,
            (string Query, LookupDirection Direction) y) =>
            x.Direction == y.Direction &&
            string.Equals(x.Query, y.Query, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Query, LookupDirection Direction) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Query), value.Direction);
    }
}
