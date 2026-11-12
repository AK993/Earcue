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
}
