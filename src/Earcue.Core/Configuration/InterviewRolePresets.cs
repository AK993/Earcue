using Earcue.Core.Models;

namespace Earcue.Core.Configuration;

public sealed class InterviewRolePreset
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}

/// <summary>面试场景下的“我”的角色。</summary>
public static class InterviewRolePresets
{
    public const string Candidate = "Candidate";
    public const string Interviewer = "Interviewer";

    public static IReadOnlyList<InterviewRolePreset> All { get; } = new List<InterviewRolePreset>
    {
        new()
        {
            Id = Candidate,
            DisplayName = "被面试者（候选人）",
            Description = "面试官提问，我负责回答。系统会结合我的简历生成可以直接说出口的回答。"
        },
        new()
        {
            Id = Interviewer,
            DisplayName = "面试者（面试官）",
            Description = "我负责提问和评估。系统会结合对话生成下一步追问或简短评估。"
        }
    };

    public static InterviewRolePreset Find(string? id)
    {
        foreach (var preset in All)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return All[0];
    }

    public static InterviewRole ToRole(string? id) =>
        string.Equals(Find(id).Id, Interviewer, StringComparison.OrdinalIgnoreCase)
            ? InterviewRole.Interviewer
            : InterviewRole.Candidate;
}
