using System.Text;
using Earcue.Core.Configuration;
using Earcue.Core.Models;
using Earcue.Speech.Asr;
using SherpaOnnx;

namespace Earcue.Speech.Tests;

public class HotwordTests
{
    [Fact]
    public void Vocabulary_NormalizesAndDeduplicatesWithoutSplittingEnglishPhrases()
    {
        Assert.Equal(new[] { "语音识别", "SQL Server", "C#" }, ProfessionalVocabulary.Parse("语音识别\r\n SQL Server \n sql server\nＣ＃\n"));
        Assert.Throws<ArgumentException>(() => ProfessionalVocabulary.Parse(new string('a', 65)));
        Assert.Throws<ArgumentException>(() => ProfessionalVocabulary.Normalize(new[] { "a\nb" }));
        Assert.Throws<ArgumentException>(() => ProfessionalVocabulary.Normalize(Enumerable.Range(0, 201).Select(i => "词" + i)));
    }

    [Fact]
    public void Zipformer_UsesNativeHotwordBufferAndBeamSearch_ParaformerKeepsGreedy()
    {
        var root = Path.Combine(Path.GetTempPath(), "earcue-hotwords-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tokens = Path.Combine(root, "tokens.txt");
            File.WriteAllText(tokens, "<blk> 0\n语 1\n音 2\n识 3\n别 4\n");
            var options = new AsrOptions { Hotwords = new() { "语音识别", "SQL", "C#" }, HotwordsScore = 2.5f };
            var config = new OnlineRecognizerConfig();
            var result = AsrHotwordConfiguration.Apply(ref config, options, new AsrModelFiles { ModelDirectory=root, Tokens=tokens });
            Assert.Equal(1, result.Applied);
            Assert.Equal(2, result.Skipped);
            Assert.Equal("modified_beam_search", config.DecodingMethod);
            Assert.Equal("cjkchar", config.ModelConfig.ModelingUnit);
            Assert.Equal("语音识别\n", config.HotwordsBuf);
            Assert.Equal(Encoding.UTF8.GetByteCount(config.HotwordsBuf), config.HotwordsBufSize);
            Assert.Equal(2.5f, config.HotwordsScore);
            var paraformer = new OnlineRecognizerConfig();
            Assert.Equal(0, AsrHotwordConfiguration.Apply(ref paraformer, options, new AsrModelFiles { ModelType=AsrModelType.Paraformer }).Applied);
            Assert.Equal("greedy_search", paraformer.DecodingMethod);
            Assert.True(string.IsNullOrEmpty(paraformer.HotwordsBuf));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Terminology_IsIncludedAsReferenceButNeverInConnectionProbe()
    {
        var prompt = new Prompt { ProfessionalTerms = new[] { "Kubernetes", "附耳" } };
        Assert.Contains("Kubernetes", prompt.BuildSystemPrompt());
        Assert.Contains("不强行替换", prompt.BuildSystemPrompt());
        prompt.Mode = PromptMode.ConnectionTest;
        Assert.DoesNotContain("Kubernetes", prompt.BuildSystemPrompt());
    }
}
