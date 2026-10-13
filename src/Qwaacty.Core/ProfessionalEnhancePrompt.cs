using System.Text;

namespace Qwaacty.Core;

/// <summary>
/// Builds the AI prompt used by the professional glossary panel's
/// "AI 增强（翻译 + 解释）" button: the model first gives a bilingual translation
/// of the term, then a rigorous Chinese explanation (definition, key principle,
/// typical application), so the enhancement covers both translation and explanation.
/// </summary>
public static class ProfessionalEnhancePrompt
{
    public static string BuildSystem() =>
        "你是电子信息工程领域的学术助手，精通中英专业术语。回答使用简体中文，学术严谨、表达精炼；" +
        "只输出纯文本，不要使用 Markdown 标记（如 **、#、代码块），不要添加前后缀或结语。";

    public static string BuildUser(ProfessionalEntry entry)
    {
        var builder = new StringBuilder(320);
        builder.Append("请针对电子信息专业术语生成两部分内容，严格使用下面两个小节标题（不要输出其他标题）：\n");
        builder.Append("【翻译】中英对照：英文术语“").Append(entry.Term).Append('”');
        if (entry.IsAbbreviation)
        {
            builder.Append("（缩略语）");
        }

        if (!string.Equals(entry.Chinese, entry.Term, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("；常见中文译名：“").Append(entry.Chinese).Append('”');
        }

        builder.Append("。若为缩略语请给出英文全称与标准中文译名；若有多个常见译法，列出最通用的 1~2 个。\n");
        builder.Append("【解释】用简体中文给出学术性解释（3~6 句）：说明其定义、关键原理或典型参数，以及应用场景；避免与【翻译】内容重复。");
        return builder.ToString();
    }
}
