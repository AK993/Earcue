using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Earcue.Core.Configuration;
using Earcue.Core.Interfaces;
using Earcue.Core.Models;
using Earcue.Core.Logging;

namespace Earcue.Brain.Llm;

/// <summary>OpenAI 兼容的 SSE 客户端；分别限制首字等待和整个请求的时间。</summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    // 客户端随设置重建时复用连接池，不遗留 HttpClient/套接字。
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;
    public string? LastEndpoint { get; private set; }
    public long? LastFirstTextMs { get; private set; }

    public OpenAiCompatibleLlmClient(LlmOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? SharedClient;
    }

    public async IAsyncEnumerable<string> AskAsync(
        Prompt prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!LlmEndpoints.TryNormalizeBaseUrl(_options.BaseUrl, out var baseUrl, out var error))
            throw new InvalidOperationException(error);
        if (string.IsNullOrWhiteSpace(_options.ApiKey) && !LlmEndpoints.IsLoopback(baseUrl))
            throw new InvalidOperationException("未配置模型 API Key，请在设置中填写。");

        var system = prompt.BuildSystemPrompt();
        var user = prompt.BuildUserMessage();
        var requestPayload = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["stream"] = true,
            ["temperature"] = 0.3,
            ["max_tokens"] = Math.Clamp(_options.MaxTokens, 64, 8192),
            ["messages"] = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };
        if (new Uri(baseUrl).Host.Equals("api.xiaomimimo.com", StringComparison.OrdinalIgnoreCase))
        {
            // MiMo counts reasoning and answer tokens together and enables thinking by default.
            requestPayload.Remove("max_tokens");
            requestPayload["max_completion_tokens"] = Math.Clamp(_options.MaxTokens, 64, 8192);
            if (_options.Model.StartsWith("mimo-v2.6-", StringComparison.OrdinalIgnoreCase))
                requestPayload["thinking"] = new { type = "disabled" };
        }
        var json = JsonSerializer.Serialize(requestPayload);
        var watch = Stopwatch.StartNew();
        LastFirstTextMs = null;
        LastEndpoint = null;
        AppLogger.Instance.Information("LLM request: model={Model}, mode={Mode}, extraction={Extraction}, systemChars={SystemChars}, userChars={UserChars}, firstSignalTimeoutMs={FirstTimeout}, totalTimeoutMs={TotalTimeout}",
            _options.Model, prompt.Mode, prompt.ExtractAndCorrectQuestion, system.Length, user.Length, _options.FirstTokenTimeoutMs, _options.TimeoutMs);

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(Math.Max(1, _options.TimeoutMs));
        using var firstToken = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        firstToken.CancelAfter(Math.Max(1, _options.FirstTokenTimeoutMs));

        using var response = await TimedAsync(
            ct => SendAsync(baseUrl, json, ct), firstToken.Token, total.Token, cancellationToken);
        using var stream = await TimedAsync(
            ct => response.Content.ReadAsStreamAsync(ct), firstToken.Token, total.Token, cancellationToken);
        using var reader = new StreamReader(stream);
        var receivedText = false;
        var receivedProgress = false;
        var receivedReasoning = false;
        var outputChars = 0;
        string? finishReason = null;

        try
        {
            while (true)
            {
                // EndOfStream 会同步读取底层网络流；只通过可取消的异步读取判断 EOF。
                var token = receivedProgress ? total.Token : firstToken.Token;
                var line = await TimedAsync(
                    ct => reader.ReadLineAsync(ct).AsTask(), token, total.Token, cancellationToken);
                if (line is null) break;
                line = line.Trim();
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var payload = line[5..].Trim();
                if (payload == "[DONE]") break;

                var chunk = ParseChunk(payload);
                finishReason = chunk.FinishReason ?? finishReason;
                receivedReasoning |= chunk.HasReasoning;
                var text = chunk.Content;
                if (chunk.HasReasoning || !string.IsNullOrWhiteSpace(text))
                {
                    receivedProgress = true;
                    firstToken.CancelAfter(Timeout.Infinite);
                }
                if (string.IsNullOrEmpty(text)) continue;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    receivedText = true;
                    LastFirstTextMs ??= watch.ElapsedMilliseconds;
                }
                outputChars += text.Length;
                yield return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!receivedText) throw new InvalidDataException(receivedReasoning
                ? "模型只返回了思考过程，没有返回回答，请检查输出长度或关闭深度思考。"
                : "模型未返回有效回答，请检查所选模型是否支持聊天补全。");
        }
        finally
        {
            AppLogger.Instance.Information("LLM stream ended: model={Model}, elapsedMs={Elapsed}, firstTextMs={FirstText}, reasoningObserved={Reasoning}, outputChars={Chars}, finishReason={Finish}, canceled={Canceled}",
                _options.Model, watch.ElapsedMilliseconds, LastFirstTextMs, receivedReasoning, outputChars, finishReason, cancellationToken.IsCancellationRequested);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string baseUrl, string json, CancellationToken ct)
    {
        var candidates = LlmEndpoints.ChatCompletionsCandidates(baseUrl);
        for (var i = 0; i < candidates.Count; i++)
        {
            LastEndpoint = candidates[i];
            using var request = new HttpRequestMessage(HttpMethod.Post, candidates[i])
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("User-Agent", "Earcue/0.1.1 LlmClient");
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            if (status == HttpStatusCode.NotFound && i < candidates.Count - 1) continue;

            // 不记录响应正文，避免代理回显请求中的会议文本或凭据。
            var message = status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "模型鉴权失败，请检查 API Key 和模型权限。",
                HttpStatusCode.TooManyRequests => "模型服务限流或余额不足，请稍后重试并检查账户。",
                _ => $"模型请求失败（HTTP {(int)status}），请检查服务地址和所选模型。"
            };
            throw new HttpRequestException(message, null, status);
        }
        throw new HttpRequestException("未找到可用的聊天补全接口。");
    }

    private static async Task<T> TimedAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken token, CancellationToken total, CancellationToken caller)
    {
        try
        {
            return await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new TimeoutException(total.IsCancellationRequested
                ? "模型回答超时，请稍后重试。"
                : "等待模型首字超时，请检查网络或选择响应更快的模型。");
        }
    }

    private readonly record struct StreamChunk(string? Content, bool HasReasoning, string? FinishReason);

    private static StreamChunk ParseChunk(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            if (root.TryGetProperty("error", out _))
                throw new InvalidDataException("模型服务返回错误，请检查模型配置和账户状态。");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                choices[0].ValueKind != JsonValueKind.Object) return default;
            var choice = choices[0];
            var finish = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return new(null, false, finish);
            var text = delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            var reasoning = (delta.TryGetProperty("reasoning_content", out var thought) || delta.TryGetProperty("reasoning", out thought)) &&
                thought.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(thought.GetString());
            // Reasoning signals activity but is never yielded, logged, or shown as an answer.
            return new(text, reasoning, finish);
        }
        catch (JsonException) { /* 兼容心跳和非标准的空包。 */ }
        return default;
    }
}
