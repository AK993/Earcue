using Earcue.Core.Configuration;
using Earcue.Core.Models;
using Xunit;

namespace Earcue.Core.Tests;

public class ProfileAndResumeTests
{
    [Fact]
    public void ProfileOptions_DefaultToCandidateWithNoResume()
    {
        var profile = new ProfileOptions();

        Assert.Equal(InterviewRolePresets.Candidate, profile.Role);
        Assert.Equal(InterviewRole.Candidate, profile.RoleValue);
        Assert.Equal(string.Empty, profile.Resume);
        Assert.InRange(profile.MaxResumeChars, 500, 10000);
        Assert.Contains("被面试者", profile.RoleDisplayName);
    }

    [Fact]
    public void InterviewRolePresets_MapIdsAndFallback()
    {
        Assert.Equal(InterviewRole.Candidate,
            InterviewRolePresets.ToRole(InterviewRolePresets.Candidate));
        Assert.Equal(InterviewRole.Interviewer,
            InterviewRolePresets.ToRole("INTERVIEWER"));
        Assert.Equal(InterviewRole.Candidate, InterviewRolePresets.ToRole("bogus"));
        Assert.Contains("面试官", InterviewRolePresets.Find(InterviewRolePresets.Interviewer).DisplayName);
    }

    [Fact]
    public void ProfileOptions_RoundTripThroughConfig()
    {
        var config = new EarcueConfig();
        config.Profile.Role = InterviewRolePresets.Interviewer;
        config.Profile.Resume = "十年后端经验，负责过高并发交易系统。";
        config.Profile.MaxResumeChars = 1800;

        var path = Path.Combine(Path.GetTempPath(), $"earcue_profile_{Guid.NewGuid():N}.json");
        try
        {
            config.SaveToFile(path);
            var loaded = EarcueConfig.LoadFromFile(path);

            Assert.Equal(InterviewRolePresets.Interviewer, loaded.Profile.Role);
            Assert.Equal(InterviewRole.Interviewer, loaded.Profile.RoleValue);
            Assert.Contains("高并发", loaded.Profile.Resume);
            Assert.Equal(1800, loaded.Profile.MaxResumeChars);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void CandidateAutoPrompt_IncludesResumeAndSelfIntroduction()
    {
        var prompt = new Prompt
        {
            Mode = PromptMode.AutoResponse,
            Role = InterviewRole.Candidate,
            ResumeText = "五年 Java 后端经验，做过灰度发布平台。",
            RecentContext = new[]
            {
                new DialogueTurn(AudioRole.Remote, "让我自我介绍一下", DateTime.UtcNow)
            }
        };

        var system = prompt.BuildSystemPrompt();
        Assert.Contains("被面试者", system);
        Assert.Contains("自我介绍", system);
        Assert.Contains("【我的简历】", system);
        Assert.Contains("灰度发布平台", system);
        Assert.Contains("不要编造", system);

        var user = prompt.BuildUserMessage();
        Assert.Contains("让我自我介绍一下", user);
        Assert.Contains("回答面试官", user);
    }

    [Fact]
    public void InterviewerAutoPrompt_AsksForFollowUpInsteadOfAnswering()
    {
        var prompt = new Prompt
        {
            Mode = PromptMode.AutoResponse,
            Role = InterviewRole.Interviewer,
            ResumeText = "岗位要求：熟悉分布式系统。",
            RecentContext = new[]
            {
                new DialogueTurn(AudioRole.Remote, "我在上一家公司负责订单系统", DateTime.UtcNow)
            }
        };

        var system = prompt.BuildSystemPrompt();
        Assert.Contains("面试官", system);
        Assert.Contains("追问", system);
        Assert.Contains("【岗位要求 / 候选人背景】", system);

        var user = prompt.BuildUserMessage();
        Assert.Contains("追问", user);
        Assert.DoesNotContain("回答面试官", user);
    }

    [Fact]
    public void Resume_IsTruncatedToConfiguredLimit()
    {
        var resume = new string('A', 400) + "SECRET_TAIL";
        var prompt = new Prompt
        {
            Role = InterviewRole.Candidate,
            ResumeText = resume,
            MaxResumeChars = 200
        };

        var system = prompt.BuildSystemPrompt();
        Assert.Contains("简历已截断", system);
        Assert.DoesNotContain("SECRET_TAIL", system);
    }
}
