using Earcue.Core.Configuration;

namespace Earcue.Brain.Understanding;

/// <summary>
/// 疑问句判定引擎（v1 规则版）
/// 严格依据 8 条已冻结前提：高精准率优先，宁可漏答，不可乱答。
/// </summary>
public sealed class QuestionDetector
{
    private readonly TriggerOptions _options;

    // 典型中文疑问词表
    private static readonly string[] QuestionKeywords =
    {
        "吗", "呢", "什么", "怎么", "为什么", "是否", "能不能", "可不可以", "会不会",
        "请问", "如何", "多少", "哪", "有没有", "哪儿", "哪里", "哪个", "几时", "谁", "可否", "怎样"
    };

    public QuestionDetector(TriggerOptions? options = null)
    {
        _options = options ?? new TriggerOptions();
    }

    /// <summary>
    /// 判断给定的断句文本是否为一个有效疑问句
    /// </summary>
    public bool IsQuestion(string text, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "文本为空";
            return false;
        }

        var trimmed = text.Trim();

        // 1. 长度过滤（过滤 "嗯？"、"啊？" 等非有效问题）
        if (trimmed.Length < _options.MinChars)
        {
            reason = $"文本长度 ({trimmed.Length}) 小于最小字数限制 ({_options.MinChars})";
            return false;
        }

        // 2. 标点符号检测：句末包含问号
        bool hasQuestionMark = trimmed.EndsWith("?") || trimmed.EndsWith("？");

        // 3. 疑问词关键词匹配
        bool hasQuestionKeyword = false;
        foreach (var kw in QuestionKeywords)
        {
            if (trimmed.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                hasQuestionKeyword = true;
                break;
            }
        }

        if (hasQuestionMark || hasQuestionKeyword)
        {
            reason = hasQuestionMark ? "包含问号" : "命中疑问词";
            return true;
        }

        reason = "未包含疑问特征";
        return false;
    }
}
