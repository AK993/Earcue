namespace Earcue.Core.Models;

public sealed class Prompt
{
    public string UserRoleDescription { get; set; } = string.Empty;
    public string UserStance { get; set; } = string.Empty;
    public IReadOnlyList<DialogueTurn> RecentContext { get; set; } = Array.Empty<DialogueTurn>();
    public string TriggerQuestion { get; set; } = string.Empty;

    public string BuildSystemPrompt()
    {
        return "你是我的会议助手。我只在屏幕角落看你给的提示，所以答案必须极短。\n" +
               (string.IsNullOrWhiteSpace(UserRoleDescription) ? "" : $"【我的角色】{UserRoleDescription}\n") +
               (string.IsNullOrWhiteSpace(UserStance) ? "" : $"【我的立场】{UserStance}\n") +
               "【要求】\n" +
               "1. 用第一人称、口语，≤ 40 字，像我会说的话\n" +
               "2. 只给一个最可能的答案，不要列举多个方案\n" +
               "3. 证据不足时输出\"这个我不确定，建议确认 XX\"";
    }

    public string BuildUserMessage()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【最近对话】");
        foreach (var turn in RecentContext)
        {
            sb.AppendLine(turn.ToString());
        }
        sb.AppendLine($"[对方] {TriggerQuestion}");
        return sb.ToString();
    }
}
