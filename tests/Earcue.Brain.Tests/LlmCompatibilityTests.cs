using System.Net;
using System.Text;
using System.Text.Json;
using Earcue.Brain.Llm;
using Earcue.Core.Configuration;
using Earcue.Core.Models;

namespace Earcue.Brain.Tests;

public class LlmCompatibilityTests
{
    static string Data(string delta) => "data: {\"choices\":[{\"delta\":" + delta + "}]}\n\n";
    static async Task<string> Read(OpenAiCompatibleLlmClient client)
    {
        var text = new StringBuilder();
        await foreach (var part in client.AskAsync(new Prompt())) text.Append(part);
        return text.ToString();
    }

    [Fact]
    public async Task ThinkingProgressPreventsFalseFirstTokenTimeoutWithoutExposingReasoning()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TwoStageStream(Data("{\"reasoning_content\":\"不可显示的内部推理\"}"),
                Data("{\"content\":\"正式回答\"}") + "data: [DONE]\n", 120))
        }));
        var options = new LlmOptions { BaseUrl = "http://localhost:9999", FirstTokenTimeoutMs = 40, TimeoutMs = 2000 };
        var client = new OpenAiCompatibleLlmClient(options, http);
        Assert.Equal("正式回答", await Read(client));
        Assert.True(client.LastFirstTextMs >= 100);
    }

    [Fact]
    public async Task ThinkingProgressStillRespectsTheOverallDeadline()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TwoStageStream(Data("{\"reasoning_content\":\"processing\"}"), "", Timeout.Infinite))
        }));
        var options = new LlmOptions { BaseUrl = "http://localhost:9999", FirstTokenTimeoutMs = 40, TimeoutMs = 160 };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Read(new(options, http)));
        Assert.DoesNotContain("首字", error.Message);
    }

    [Fact]
    public async Task EmptyHeartbeatsDoNotCancelFirstSignalDeadline()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TwoStageStream(Data("{\"reasoning_content\":\"\"}"), "", Timeout.Infinite))
        }));
        var options = new LlmOptions { BaseUrl = "http://localhost:9999", FirstTokenTimeoutMs = 40, TimeoutMs = 2000 };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Read(new(options, http)));
        Assert.Contains("首字", error.Message);
    }

    [Fact]
    public async Task ReasoningOnlyResponseIsReportedInsteadOfDisplayedAsAnAnswer()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(Data("{\"reasoning_content\":\"不可显示\"}") + "data: [DONE]\n")
        }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Read(new(new LlmOptions { BaseUrl = "http://localhost:9999" }, http)));
        Assert.Contains("思考过程", error.Message);
    }

    [Fact]
    public async Task MiMoRequestsUseNonthinkingAndOfficialTokenLimitForProductionAndProbe()
    {
        var bodies = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new StringContent(Data("{\"content\":\"连接正常\"}") + "data: [DONE]\n") };
        }));
        const string endpoint = "https://api.xiaomimimo.com/v1";
        var options = new LlmOptions { BaseUrl = endpoint, Model = "mimo-v2.6-flash", ApiKey = "unit-test-key" };
        Assert.Equal("连接正常", await Read(new(options, http)));
        using var catalog = new OpenAiCompatibleModelCatalog(http);
        Assert.True((await catalog.TestChatAsync(endpoint, "unit-test-key", options.Model)).IsSuccess);
        Assert.Equal(2, bodies.Count);
        foreach (var body in bodies)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("disabled", json.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.True(json.RootElement.TryGetProperty("max_completion_tokens", out _));
            Assert.False(json.RootElement.TryGetProperty("max_tokens", out _));
        }
    }

    [Fact]
    public async Task OtherCompatibleServicesDoNotReceiveMiMoSpecificFields()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK) { Content = new StringContent(Data("{\"content\":\"ok\"}") + "data: [DONE]\n") };
        }));
        await Read(new(new LlmOptions { BaseUrl = "http://localhost:9999", Model = "mimo-v2.6-flash" }, http));
        using var json = JsonDocument.Parse(body!);
        Assert.True(json.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(json.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void ProbePromptNeverIncludesProfileOrMeetingContext()
    {
        var prompt = new Prompt { Mode = PromptMode.ConnectionTest, ResumeText = "private-resume-marker",
            RecentContext = new[] { new DialogueTurn(AudioRole.Remote, "private-meeting-marker", DateTime.UtcNow) } };
        Assert.DoesNotContain("private-", prompt.BuildSystemPrompt() + prompt.BuildUserMessage());
    }

    sealed class Handler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = request => Task.FromResult(respond(request));
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => this.respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    sealed class TwoStageStream(string first, string second, int delay) : Stream
    {
        int stage;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (stage == 0) { stage++; var bytes = Encoding.UTF8.GetBytes(first); bytes.CopyTo(buffer); return bytes.Length; }
            if (stage == 1) { stage++; await Task.Delay(delay, ct); var bytes = Encoding.UTF8.GetBytes(second); bytes.CopyTo(buffer); return bytes.Length; }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new Exception("sync read");
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
