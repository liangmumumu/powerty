namespace Qwaacty.Core;

public sealed record QueryCandidate(string Text, LookupDirection Direction);

public static class InputContextParser
{
    private const int MaxPhraseWords = 3;
    private const int MaxChineseCharacters = 4;

    public static IReadOnlyList<QueryCandidate> Parse(string context, int caretIndex)
    {
        if (string.IsNullOrEmpty(context) || caretIndex < 0 || caretIndex > context.Length)
        {
            return [];
        }

        var boundedCaret = Math.Min(caretIndex, context.Length);
        var candidates = new List<QueryCandidate>(2);
        if (boundedCaret > 0 && IsChineseCharacter(context[boundedCaret - 1]))
        {
            AddChineseCandidate(context, boundedCaret, candidates);
            AddEnglishCandidates(context, boundedCaret, candidates);
        }
        else
        {
            AddEnglishCandidates(context, boundedCaret, candidates);
            AddChineseCandidate(context, boundedCaret, candidates);
        }

        return candidates;
    }

    private static void AddEnglishCandidates(string text, int caret, ICollection<QueryCandidate> candidates)
    {
        var wordEnd = caret;
        while (wordEnd > 0 &&
               (char.IsWhiteSpace(text[wordEnd - 1]) || IsPhraseBoundary(text[wordEnd - 1])))
        {
            wordEnd--;
        }

        var wordStart = wordEnd;
        while (wordStart > 0 && IsEnglishWordCharacter(text[wordStart - 1]))
        {
            wordStart--;
        }

        while (wordEnd < text.Length && IsEnglishWordCharacter(text[wordEnd]))
        {
            wordEnd++;
        }

        if (wordStart == wordEnd || !ContainsLetter(text, wordStart, wordEnd))
        {
            return;
        }

        var word = text[wordStart..wordEnd].Trim('\'', '-');
        if (word.Length is 0 or > 48)
        {
            return;
        }

        candidates.Add(new QueryCandidate(word, LookupDirection.EnglishToChinese));

        var phraseStart = wordStart;
        var words = 1;
        while (phraseStart > 0 && words < MaxPhraseWords)
        {
            var previousEnd = phraseStart;
            while (previousEnd > 0 && char.IsWhiteSpace(text[previousEnd - 1]))
            {
                previousEnd--;
            }

            if (previousEnd == 0 || IsPhraseBoundary(text[previousEnd - 1]))
            {
                break;
            }

            var previousStart = previousEnd;
            while (previousStart > 0 && IsEnglishWordCharacter(text[previousStart - 1]))
            {
                previousStart--;
            }

            if (previousStart == previousEnd || !ContainsLetter(text, previousStart, previousEnd))
            {
                break;
            }

            phraseStart = previousStart;
            words++;
        }

        var phrase = text[phraseStart..wordEnd].Trim();
        if (words > 1 && phrase.Length <= 48)
        {
            candidates.Add(new QueryCandidate(phrase, LookupDirection.EnglishToChinese));
        }
    }

    private static void AddChineseCandidate(string text, int caret, ICollection<QueryCandidate> candidates)
    {
        var characterIndex = caret;
        if (characterIndex == text.Length ||
            (characterIndex < text.Length && !IsChineseCharacter(text[characterIndex])))
        {
            if (characterIndex > 0 && IsChineseCharacter(text[characterIndex - 1]))
            {
                characterIndex--;
            }
        }

        if (characterIndex < 0 || characterIndex >= text.Length || !IsChineseCharacter(text[characterIndex]))
        {
            return;
        }

        var start = characterIndex;
        var end = characterIndex + 1;
        while (start > 0 && characterIndex - start < MaxChineseCharacters - 1 &&
               IsChineseCharacter(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length && end - start < MaxChineseCharacters && IsChineseCharacter(text[end]))
        {
            end++;
        }

        var query = text[start..end];
        if (query.Length > 0)
        {
            candidates.Add(new QueryCandidate(query, LookupDirection.ChineseToEnglish));
        }
    }

    private static bool IsEnglishWordCharacter(char character) =>
        char.IsAsciiLetter(character) || character is '\'' or '-';

    private static bool ContainsLetter(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (char.IsAsciiLetter(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPhraseBoundary(char character) =>
        character is '.' or ',' or '!' or '?' or ';' or ':' or '\r' or '\n';

    private static bool IsChineseCharacter(char character)
    {
        return character is >= '\u3400' and <= '\u4DBF' or
            >= '\u4E00' and <= '\u9FFF' or
            >= '\uF900' and <= '\uFAFF';
    }
}
