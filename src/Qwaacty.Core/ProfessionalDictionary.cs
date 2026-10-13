using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qwaacty.Core;

/// <summary>One bilingual entry of the professional (electronics) glossary.</summary>
public sealed record ProfessionalEntry(
    string Term,
    string Chinese,
    bool IsAbbreviation,
    string? Explanation,
    string Link);

/// <summary>Bilingual electronics/EE glossary used by the professional lookup mode.
/// Loaded from a small JSON file (dicts\eedict.json) that the app downloads from the
/// project's 词库 page. Queries are answered fully offline.</summary>
public sealed class ProfessionalDictionary
{
    private readonly Dictionary<string, ProfessionalEntry> _byTerm;
    private readonly Dictionary<string, ProfessionalEntry> _byChinese;

    private ProfessionalDictionary(
        Dictionary<string, ProfessionalEntry> byTerm,
        Dictionary<string, ProfessionalEntry> byChinese)
    {
        _byTerm = byTerm;
        _byChinese = byChinese;
    }

    public int Count => _byTerm.Count;

    public static ProfessionalDictionary? TryLoad(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = $"未找到专业词库文件：{path}";
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Load(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error = exception.Message;
            return null;
        }
    }

    public static ProfessionalDictionary Load(Stream stream)
    {
        var payload = JsonSerializer.Deserialize<Payload>(stream)
            ?? throw new InvalidDataException("专业词库文件为空。");
        if (payload.Entries is null || payload.Entries.Count == 0)
        {
            throw new InvalidDataException("专业词库文件没有词条。");
        }

        var byTerm = new Dictionary<string, ProfessionalEntry>(payload.Entries.Count, StringComparer.Ordinal);
        var byChinese = new Dictionary<string, ProfessionalEntry>(payload.Entries.Count, StringComparer.Ordinal);
        foreach (var item in payload.Entries)
        {
            if (string.IsNullOrWhiteSpace(item.En) || string.IsNullOrWhiteSpace(item.Zh))
            {
                continue;
            }

            var entry = new ProfessionalEntry(
                item.En.Trim(),
                item.Zh.Trim(),
                item.Abbr,
                string.IsNullOrWhiteSpace(item.Explain) ? null : item.Explain.Trim(),
                string.IsNullOrWhiteSpace(item.Link) ? string.Empty : item.Link.Trim());
            var termKey = NormalizeKey(entry.Term);
            if (termKey.Length > 0)
            {
                byTerm[termKey] = entry;
            }

            // Several English terms can share one Chinese name; keep the first (the build
            // emits entries in alphabetical order, so the result is deterministic).
            byChinese.TryAdd(entry.Chinese, entry);
        }

        if (byTerm.Count == 0)
        {
            throw new InvalidDataException("专业词库文件没有有效词条。");
        }

        return new ProfessionalDictionary(byTerm, byChinese);
    }

    /// <summary>Finds an entry by English term (exact, then simple morphology such as
    /// plurals) or by Chinese name. English matching ignores case and treats '-', '_'
    /// and '/' as word separators, so "A/D Converter", "a_d_converter" and
    /// "a d converter" all resolve to the same entry.</summary>
    public bool TryFind(string query, out ProfessionalEntry entry)
    {
        entry = null!;
        var normalized = query.Trim();
        if (normalized.Length is 0 or > 64)
        {
            return false;
        }

        var termKey = NormalizeKey(normalized);
        if (termKey.Length > 0 && _byTerm.TryGetValue(termKey, out var direct))
        {
            entry = direct;
            return true;
        }

        foreach (var candidate in EnglishMorphology.Candidates(normalized))
        {
            var candidateKey = NormalizeKey(candidate);
            if (candidateKey.Length > 0 && _byTerm.TryGetValue(candidateKey, out var inflected))
            {
                entry = inflected;
                return true;
            }
        }

        if (_byChinese.TryGetValue(normalized, out var chinese))
        {
            entry = chinese;
            return true;
        }

        return false;
    }

    /// <summary>Normalizes a term for the English lookup table: lower-cased, with
    /// whitespace, '-', '_' and '/' all collapsing to a single space, and superscript
    /// digits folded to normal digits ("I²C" == "I2C").</summary>
    private static string NormalizeKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '-' or '_' or '/')
            {
                if (builder.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character switch
            {
                '²' => '2',
                '³' => '3',
                _ => character
            }));
        }

        return builder.ToString();
    }

    private sealed class Payload
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("entries")]
        public List<Item>? Entries { get; init; }
    }

    private sealed class Item
    {
        [JsonPropertyName("en")]
        public string? En { get; init; }

        [JsonPropertyName("zh")]
        public string? Zh { get; init; }

        [JsonPropertyName("abbr")]
        public bool Abbr { get; init; }

        [JsonPropertyName("explain")]
        public string? Explain { get; init; }

        [JsonPropertyName("link")]
        public string? Link { get; init; }
    }
}
