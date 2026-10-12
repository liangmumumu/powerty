using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Powerty.Core;

const int MaxDefinitionsPerWord = 6;
const int MaxWordLength = 48;
const int MaxEcdictDefinitionLength = 500;

var source = GetOption(args, "--source");
var output = GetOption(args, "--output");
var ecdictPath = GetOption(args, "--ecdict");
var curatedPath = GetOption(args, "--curated");
var ecdictMaxBnc = ParseLimit(GetOption(args, "--ecdict-max-bnc"), 60000);
var ecdictMaxFrq = ParseLimit(GetOption(args, "--ecdict-max-frq"), 60000);
var ecdictPhraseWords = ParseLimit(GetOption(args, "--ecdict-phrase-words"), 0);
var ecdictIncludeAll = HasFlag(args, "--ecdict-include-all");
if (source is null || output is null)
{
    Console.Error.WriteLine("Usage: Powerty.LexiconBuilder --source <qwerty-learner-master directory> --output <lexicon.ttlx> [--ecdict <ecdict.csv>]");
    return 1;
}

var indexPath = Path.Combine(source, "src", "resources", "dictionary.ts");
if (!File.Exists(indexPath))
{
    Console.Error.WriteLine($"Dictionary index not found: {indexPath}");
    return 1;
}

var stopwatch = Stopwatch.StartNew();
var whitespacePattern = new Regex(@"\s+", RegexOptions.Compiled);
var indexText = File.ReadAllText(indexPath);
var blockPattern = new Regex(
    @"id:\s*'([^']+)',\s*name:\s*'([^']+)',\s*description:\s*'([^']*)',\s*category:\s*'([^']*)',[\s\S]*?url:\s*'([^']+)',\s*length:\s*(\d+),\s*language:\s*'([^']+)'");
var dictionaryFiles = blockPattern.Matches(indexText)
    .Where(match => match.Groups[7].Value == "en")
    .Select(match => match.Groups[5].Value)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

var dictionaryDirectory = Path.Combine(source, "public", "dicts");
var words = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
var entriesRead = 0;
var skippedLongNames = 0;
var missingFiles = 0;
var unreadableFiles = 0;

foreach (var dictionaryFile in dictionaryFiles)
{
    var path = Path.Combine(dictionaryDirectory, Path.GetFileName(dictionaryFile));
    if (!File.Exists(path))
    {
        missingFiles++;
        continue;
    }

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            unreadableFiles++;
            continue;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = element.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            name = whitespacePattern.Replace(name.Trim(), " ");
            if (name.Length > MaxWordLength)
            {
                skippedLongNames++;
                continue;
            }

            entriesRead++;
            var key = name.ToLowerInvariant();
            if (!words.TryGetValue(key, out var accumulator))
            {
                words[key] = accumulator = new Accumulator(key, name);
            }

            if (element.TryGetProperty("trans", out var transValue))
            {
                if (transValue.ValueKind == JsonValueKind.String)
                {
                    AddDefinition(accumulator, transValue.GetString());
                }
                else if (transValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var definitionElement in transValue.EnumerateArray())
                    {
                        if (definitionElement.ValueKind == JsonValueKind.String)
                        {
                            AddDefinition(accumulator, definitionElement.GetString());
                        }
                    }
                }
            }

            if (accumulator.UsPhonetic.Length == 0 &&
                element.TryGetProperty("usphone", out var usValue) &&
                usValue.ValueKind == JsonValueKind.String &&
                usValue.GetString() is { Length: > 0 } us)
            {
                accumulator.UsPhonetic = us.Trim();
            }

            if (accumulator.UkPhonetic.Length == 0 &&
                element.TryGetProperty("ukphone", out var ukValue) &&
                ukValue.ValueKind == JsonValueKind.String &&
                ukValue.GetString() is { Length: > 0 } uk)
            {
                accumulator.UkPhonetic = uk.Trim();
            }
        }
    }
    catch (JsonException)
    {
        unreadableFiles++;
    }
}

var ecdictAdded = 0;
var ecdictPhoneticMerged = 0;
var ecdictFiltered = 0;
var ecdictNoTranslation = 0;
var ecdictInvalidWord = 0;
if (ecdictPath is not null && File.Exists(ecdictPath))
{
    var firstRow = true;
    foreach (var fields in EcdictCsv.ReadRows(ecdictPath))
    {
        if (firstRow)
        {
            firstRow = false;
            if (fields.Length > 0 && string.Equals(fields[0], "word", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        if (fields.Length < 4)
        {
            continue;
        }

        var word = fields[0].Trim();
        if (word.Length is 0 || word.Length > MaxWordLength || !IsSupportedWord(word))
        {
            ecdictInvalidWord++;
            continue;
        }

        var key = word.ToLowerInvariant();
        var phonetic = fields[1].Trim();
        // Column layout: 0 word, 1 phonetic, 2 definition, 3 translation, 4 pos,
        // 5 collins, 6 oxford, 7 tag, 8 bnc, 9 frq.
        var rowFrequency = BestFrequency(ParseIntField(fields, 8), ParseIntField(fields, 9));
        if (words.TryGetValue(key, out var existing))
        {
            if (existing.UsPhonetic.Length == 0 && phonetic.Length > 0)
            {
                existing.UsPhonetic = phonetic;
                ecdictPhoneticMerged++;
            }

            existing.MergeFrequency(rowFrequency);
            continue;
        }

        var row = new EcdictRow(
            word,
            phonetic,
            fields[3],
            ParseIntField(fields, 5),
            ParseIntField(fields, 6),
            fields.Length > 7 ? fields[7].Trim() : string.Empty,
            ParseIntField(fields, 8),
            ParseIntField(fields, 9));
        if (!ShouldIncludeEcdict(row, ecdictMaxBnc, ecdictMaxFrq, ecdictPhraseWords, ecdictIncludeAll))
        {
            ecdictFiltered++;
            continue;
        }

        var accumulator = new Accumulator(key, word);
        foreach (var definition in SplitTranslations(row.Translation))
        {
            AddDefinition(accumulator, definition);
        }

        if (accumulator.Definitions.Count == 0 || !accumulator.Definitions.Any(HasCjk))
        {
            ecdictNoTranslation++;
            continue;
        }

        if (phonetic.Length > 0)
        {
            accumulator.UsPhonetic = phonetic;
        }

        accumulator.MergeFrequency(rowFrequency);
        words[key] = accumulator;
        ecdictAdded++;
    }
}

var curatedAdded = 0;
var curatedMerged = 0;
if (curatedPath is not null && File.Exists(curatedPath))
{
    foreach (var rawLine in File.ReadLines(curatedPath, Encoding.UTF8))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var tab = line.IndexOf('\t');
        if (tab <= 0)
        {
            continue;
        }

        var word = line[..tab].Trim();
        var translation = line[(tab + 1)..].Trim();
        if (word.Length is 0 or > MaxWordLength || translation.Length == 0 || !IsSupportedWord(word))
        {
            continue;
        }

        var key = word.ToLowerInvariant();
        if (words.TryGetValue(key, out var existing))
        {
            foreach (var definition in SplitTranslations(translation))
            {
                AddDefinition(existing, definition, force: true);
            }

            curatedMerged++;
            continue;
        }

        var accumulator = new Accumulator(key, word);
        foreach (var definition in SplitTranslations(translation))
        {
            AddDefinition(accumulator, definition);
        }

        if (accumulator.Definitions.Count > 0)
        {
            words[key] = accumulator;
            curatedAdded++;
        }
    }
}

var lexicon = words.Values
    .OrderBy(word => word.Key, StringComparer.Ordinal)
    .Select(word => new LexiconWord(
        word.Key,
        word.Name,
        word.Definitions,
        word.UsPhonetic,
        word.UkPhonetic,
        word.Frequency))
    .ToArray();

var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
if (!string.IsNullOrEmpty(outputDirectory))
{
    Directory.CreateDirectory(outputDirectory);
}

using (var stream = File.Create(output))
{
    LexiconFormat.Save(stream, lexicon);
}

stopwatch.Stop();
var fileSize = new FileInfo(output).Length;
Console.WriteLine($"Dictionary files: {dictionaryFiles.Length} (missing: {missingFiles}, unreadable: {unreadableFiles})");
Console.WriteLine($"Entries read: {entriesRead} (skipped for length: {skippedLongNames})");
if (ecdictPath is not null && File.Exists(ecdictPath))
{
    Console.WriteLine($"ECDICT: added {ecdictAdded}, phonetic merged {ecdictPhoneticMerged}, filtered {ecdictFiltered}, no translation {ecdictNoTranslation}, invalid word {ecdictInvalidWord}");
}

if (curatedPath is not null && File.Exists(curatedPath))
{
    Console.WriteLine($"Curated: added {curatedAdded}, merged {curatedMerged}");
}
Console.WriteLine($"Unique words: {lexicon.Length}");
Console.WriteLine($"Definitions written: {lexicon.Sum(word => word.Definitions.Count)}");
Console.WriteLine($"Output: {output} ({fileSize / 1048576.0:F2} MB, {stopwatch.ElapsedMilliseconds} ms)");
return 0;

void AddDefinition(Accumulator accumulator, string? definition, bool force = false)
{
    if ((!force && accumulator.Definitions.Count >= MaxDefinitionsPerWord) || string.IsNullOrWhiteSpace(definition))
    {
        return;
    }

    var trimmed = definition.Trim();
    var dedupKey = GlossTokenizer.DedupKey(trimmed);
    if (dedupKey.Length > 0)
    {
        if (!accumulator.SeenKeys.Add(dedupKey))
        {
            return;
        }
    }
    else if (!accumulator.SeenRawDefinitions.Add(trimmed))
    {
        return;
    }

    accumulator.Definitions.Add(trimmed);
}

IEnumerable<string> SplitTranslations(string translation)
{
    foreach (var rawLine in translation.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
    {
        var line = whitespacePattern.Replace(rawLine.Trim(), " ");
        if (line.Length == 0)
        {
            continue;
        }

        yield return line.Length > MaxEcdictDefinitionLength ? line[..MaxEcdictDefinitionLength] + "…" : line;
    }
}

static bool ShouldIncludeEcdict(EcdictRow row, int maxBnc, int maxFrq, int phraseWords, bool includeAll)
{
    if (includeAll)
    {
        return true;
    }

    if (row.Collins > 0 || row.Oxford == 1 || row.Tag.Length > 0 ||
        (row.Bnc > 0 && row.Bnc <= maxBnc) || (row.Frq > 0 && row.Frq <= maxFrq))
    {
        return true;
    }

    if (phraseWords > 0 && row.Word.Contains(' '))
    {
        var wordCount = row.Word.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount <= phraseWords)
        {
            return true;
        }
    }

    return false;
}

static bool HasCjk(string text)
{
    foreach (var character in text)
    {
        if (character is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF')
        {
            return true;
        }
    }

    return false;
}

static bool IsSupportedWord(string word)
{
    foreach (var character in word)
    {
        if (!char.IsAsciiLetterOrDigit(character) && character is not (' ' or '-' or '\'' or '\u2019' or '.'))
        {
            return false;
        }
    }

    return true;
}

static int ParseIntField(string[] fields, int index) =>
    index < fields.Length && int.TryParse(fields[index].Trim(), out var value) ? value : 0;

static int BestFrequency(int bnc, int frq)
{
    if (bnc > 0 && frq > 0)
    {
        return Math.Min(bnc, frq);
    }

    return bnc > 0 ? bnc : frq > 0 ? frq : 0;
}

static int ParseLimit(string? value, int fallback) =>
    int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;

static bool HasFlag(string[] arguments, string name) =>
    arguments.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

static string? GetOption(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

internal sealed class Accumulator(string key, string name)
{
    public string Key { get; } = key;

    public string Name { get; } = name;

    public List<string> Definitions { get; } = [];

    public HashSet<string> SeenKeys { get; } = new(StringComparer.Ordinal);

    public HashSet<string> SeenRawDefinitions { get; } = new(StringComparer.Ordinal);

    public string UsPhonetic { get; set; } = string.Empty;

    public string UkPhonetic { get; set; } = string.Empty;

    public int Frequency { get; private set; }

    /// <summary>Keeps the smallest known corpus rank; 0 means "unknown".</summary>
    public void MergeFrequency(int frequency)
    {
        if (frequency <= 0)
        {
            return;
        }

        if (Frequency == 0 || frequency < Frequency)
        {
            Frequency = frequency;
        }
    }
}
