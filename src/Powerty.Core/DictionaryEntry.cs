namespace Powerty.Core;

public enum LookupDirection
{
    EnglishToChinese,
    ChineseToEnglish
}

public sealed record DictionaryEntry(
    string Query,
    LookupDirection Direction,
    IReadOnlyList<string> Definitions,
    string Source,
    string? PartOfSpeech = null,
    string? Phonetic = null,
    bool Structured = false);

public sealed record LookupResponse(
    IReadOnlyList<DictionaryEntry> Entries,
    bool IsOnline,
    string? Error = null)
{
    public bool Found => Entries.Count > 0;
}

public interface ILocalDictionaryProvider
{
    ValueTask<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken);
}

public interface IOnlineLookupProvider
{
    Task<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken);
}

public interface IDictionaryLookup
{
    Task<LookupResponse> FindAsync(
        string query,
        LookupDirection direction,
        bool allowOnline,
        CancellationToken cancellationToken);

    Task<LookupResponse> FindSelectionAsync(
        string query,
        LookupDirection direction,
        bool allowOnline,
        CancellationToken cancellationToken);
}
