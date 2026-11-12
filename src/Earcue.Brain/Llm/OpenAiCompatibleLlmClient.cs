using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Earcue.Core.Configuration;
using Earcue.Core.Interfaces;
using Earcue.Core.Logging;
using Earcue.Core.Models;

namespace Earcue.Brain.Llm;

/// <summary>
/// OpenAI 兼容的流式 LLM 客户端
/// 支持 SSE 流式传输、首 token 超时、总超时与失败降级
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;

    public OpenAiCompatibleLlmClient(LlmOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async IAsyncEnumerable<string> AskAsync(
        Prompt prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            AppLogger.Instance.Warning("未配置 LLM API Key，无法调用 LLM 回答");
            yield break;
        }

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var requestUrl = $"{baseUrl}/chat/completions";

        var requestBody = new
        {
            model = _options.Model,
            stream = true,
            temperature = 0.3,
            max_tokens = 150,
            messages = new[]
            {
                new { role = "system", content = prompt.BuildSystemPrompt() },
                new { role = "user", content = prompt.BuildUserMessage() }
            }
        };

        var requestJson = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_options.TimeoutMs);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Instance.Warning("LLM 请求失败: {StatusCode} {Reason}", response.StatusCode, response.ReasonPhrase);
                yield break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "请求 LLM 发生网络异常");
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !linkedCts.Token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(linkedCts.Token);
            if (line == null) continue;

            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(":")) continue;

            if (line.StartsWith("data: "))
            {
                var payload = line.Substring(6).Trim();
                if (payload == "[DONE]")
                {
                    break;
                }

                string? chunkText = null;
                try
                {
                    var node = JsonNode.Parse(payload);
                    var delta = node?["choices"]?[0]?["delta"];
                    chunkText = delta?["content"]?.GetValue<string>();
                }
                catch
                {
                    // 容错处理
                }

                if (!string.IsNullOrEmpty(chunkText))
                {
                    yield return chunkText;
                }
            }
        }
    }
}
