using System.Text;

namespace Qwaacty.Core;

public static class GlossTokenizer
{
    private const int MaxTokenLength = 12;

    private static readonly char[] Separators =
        ['，', ',', '；', ';', '、', '｜', '|', '/', '\\', ' ', '\u3000'];

    private static readonly char[] TokenTrimChars =
        ['，', ',', '；', ';', '、', '。', '.', '：', ':', '·', '…', '—', '－', '-', '"', '\'', '（', '）', '(', ')', '【', '】', '[', ']', ' ', '\u3000'];

    /// <summary>Splits a definition into CJK gloss tokens as a span-based single pass.
    /// This runs over every definition of every lexicon word (1.4M+ calls on a 432k
    /// lexicon) and the previous regex + string.Split pipeline dominated startup.</summary>
    public static IReadOnlyList<string> Split(string translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return [];
        }

        var tokens = new List<string>(3);
        var span = translation.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            var remainder = span[index..];
            var separatorIndex = remainder.IndexOfAny(Separators);
            var parenthesisIndex = remainder.IndexOfAny('(', '（');
            if (parenthesisIndex >= 0 && (separatorIndex < 0 || parenthesisIndex < separatorIndex))
            {
                if (parenthesisIndex > 0)
                {
                    AddToken(remainder[..parenthesisIndex], tokens);
                }

                // Parenthesised content is an annotation, not gloss text. An unmatched
                // opening bracket is treated as a separator on its own.
                var closingIndex = remainder[(parenthesisIndex + 1)..].IndexOfAny(')', '）');
                index += closingIndex < 0 ? parenthesisIndex + 1 : parenthesisIndex + closingIndex + 2;
                continue;
            }

            var piece = separatorIndex < 0 ? remainder : remainder[..separatorIndex];
            index = separatorIndex < 0 ? span.Length : index + separatorIndex + 1;
            AddToken(piece, tokens);
        }

        return tokens;
    }

    private static void AddToken(ReadOnlySpan<char> piece, List<string> tokens)
    {
        piece = StripLeadingPartOfSpeech(piece).Trim(TokenTrimChars);
        if (piece.Length is 0 or > MaxTokenLength || !ContainsCjk(piece))
        {
            return;
        }

        var token = piece.ToString();
        if (!tokens.Contains(token))
        {
            tokens.Add(token);
        }
    }

    /// <summary>Strips leading part-of-speech markers like "n. " / "vt. " — equivalent
    /// to the old ^(?:[a-zA-Z]+\.\s*)+ regex, without its per-piece cost.</summary>
    private static ReadOnlySpan<char> StripLeadingPartOfSpeech(ReadOnlySpan<char> piece)
    {
        while (true)
        {
            var index = 0;
            while (index < piece.Length && IsAsciiLetter(piece[index]))
            {
                index++;
            }

            if (index == 0 || index >= piece.Length || piece[index] != '.')
            {
                return piece;
            }

            index++;
            while (index < piece.Length && char.IsWhiteSpace(piece[index]))
            {
                index++;
            }

            piece = piece[index..];
        }
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

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

    public static bool ContainsCjk(string text) => ContainsCjk(text.AsSpan());

    private static bool ContainsCjk(ReadOnlySpan<char> text)
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
