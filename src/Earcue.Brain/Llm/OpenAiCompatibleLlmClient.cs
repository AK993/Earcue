using System.Net;
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
/// 支持 SSE 流式传输、总超时与失败降级；本地模型（localhost）允许不填 API Key。
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
        if (!LlmEndpoints.TryNormalizeBaseUrl(_options.BaseUrl, out var baseUrl, out var urlError))
        {
            AppLogger.Instance.Warning("LLM Base URL 无效: {Error}", urlError);
            yield break;
        }

        var isLoopback = LlmEndpoints.IsLoopback(baseUrl);
        if (string.IsNullOrWhiteSpace(_options.ApiKey) && !isLoopback)
        {
            AppLogger.Instance.Warning("未配置 LLM API Key，无法调用云端模型；请在【模型设置】中填写");
            yield break;
        }

        var requestBody = new
        {
            model = _options.Model,
            stream = true,
            temperature = 0.3,
            max_tokens = Math.Clamp(_options.MaxTokens, 64, 8192),
            messages = new[]
            {
                new { role = "system", content = prompt.BuildSystemPrompt() },
                new { role = "user", content = prompt.BuildUserMessage() }
            }
        };

        var requestJson = JsonSerializer.Serialize(requestBody);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_options.TimeoutMs);

        var candidates = LlmEndpoints.ChatCompletionsCandidates(baseUrl);
        HttpResponseMessage? response = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var url = candidates[i];
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("User-Agent", "Earcue/0.1 LlmClient");
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
            }

            try
            {
                var candidateResponse = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token);

                if (!candidateResponse.IsSuccessStatusCode)
                {
                    var errorBody = await TryReadBodyAsync(candidateResponse, linkedCts.Token);

                    // 裸域名首选 /v1/chat/completions，DeepSeek 等实现只支持 /chat/completions
                    if (candidateResponse.StatusCode == HttpStatusCode.NotFound && i < candidates.Count - 1)
                    {
                        candidateResponse.Dispose();
                        continue;
                    }

                    AppLogger.Instance.Warning(
                        "LLM 请求失败: host={Host}, status={StatusCode}, detail={Detail}",
                        LlmEndpoints.DescribeHost(url),
                        (int)candidateResponse.StatusCode,
                        Truncate(errorBody));
                    candidateResponse.Dispose();
                    yield break;
                }

                response = candidateResponse;
                break;
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                AppLogger.Instance.Warning("LLM 请求超时或被取消");
                yield break;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warning(ex, "请求 LLM 发生网络异常");
                yield break;
            }
        }

        if (response is null)
        {
            yield break;
        }

        using (response)
        {
            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warning(ex, "读取 LLM 响应流失败");
                yield break;
            }

            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !linkedCts.Token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    AppLogger.Instance.Warning("LLM 流式读取超时");
                    yield break;
                }

                if (line == null)
                {
                    continue;
                }

                line = line.Trim();
                if (line.Length == 0 || line.StartsWith(':'))
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = line[5..].Trim();
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
                    // 忽略无法解析的心跳/非标准包
                }

                if (!string.IsNullOrEmpty(chunkText))
                {
                    yield return chunkText;
                }
            }
        }
    }

    private static async Task<string> TryReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine[..300] + "…";
    }
}
