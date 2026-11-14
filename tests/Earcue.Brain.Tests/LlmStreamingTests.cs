using System.Net;
using System.Text;
using Earcue.Brain.Llm;
using Earcue.Core.Configuration;
using Earcue.Core.Models;

namespace Earcue.Brain.Tests;

public class LlmStreamingTests
{
    private static LlmOptions Options() => new()
    {
        BaseUrl = "http://localhost:9999", TimeoutMs = 2000, FirstTokenTimeoutMs = 1000
    };

    private static async Task<string> Collect(OpenAiCompatibleLlmClient client, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        await foreach (var chunk in client.AskAsync(new Prompt(), ct)) text.Append(chunk);
        return text.ToString();
    }

    [Fact]
    public async Task Streaming_HandlesHeartbeatsContentAnd404Fallback()
    {
        var urls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            urls.Add(request.RequestUri!.AbsolutePath);
            return urls.Count == 1 ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK)
            {
                Content = new StringContent(": heartbeat\n\ndata: {}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\ndata: [DONE]\n")
            };
        }));

        Assert.Equal("你好", await Collect(new(Options(), http)));
        Assert.Equal(new[] { "/v1/chat/completions", "/chat/completions" }, urls);
    }

    [Fact]
    public async Task HttpFailure_IsReportedInsteadOfReturningEmptySuccess()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Collect(new(Options(), http)));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("API Key", error.Message);
    }

    [Fact]
    public async Task FirstTokenTimeout_DoesNotUseSynchronousReads()
    {
        using var http = StreamClient();
        var options = Options();
        options.FirstTokenTimeoutMs = 50;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Collect(new(options, http)));
        Assert.Contains("首字", error.Message);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesDuringStreamRead()
    {
        using var http = StreamClient();
        using var cts = new CancellationTokenSource();
        var request = Collect(new(Options(), http), cts.Token);
        Assert.False(request.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task FirstTokenDeadline_IsRemovedAfterContentButTotalDeadlineRemains()
    {
        using var http = StreamClient("data: {\"choices\":[{\"delta\":{\"content\":\"首字\"}}]}\n");
        var options = Options();
        options.TimeoutMs = 200;
        options.FirstTokenTimeoutMs = 50;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Collect(new(options, http)));
        Assert.DoesNotContain("首字", error.Message);
    }

    [Fact]
    public async Task EmptyResponse_IsReportedAsFailure()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n")
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new(Options(), http)));
    }

    private static HttpClient StreamClient(string? first = null) => new(new Handler(_ => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new AsyncOnlyStream(first))
    }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class AsyncOnlyStream(string? first) : Stream
    {
        private byte[]? _first = first is null ? null : Encoding.UTF8.GetBytes(first);
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("禁止同步网络读取");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_first is { } bytes)
            {
                bytes.CopyTo(buffer); _first = null; return bytes.Length;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
