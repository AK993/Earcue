using Earcue.Brain.Context;
using Earcue.Brain.Understanding;
using Earcue.Core.Configuration;
using Earcue.Core.Models;
using Xunit;

namespace Earcue.Brain.Tests;

public class UnderstandingTests
{
    [Theory]
    [InlineData("那你们这个灰度策略怎么保证回滚？", true)]
    [InlineData("请问下周一能上线吗", true)]
    [InlineData("为什么这个接口延迟这么高？", true)]
    [InlineData("这个需求谁负责？", true)]
    [InlineData("好的收到", false)]
    [InlineData("今天下午两点开周会", false)]
    [InlineData("嗯？", false)] // 过短过滤 (< 5 字符)
    public void QuestionDetector_ClassifiesAccurately(string text, bool expectedIsQuestion)
    {
        var detector = new QuestionDetector(new TriggerOptions { MinChars = 5 });
        bool isQuestion = detector.IsQuestion(text, out _);
        Assert.Equal(expectedIsQuestion, isQuestion);
    }

    [Fact]
    public void TriggerCooldown_SuppressesRapidDuplicateQuestions()
    {
        var options = new TriggerOptions
        {
            CooldownMs = 8000,
            MinIntervalMs = 5000,
            MinChars = 5
        };
        var manager = new TriggerCooldownManager(options);

        // 第一次触发应成功
        bool first = manager.ShouldTrigger("这个需求什么时候能上线呢？", out _);
        Assert.True(first);

        // 立即重复触发应被抑制
        bool second = manager.ShouldTrigger("这个需求什么时候能上线呢？", out string reason);
        Assert.False(second);
        Assert.Contains("过密", reason);
    }

    [Fact]
    public void DialogueContextBuffer_MaintainsRecentTurns()
    {
        var buffer = new DialogueContextBuffer(TimeSpan.FromSeconds(90));
        buffer.AddTurn(AudioRole.Local, "我们准备下周发布");
        buffer.AddTurn(AudioRole.Remote, "回滚策略如何考虑？");

        var turns = buffer.GetRecentTurns();
        Assert.Equal(2, turns.Count);
        Assert.Equal(AudioRole.Local, turns[0].Role);
        Assert.Equal(AudioRole.Remote, turns[1].Role);
    }

    [Fact]
    public void FollowUpIncludesActualAnswerEvenWhenItTakesLongerThanNinetySeconds()
    {
        var now = DateTime.UtcNow;
        var buffer = new DialogueContextBuffer();
        buffer.AddTurn(AudioRole.Remote, "你们怎么做灰度发布？", now.AddMinutes(-3));
        buffer.AddTurn(AudioRole.Remote, "为什么选这个比例？", now);
        buffer.AddTurn(AudioRole.Local, "我们每批先放百分之五。", now.AddMinutes(-2));

        var context = buffer.GetRecentTurns();
        Assert.Equal(AudioRole.Local, context[1].Role);
        var prompt = new Prompt { Mode = PromptMode.AutoResponse, RecentContext = context, TriggerQuestion = context[2].Text };
        Assert.Contains("[我] 我们每批先放百分之五。", prompt.BuildUserMessage());
        Assert.EndsWith("为什么选这个比例？", prompt.BuildUserMessage().Split("【任务】")[0].TrimEnd());

        buffer.RemoveLocalTurns();
        Assert.All(buffer.GetRecentTurns(), turn => Assert.Equal(AudioRole.Remote, turn.Role));
    }

    [Fact]
    public void PlaybackFilter_DoesNotSuppressDistinctOrLaterFollowUps()
    {
        var now = DateTime.UtcNow;
        var buffer = new DialogueContextBuffer();
        buffer.AddTurn(AudioRole.Local, "我们每批先放百分之五。", now);

        Assert.True(buffer.IsLikelyLocalPlayback("我们每批先放百分之五", now.AddMilliseconds(200)));
        Assert.False(buffer.IsLikelyLocalPlayback("为什么每批先放百分之五？", now.AddMilliseconds(300)));
        Assert.False(buffer.IsLikelyLocalPlayback("我们每批先放百分之五", now.AddSeconds(4)));
        Assert.False(buffer.IsLikelyLocalPlayback("我们每批先放百分之五", now.AddMilliseconds(-400)));
        Assert.False(buffer.IsLikelyLocalPlayback("我们每批先放百分之五", now.AddMilliseconds(20)));
    }
}
