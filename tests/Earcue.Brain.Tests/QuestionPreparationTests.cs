using Earcue.Brain.Context;
using Earcue.Brain.Understanding;
using Earcue.Core.Models;

namespace Earcue.Brain.Tests;

public class QuestionPreparationTests
{
    private static string Response(string metadata, string answer = "先说明结论，再解释依据。") =>
        QuestionAnswerStream.QuestionMarker + "\n" + metadata + "\n" + QuestionAnswerStream.AnswerMarker + "\n" + answer;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(1024)]
    public void StreamingSeparatesCorrectedQuestionsFromAnswerAcrossChunkBoundaries(int chunkSize)
    {
        var response = Response("{\"questions\":[\"Redis 如何保证一致性？\",\"失败后怎样回滚？\"],\"uncertain\":false}");
        var stream = new QuestionAnswerStream();
        for (var i = 0; i < response.Length; i += chunkSize)
        {
            stream.Push(response.Substring(i, Math.Min(chunkSize, response.Length - i)));
            Assert.DoesNotContain("EARCUE", stream.Answer);
            Assert.DoesNotContain("questions", stream.Answer);
        }
        stream.Complete();
        Assert.Equal(2, stream.Extraction!.Questions.Count);
        Assert.Contains("Redis", stream.Extraction.Text);
        Assert.False(stream.Extraction.Uncertain);
        Assert.Equal("先说明结论，再解释依据。", stream.Answer);
        Assert.False(stream.UsedFallback);
    }

    [Fact]
    public void PartialMetadataNeverAppearsAsAnswer()
    {
        var stream = new QuestionAnswerStream();
        stream.Push("[EARCUE_QUESTION]\n{\"questions\":[\"如何");
        Assert.Empty(stream.Answer);
        Assert.Null(stream.Extraction);
        Assert.Throws<InvalidDataException>(stream.Complete);
    }

    [Fact]
    public void DelimiterInsideJsonStringIsNotTreatedAsAnswerBoundary()
    {
        var stream = new QuestionAnswerStream();
        stream.Push(Response("{\"questions\":[\"字符串 [EARCUE_ANSWER] 如何转义？\"],\"uncertain\":true}"));
        stream.Complete();
        Assert.Contains("[EARCUE_ANSWER]", stream.Extraction!.Text);
        Assert.True(stream.Extraction.Uncertain);
        Assert.Equal("先说明结论，再解释依据。", stream.Answer);
    }

    [Fact]
    public void PlainAnswerFallsBackWithoutLosingText()
    {
        var stream = new QuestionAnswerStream();
        stream.Push("普通回答，"); stream.Push("继续显示。"); stream.Complete();
        Assert.Equal("普通回答，继续显示。", stream.Answer);
        Assert.Null(stream.Extraction);
        Assert.True(stream.UsedFallback);
    }

    [Fact]
    public void FencedStructuredResponseIsAcceptedWithoutExposingMetadata()
    {
        var text = "```text\n" + Response("{\"questions\":[\"如何上线？\"],\"uncertain\":false}") + "\n```";
        var stream = new QuestionAnswerStream();
        foreach (var character in text) stream.Push(character.ToString());
        stream.Complete();
        Assert.Equal("如何上线？", stream.Extraction!.Text);
        Assert.Equal("先说明结论，再解释依据。", stream.Answer);
    }

    [Fact]
    public void PlainCodeAnswerRetainsItsCodeFence()
    {
        var stream = new QuestionAnswerStream();
        const string text = "```csharp\nvar value = 1;\n```";
        foreach (var character in text) stream.Push(character.ToString());
        stream.Complete();
        Assert.Equal(text, stream.Answer);
        Assert.True(stream.UsedFallback);
    }

    [Fact]
    public void ClosingMetadataFenceDoesNotRemoveCodeFenceFromAnswer()
    {
        var stream = new QuestionAnswerStream();
        const string code = "```csharp\nvar value = 1;\n```";
        stream.Push("```json\n" + QuestionAnswerStream.QuestionMarker +
            "\n{\"questions\":[\"代码如何实现？\"],\"uncertain\":false}\n```\n" + QuestionAnswerStream.AnswerMarker + "\n" + code);
        stream.Complete();
        Assert.NotNull(stream.Extraction);
        Assert.Equal(code, stream.Answer);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"questions\":\"错误类型\",\"uncertain\":false}")]
    [InlineData("{\"questions\":[\"问题\"]}")]
    [InlineData("{\"questions\":[123],\"uncertain\":false}")]
    public void InvalidMetadataKeepsAnswerAndReportsOriginalQuestionFallback(string metadata)
    {
        var stream = new QuestionAnswerStream();
        stream.Push(Response(metadata)); stream.Complete();
        Assert.Equal("先说明结论，再解释依据。", stream.Answer);
        Assert.Null(stream.Extraction);
        Assert.True(stream.UsedFallback);
    }

    [Fact]
    public void OrdinaryStatementDoesNotRequireInventingAQuestion()
    {
        var stream = new QuestionAnswerStream();
        stream.Push(Response("{\"questions\":[],\"uncertain\":false}", "谢谢你的补充。")); stream.Complete();
        Assert.Empty(stream.Extraction!.Questions);
        Assert.False(stream.UsedFallback);
        Assert.Equal("谢谢你的补充。", stream.Answer);
    }

    [Fact]
    public void EmptyAnswerAndOversizedHeaderAreFailures()
    {
        var empty = new QuestionAnswerStream();
        empty.Push(Response("{\"questions\":[],\"uncertain\":false}", ""));
        Assert.Throws<InvalidDataException>(empty.Complete);
        var huge = new QuestionAnswerStream();
        Assert.Throws<InvalidDataException>(() => huge.Push(QuestionAnswerStream.QuestionMarker + new string('x', 5000)));
    }

    [Fact]
    public void PendingSpeechMergesEndpointsAndAcknowledgesOnlyCapturedSnapshot()
    {
        var pending = new PendingSpeechBuffer();
        pending.Add("你刚才提到了 Redis");
        pending.Add("它怎么保证一致性");
        var snapshot = pending.Snapshot();
        Assert.Equal("你刚才提到了 Redis\n它怎么保证一致性", snapshot.Text);
        pending.Add("再说一下回滚策略");
        pending.Acknowledge(snapshot);
        Assert.Equal("再说一下回滚策略", pending.Snapshot().Text);
        pending.Acknowledge(snapshot);
        Assert.Equal("再说一下回滚策略".Length, pending.CharacterCount);
    }

    [Fact]
    public void OldSnapshotCannotConsumeSpeechAfterClearAndBufferRemainsBounded()
    {
        var pending = new PendingSpeechBuffer(12);
        pending.Add("旧问题"); var old = pending.Snapshot();
        pending.Clear(); pending.Add("新问题"); pending.Acknowledge(old);
        Assert.Equal("新问题", pending.Snapshot().Text);
        pending.Add(new string('中', 100));
        Assert.InRange(pending.CharacterCount, 1, 12);
        Assert.Equal(12, pending.Snapshot().Text.Length);
    }

    [Fact]
    public void PromptSeparatesCurrentTurnFromOlderContextAndPreservesUncertainty()
    {
        var prompt = new Prompt
        {
            Mode = PromptMode.AutoResponse,
            ExtractAndCorrectQuestion = true,
            TriggerQuestion = "这个为什么不能上线",
            RecentContext = new[] { new DialogueTurn(AudioRole.Remote, "旧问题：自我介绍", DateTime.UtcNow) }
        };
        Assert.Contains("【本轮待处理原文】\n这个为什么不能上线".Replace("\n", Environment.NewLine), prompt.BuildUserMessage());
        Assert.Contains("不重复提取旧问题", prompt.BuildSystemPrompt());
        Assert.Contains("保留原意、否定、条件、数字", prompt.BuildSystemPrompt());
        Assert.Contains(QuestionAnswerStream.QuestionMarker, prompt.BuildSystemPrompt());
        prompt.ExtractAndCorrectQuestion = false;
        Assert.DoesNotContain(QuestionAnswerStream.QuestionMarker, prompt.BuildSystemPrompt());
    }
}
