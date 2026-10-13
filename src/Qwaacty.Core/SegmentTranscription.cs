namespace Qwaacty.Core;

/// <summary>Helpers for the "input translation" module: decide the translation
/// direction from the content, find the text typed in the current round, and apply
/// English sentence-start capitalization.</summary>
public static class SegmentTranscription
{
    /// <summary>Characters that end a sentence for splitting and capitalization.</summary>
    private static readonly char[] SentenceTerminators = ['。', '！', '？', '!', '?', '；', ';', '…'];

    private const int SentenceFallbackCharacters = 300;

    /// <summary>Picks the direction from the content: Chinese input translates to
    /// English, anything else to Chinese.</summary>
    public static LookupDirection DetectDirection(string segment)
    {
        foreach (var character in segment)
        {
            if (IsCjk(character))
            {
                return LookupDirection.ChineseToEnglish;
            }
        }

        return LookupDirection.EnglishToChinese;
    }

    /// <summary>True when the segment starts a sentence: the character right before it
    /// is a sentence terminator or a line break, or nothing precedes it (field start).</summary>
    public static bool IsSentenceBoundary(char character) =>
        character is '\n' or '\r' || Array.IndexOf(SentenceTerminators, character) >= 0;

    /// <summary>Uppercases the first ASCII letter of an English translation when it
    /// starts a sentence; leaves every other case untouched.</summary>
    public static string ApplySentenceStartCapitalization(string translation, bool sentenceStart)
    {
        if (!sentenceStart || string.IsNullOrEmpty(translation))
        {
            return translation;
        }

        var characters = translation.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] is >= 'a' and <= 'z')
            {
                characters[index] = char.ToUpperInvariant(characters[index]);
                return new string(characters);
            }

            if (characters[index] is >= 'A' and <= 'Z')
            {
                return translation;
            }

            if (!char.IsWhiteSpace(characters[index]) && !IsOpeningPunctuation(characters[index]))
            {
                // Starts with a digit or a non-Latin script: nothing to capitalize.
                return translation;
            }
        }

        return translation;
    }

    /// <summary>The text typed since the round anchor. Returns an empty string when the
    /// round is known to be empty, or null when the anchor is unusable and the caller
    /// should fall back to <see cref="SentenceSegment"/>.</summary>
    public static string? ComputeRoundSegment(string tail, int lengthBeforeCaret, bool lengthCapped, int anchorLength)
    {
        if (string.IsNullOrEmpty(tail) || lengthCapped || anchorLength < 0 || anchorLength > lengthBeforeCaret)
        {
            return null;
        }

        var typed = lengthBeforeCaret - anchorLength;
        if (typed > tail.Length)
        {
            // The anchor predates the readable tail window; cannot reconstruct.
            return null;
        }

        return typed == 0 ? string.Empty : tail[^typed..];
    }

    /// <summary>Fallback when no anchor is available: the text after the last sentence
    /// terminator inside the tail (bounded, so an old corner of a long field is never
    /// dragged along).</summary>
    public static string SentenceSegment(string tail, int maxLength = SentenceFallbackCharacters)
    {
        if (string.IsNullOrEmpty(tail))
        {
            return string.Empty;
        }

        var start = Math.Max(0, tail.Length - maxLength);
        for (var index = tail.Length - 1; index > start; index--)
        {
            if (IsSentenceBoundary(tail[index]))
            {
                start = index + 1;
                break;
            }
        }

        return tail[start..];
    }

    private static bool IsOpeningPunctuation(char character) =>
        character is '"' or '\'' or '“' or '‘' or '(' or '（' or '[' or '【' or '《' or '-';

    private static bool IsCjk(char character) =>
        (character >= '\u4E00' && character <= '\u9FFF') ||
        (character >= '\u3400' && character <= '\u4DBF') ||
        (character >= '\u3000' && character <= '\u303F') ||
        (character >= '\uFF00' && character <= '\uFFEF');
}
