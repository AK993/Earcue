using Earcue.Core.Configuration;
using Earcue.Core.Models;
using Xunit;

namespace Earcue.Core.Tests;

public class CoreModelTests
{
    [Fact]
    public void AudioFrame_CalculateRms_ReturnsExpectedEnergy()
    {
        // 构造一个简单的正弦波或非零样本
        var pcm = new byte[320]; // 160 个 16-bit 样本
        for (int i = 0; i < pcm.Length; i += 2)
        {
            short sample = 1000;
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var frame = new AudioFrame(
            AudioRole.Remote,
            pcm,
            sampleRate: 16000,
            channels: 1,
            bitsPerSample: 16,
            timestamp: TimeSpan.Zero
        );

        double rms = frame.CalculateRms();
        Assert.True(rms > 990 && rms < 1010);
    }

    [Fact]
    public void EarcueConfig_Serialization_WorksAccurately()
    {
        var config = new EarcueConfig();
        config.Llm.Model = "deepseek-chat";
        config.Overlay.Opacity = 0.85;

        var tempPath = Path.Combine(Path.GetTempPath(), $"earcue_test_{Guid.NewGuid():N}.json");
        try
        {
            config.SaveToFile(tempPath);
            Assert.True(File.Exists(tempPath));

            var loaded = EarcueConfig.LoadFromFile(tempPath);
            Assert.Equal("deepseek-chat", loaded.Llm.Model);
            Assert.Equal(0.85, loaded.Overlay.Opacity);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void LlmOptions_NoHardCharacterLimitAndHasOutputTokenBudget()
    {
        var options = new LlmOptions();

        Assert.Equal(0, options.MaxChars);
        Assert.InRange(options.MaxTokens, 512, 8192);
    }

    [Fact]
    public void Prompt_BuildSystemPrompt_ContainsIdentityAndNoHardLimit()
    {
        var prompt = new Prompt
        {
            UserRoleDescription = "研发技术主管",
            TriggerQuestion = "怎么解决？"
        };

        var systemPrompt = prompt.BuildSystemPrompt();
        Assert.DoesNotContain("40 字", systemPrompt);
        Assert.DoesNotContain("≤ 40", systemPrompt);
        Assert.Contains("研发技术主管", systemPrompt);
        Assert.Contains("第一人称", systemPrompt);
    }
}
