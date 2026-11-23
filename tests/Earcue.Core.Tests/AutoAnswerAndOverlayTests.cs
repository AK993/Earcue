using Earcue.Core.Configuration;
using Earcue.Core.Models;
using System.Text.Json;
using Xunit;

namespace Earcue.Core.Tests;

public class AutoAnswerAndOverlayTests
{
    [Fact]
    public void MicrophoneContext_RequiresExplicitOptInEvenWithLegacySettings()
    {
        var legacy = JsonSerializer.Deserialize<EarcueConfig>("""{"mic":{"enabled":true,"echoGate":true}}""");
        Assert.False(legacy!.Mic.CaptureForContext);
        legacy.Mic.CaptureForContext = true;
        Assert.True(JsonSerializer.Deserialize<EarcueConfig>(JsonSerializer.Serialize(legacy))!.Mic.CaptureForContext);
    }

    [Fact]
    public void Prompt_AutoMode_SendsWholeContextAndSupportsNonQuestions()
    {
        var prompt = new Prompt
        {
            Mode = PromptMode.AutoResponse,
            UserRoleDescription = "技术研发负责人",
            RecentContext = new[]
            {
                new DialogueTurn(AudioRole.Remote, "让我自我介绍一下", DateTime.UtcNow),
                new DialogueTurn(AudioRole.Remote, "我们这次主要做会议助手", DateTime.UtcNow)
            }
        };

        var system = prompt.BuildSystemPrompt();
        Assert.Contains("自我介绍", system);
        Assert.Contains("不是提问", system);
        Assert.DoesNotContain("40 字", system);
        Assert.Contains("长度按内容需要", system);
        Assert.Contains("第一人称", system);

        var user = prompt.BuildUserMessage();
        Assert.Contains("[对方] 让我自我介绍一下", user);
        Assert.Contains("[对方] 我们这次主要做会议助手", user);
        Assert.Contains("【任务】", user);
    }

    [Fact]
    public void Prompt_QuestionMode_StillAppendsExplicitQuestion()
    {
        var prompt = new Prompt
        {
            Mode = PromptMode.Question,
            TriggerQuestion = "灰度怎么回滚？",
            RecentContext = new[] { new DialogueTurn(AudioRole.Remote, "前面说了灰度", DateTime.UtcNow) }
        };

        var user = prompt.BuildUserMessage();
        Assert.Contains("[对方] 灰度怎么回滚？", user);
        Assert.DoesNotContain("【任务】", user);
    }

    [Fact]
    public void AutoAnswerOptions_HaveSensibleDefaultsAndRoundTrip()
    {
        var defaults = new AutoAnswerOptions();
        Assert.True(defaults.Enabled);
        Assert.True(defaults.ExtractAndCorrectQuestion);
        Assert.InRange(defaults.SilenceMs, 500, 10000);
        Assert.True(defaults.MinChars > 0);

        var config = new EarcueConfig();
        config.AutoAnswer.Enabled = false;
        config.AutoAnswer.ExtractAndCorrectQuestion = false;
        config.AutoAnswer.SilenceMs = 3500;
        config.AutoAnswer.MinChars = 8;
        config.AutoAnswer.CooldownMs = 7000;

        var path = Path.Combine(Path.GetTempPath(), $"earcue_auto_{Guid.NewGuid():N}.json");
        try
        {
            config.SaveToFile(path);
            var loaded = EarcueConfig.LoadFromFile(path);
            Assert.False(loaded.AutoAnswer.Enabled);
            Assert.False(loaded.AutoAnswer.ExtractAndCorrectQuestion);
            Assert.Equal(3500, loaded.AutoAnswer.SilenceMs);
            Assert.Equal(8, loaded.AutoAnswer.MinChars);
            Assert.Equal(7000, loaded.AutoAnswer.CooldownMs);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void OverlayOptions_DefaultToBottomLeftWithReadableFont()
    {
        var options = new OverlayOptions();
        Assert.Equal(OverlayPositionPresets.BottomLeft, options.Position);
        Assert.Equal("Microsoft YaHei UI", options.FontFamily);
        Assert.InRange(options.Opacity, 0.05, 1.0);
        Assert.True(options.AnswerFontSize > options.QuestionFontSize);
        Assert.True(options.MarginX >= 0);
        Assert.True(options.MarginY >= 0);
        Assert.False(options.AutoHide);
        Assert.Equal("#00F0FF", options.AnswerColor);
        Assert.True(options.Width >= 300);
        Assert.True(options.MinHeight >= 0);
    }

    [Fact]
    public void OverlayOptions_RoundTripWithPositionAndFont()
    {
        var config = new EarcueConfig();
        config.Overlay.Position = OverlayPositionPresets.TopRight;
        config.Overlay.FontFamily = "SimHei";
        config.Overlay.AnswerFontSize = 22;
        config.Overlay.QuestionFontSize = 14;
        config.Overlay.Opacity = 0.75;
        config.Overlay.MarginX = 48;
        config.Overlay.MarginY = 36;
        config.Overlay.AutoHide = true;
        config.Overlay.AutoFadeoutSeconds = 30;
        config.Overlay.Width = 620;
        config.Overlay.MinHeight = 180;
        config.Overlay.AnswerColor = "#FACC15";

        var path = Path.Combine(Path.GetTempPath(), $"earcue_overlay_{Guid.NewGuid():N}.json");
        try
        {
            config.SaveToFile(path);
            var loaded = EarcueConfig.LoadFromFile(path);
            Assert.Equal(OverlayPositionPresets.TopRight, loaded.Overlay.Position);
            Assert.Equal("SimHei", loaded.Overlay.FontFamily);
            Assert.Equal(22, loaded.Overlay.AnswerFontSize);
            Assert.Equal(14, loaded.Overlay.QuestionFontSize);
            Assert.Equal(0.75, loaded.Overlay.Opacity);
            Assert.Equal(48, loaded.Overlay.MarginX);
            Assert.Equal(36, loaded.Overlay.MarginY);
            Assert.True(loaded.Overlay.AutoHide);
            Assert.Equal(30, loaded.Overlay.AutoFadeoutSeconds);
            Assert.Equal(620, loaded.Overlay.Width);
            Assert.Equal(180, loaded.Overlay.MinHeight);
            Assert.Equal("#FACC15", loaded.Overlay.AnswerColor);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Theory]
    [InlineData("BottomLeft", "BottomLeft")]
    [InlineData("TopRight", "TopRight")]
    [InlineData("Custom", "Custom")]
    [InlineData("bogus", "BottomLeft")]
    [InlineData(null, "BottomLeft")]
    public void OverlayPositionPresets_FallBackToBottomLeft(string? input, string expected)
    {
        Assert.Equal(expected, OverlayPositionPresets.Find(input).Id);
    }
}
