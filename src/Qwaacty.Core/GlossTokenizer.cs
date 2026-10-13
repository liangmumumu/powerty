using System.Text;
using System.Text.RegularExpressions;

namespace Qwaacty.Core;

public static partial class GlossTokenizer
{
    private const int MaxTokenLength = 12;

    [GeneratedRegex(@"^(?:[a-zA-Z]+\.\s*)+")]
    private static partial Regex LeadingPartOfSpeechPattern();

    [GeneratedRegex(@"[\(（][^)）]*[\)）]")]
    private static partial Regex ParentheticalPattern();
    private static readonly char[] Separators =
        ['，', ',', '；', ';', '、', '｜', '|', '/', '\\', ' ', '\u3000'];

    private static readonly char[] TokenTrimChars =
        ['，', ',', '；', ';', '、', '。', '.', '：', ':', '·', '…', '—', '－', '-', '"', '\'', '（', '）', '(', ')', '【', '】', '[', ']', ' ', '\u3000'];

    public static IReadOnlyList<string> Split(string translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return [];
        }

        var text = ParentheticalPattern().Replace(translation, " ");
        var tokens = new List<string>(3);
        foreach (var piece in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = LeadingPartOfSpeechPattern().Replace(piece, string.Empty).Trim(TokenTrimChars);
            if (token.Length is 0 or > MaxTokenLength || !ContainsCjk(token) || tokens.Contains(token))
            {
                continue;
            }

            tokens.Add(token);
        }

        return tokens;
    }

    public static string DedupKey(string text)
    {
        StringBuilder? builder = null;
        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                (builder ??= new StringBuilder(text.Length)).Append(character);
            }
        }

        return builder?.ToString() ?? string.Empty;
    }

    public static bool ContainsCjk(string text)
    {
        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u4DBF' or
            >= '\u4E00' and <= '\u9FFF' or
            >= '\uF900' and <= '\uFAFF';
}
