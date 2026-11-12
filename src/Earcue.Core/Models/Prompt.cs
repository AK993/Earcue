namespace Earcue.Core.Models;

public enum PromptMode
{
    /// <summary>用户明确提问，回答该问题。</summary>
    Question,

    /// <summary>会议中静音一段时间后，根据最近全部内容主动给出接话提示。</summary>
    AutoResponse
}

public sealed class Prompt
{
    public PromptMode Mode { get; set; } = PromptMode.Question;

    /// <summary>我在面试中的角色：候选人回答，或面试官追问。</summary>
    public InterviewRole Role { get; set; } = InterviewRole.Candidate;

    public string UserRoleDescription { get; set; } = string.Empty;
    public string UserStance { get; set; } = string.Empty;
    public string ResumeText { get; set; } = string.Empty;
    public int MaxResumeChars { get; set; } = 2500;

    public IReadOnlyList<DialogueTurn> RecentContext { get; set; } = Array.Empty<DialogueTurn>();
    public string TriggerQuestion { get; set; } = string.Empty;

    public string BuildSystemPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("你是我的会议助手。我只在屏幕角落看你给的提示，所以回答要口语化、可以直接说出口；把必要信息说完整，不要为了简短而省略关键信息，也不要长篇大论。");

        sb.AppendLine(Role == InterviewRole.Interviewer
            ? "【我的身份】我是面试官（面试者），正在面试候选人。"
            : "【我的身份】我是被面试者（候选人），正在回答面试官的问题。");

        if (!string.IsNullOrWhiteSpace(UserRoleDescription))
        {
            sb.AppendLine($"【我的岗位/身份】{UserRoleDescription.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(UserStance))
        {
            sb.AppendLine($"【我的立场】{UserStance.Trim()}");
        }

        sb.Append(BuildResumeSection());

        if (Mode == PromptMode.AutoResponse)
        {
            if (Role == InterviewRole.Interviewer)
            {
                sb.AppendLine("【场景】这是一场面试。系统会把最近一段对话发给你，候选人刚说完一段。");
                sb.AppendLine("请给出我作为面试官下一步最合适的追问或简短评估。");
                sb.AppendLine("【要求】");
                sb.AppendLine("1. 用第一人称、口语，长度按内容需要，像我在面试现场说的话");
                sb.AppendLine("2. 优先追问具体项目、数据、取舍或验证方式");
                sb.AppendLine("3. 不要替候选人编造经历，不要长篇总结");
                sb.AppendLine("4. 信息不足时就问一个最关键的问题");
            }
            else
            {
                sb.AppendLine("【场景】这是一场面试。系统会把最近一段对话发给你，面试官可能在提问、让我自我介绍、让我介绍项目或追问经历。");
                sb.AppendLine("即使对方不是提问，也可能是在点名让我发言、让我自我介绍、征求我的意见或等我接话。");
                sb.AppendLine("请结合我的简历，给出我此刻最合适的回答。");
                sb.AppendLine("【要求】");
                sb.AppendLine("1. 用第一人称、口语，长度按内容需要，像我在面试现场回答");
                sb.AppendLine("2. 优先结合简历中的真实经历；简历没有的不要编造");
                sb.AppendLine("3. 先给结论，再补充必要依据；可以分 2–3 点，但不要长篇大论");
                sb.AppendLine("4. 信息不足时输出\"这个我不确定，建议确认 XX\"");
            }

            return sb.ToString();
        }

        if (Role == InterviewRole.Interviewer)
        {
            sb.AppendLine("【要求】");
            sb.AppendLine("1. 用第一人称、口语，长度按内容需要，像我在面试现场说的话");
            sb.AppendLine("2. 根据候选人的回答，给出一句最合适的追问或澄清");
            sb.AppendLine("3. 不要替候选人编造经历");
            sb.AppendLine("4. 信息不足时追问具体项目、数据或取舍");
        }
        else
        {
            sb.AppendLine("【要求】");
            sb.AppendLine("1. 用第一人称、口语，长度按内容需要，像我在面试现场回答");
            sb.AppendLine("2. 优先结合简历中的真实经历，简历没有的不要编造");
            sb.AppendLine("3. 先给结论，再补充必要依据；可以分 2–3 点，但不要长篇大论");
            sb.AppendLine("4. 证据不足时输出\"这个我不确定，建议确认 XX\"");
        }

        return sb.ToString();
    }

    public string BuildUserMessage()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【最近对话】");
        foreach (var turn in RecentContext)
        {
            sb.AppendLine(turn.ToString());
        }

        if (Mode == PromptMode.AutoResponse)
        {
            sb.AppendLine(Role == InterviewRole.Interviewer
                ? "【任务】结合以上对话，给我一段现在可以接着问候选人的追问（简洁、口语，长度按内容需要）。"
                : "【任务】结合我的简历和以上对话，给我一段现在可以直接回答面试官的话（简洁、口语，长度按内容需要）。");
        }
        else
        {
            sb.AppendLine($"[对方] {TriggerQuestion}");
        }

        return sb.ToString();
    }

    private string BuildResumeSection()
    {
        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            return string.Empty;
        }

        var limit = Math.Max(200, MaxResumeChars);
        var text = ResumeText.Trim();
        if (text.Length > limit)
        {
            text = text[..limit] + "\n…（简历已截断）";
        }

        var title = Role == InterviewRole.Interviewer
            ? "【岗位要求 / 候选人背景】"
            : "【我的简历】";

        return $"{title}\n{text}\n";
    }
}
