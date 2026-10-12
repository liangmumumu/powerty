using System.Text;

namespace Powerty.Core;

/// <summary>
/// Cleans up raw OCR output: OCR inserts spaces between CJK characters and line
/// breaks where the visual layout wraps; both must be undone before translating.
/// </summary>
public static class OcrTextCleaner
{
    public const int MaxTextLength = 600;

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var builder = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            if (builder.Length > 0)
            {
                // Keep a single space between lines; CJK lines are joined without one.
                if (!(IsCjk(builder[^1]) && IsCjk(line[0])))
                {
                    builder.Append(' ');
                }
            }

            AppendLine(builder, line);
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length > MaxTextLength ? normalized[..MaxTextLength] : normalized;
    }

    private static void AppendLine(StringBuilder builder, string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == ' ')
            {
                var next = index + 1;
                while (next < line.Length && line[next] == ' ')
                {
                    next++;
                }

                // Drop spaces sitting between two CJK characters ("你 好 世 界" -> "你好世界").
                var previousIsCjk = builder.Length > 0 && IsCjk(builder[^1]);
                var nextIsCjk = next < line.Length && IsCjk(line[next]);
                if (previousIsCjk && nextIsCjk)
                {
                    continue;
                }

                if (builder.Length > 0 && builder[^1] == ' ')
                {
                    continue;
                }

                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }
    }

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    /// <summary>Picks the translation direction from the dominant script of OCR'd text.</summary>
    public static bool LooksChinese(string text)
    {
        var cjk = 0;
        var latin = 0;
        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                cjk++;
            }
            else if (char.IsAsciiLetter(character))
            {
                latin++;
            }
        }

        return cjk * 2 >= latin;
    }
}
