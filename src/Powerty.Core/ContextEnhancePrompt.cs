using System.Text;

namespace Powerty.Core;

/// <summary>
/// Builds the AI prompt used by the "AI context enhance" selection feature and
/// decides which selections are eligible (single words and short phrases).
/// </summary>
public static class ContextEnhancePrompt
{
    public const int MaxContextCharacters = 360;
    private const int MaxCjkQueryLength = 32;
    private const int MaxLatinQueryLength = 64;
    private const int MaxLatinQueryWords = 6;

    public static bool Qualifies(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (GlossTokenizer.ContainsCjk(trimmed))
        {
            return trimmed.Length <= MaxCjkQueryLength;
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= MaxLatinQueryWords && trimmed.Length <= MaxLatinQueryLength;
    }

    public static string BuildSystem(LookupDirection direction) => direction == LookupDirection.EnglishToChinese
        ? "你是中英翻译助手，任务是结合语境对选中的英文内容做情景化翻译。用户会给出【语境】和【选中内容】。请：1) 推断选中内容在当前语境中的词性和含义（若语境缺失则按最常用含义）；2) 给出最贴合语境的中文翻译。严格按两行输出，不要多余内容：\n语境义：<词性>. <简明中文释义>\n情境翻译：<中文译文>"
        : "你是中英翻译助手，任务是结合语境对选中的中文内容做情景化翻译。用户会给出【语境】和【选中内容】。请：1) 推断选中内容在当前语境中的词性/用法（若语境缺失则按最常用含义）；2) 给出最贴合语境的英文表达。严格按两行输出，不要多余内容：\n语境义：<词性/用法>. <简明中文说明>\n情境翻译：<英文译文>";

    public static string BuildUser(string query, string? context, LookupDirection direction)
    {
        var builder = new StringBuilder(512);
        builder.Append("【语境】\n");
        if (string.IsNullOrWhiteSpace(context))
        {
            builder.Append("（无可用语境）");
        }
        else
        {
            var squashed = Squash(context);
            builder.Append(squashed.Length > MaxContextCharacters ? squashed[..MaxContextCharacters] + "…" : squashed);
        }

        builder.Append("\n\n【选中内容】\n");
        builder.Append(query.Trim());
        builder.Append("\n\n【目标】");
        builder.Append(direction == LookupDirection.EnglishToChinese ? "翻译为中文" : "翻译为英文");
        return builder.ToString();
    }

    private static string Squash(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var character in text)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(character);
            }

            lastWasSpace = isSpace;
        }

        return builder.ToString().Trim();
    }
}
