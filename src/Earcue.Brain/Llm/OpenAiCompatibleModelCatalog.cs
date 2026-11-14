using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Earcue.Core.Logging;
using Earcue.Core.Models;

namespace Earcue.Brain.Llm;

/// <summary>
/// 通过 OpenAI 兼容的 GET /models 接口，查询某个 API Key 实际可以调用哪些模型；
/// 并使用正式回答相同的流式请求验证所选模型是否可用。
/// </summary>
public sealed class OpenAiCompatibleModelCatalog : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public OpenAiCompatibleModelCatalog(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
    }

    /// <summary>查询模型列表。不会抛出网络异常，失败信息都在返回值里。</summary>
    public async Task<LlmModelListResult> ListModelsAsync(
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!LlmEndpoints.TryNormalizeBaseUrl(baseUrl, out var normalized, out var urlError))
        {
            return Fail(urlError, stopwatch);
        }

        var candidates = LlmEndpoints.ModelsUrlCandidates(normalized);
        string? lastError = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var url = candidates[i];
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("User-Agent", "Earcue/0.1 ModelCatalog");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var detail = ExtractApiError(body);
                    lastError = DescribeHttpStatus(response.StatusCode, detail);

                    // 裸域名可能落在错误的前缀上（例如服务只支持 /models 不支持 /v1/models），换下一个候选
                    if (response.StatusCode == HttpStatusCode.NotFound && i < candidates.Count - 1)
                    {
                        continue;
                    }

                    AppLogger.Instance.Warning(
                        "模型列表获取失败: host={Host}, status={Status}",
                        LlmEndpoints.DescribeHost(url),
                        (int)response.StatusCode);
                    return Fail(lastError, stopwatch, url);
                }

                var parsed = ParseModels(body);
                if (parsed.Error is not null)
                {
                    return Fail(parsed.Error, stopwatch, url);
                }

                var models = parsed.Models;
                var message = models.Count == 0
                    ? $"接口连通，但该 Key 下没有可用模型（0 个，{LlmEndpoints.DescribeHost(url)}）"
                    : $"连接成功，该 Key 可调用 {models.Count} 个模型";

                AppLogger.Instance.Information(
                    "模型列表获取成功: host={Host}, count={Count}",
                    LlmEndpoints.DescribeHost(url),
                    models.Count);

                return new LlmModelListResult
                {
                    IsSuccess = true,
                    Message = message,
                    Models = models,
                    Endpoint = url,
                    Elapsed = stopwatch.Elapsed
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail($"请求超时（{RequestTimeout.TotalSeconds:0} 秒），请检查网络或代理设置", stopwatch, url);
            }
            catch (HttpRequestException ex)
            {
                return Fail($"网络请求失败：{ex.Message}", stopwatch, url);
            }
            catch (Exception ex)
            {
                return Fail($"请求异常：{ex.Message}", stopwatch, url);
            }
        }

        return Fail(lastError ?? "无法访问模型列表接口", stopwatch, candidates[^1]);
    }

    /// <summary>使用与正式回答完全相同的流式客户端验证模型，不发送个人资料。</summary>
    public async Task<LlmProbeResult> TestChatAsync(
        string? baseUrl,
        string? apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!LlmEndpoints.TryNormalizeBaseUrl(baseUrl, out var normalized, out var urlError))
            return ProbeFail(urlError, stopwatch);
        if (string.IsNullOrWhiteSpace(model)) return ProbeFail("请先选择或输入模型 ID", stopwatch);
        var client = new OpenAiCompatibleLlmClient(new Earcue.Core.Configuration.LlmOptions
        {
            BaseUrl = normalized, ApiKey = apiKey ?? string.Empty, Model = model.Trim(), MaxTokens = 64
        }, _httpClient);
        try
        {
            var reply = new StringBuilder();
            await foreach (var part in client.AskAsync(new Prompt { Mode = PromptMode.ConnectionTest }, cancellationToken))
                reply.Append(part);
            return new LlmProbeResult
            {
                IsSuccess = true, Message = "流式模型调用成功", Reply = reply.ToString().Trim(),
                LatencyMs = stopwatch.ElapsedMilliseconds, FirstTextMs = client.LastFirstTextMs,
                Endpoint = client.LastEndpoint
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException error)
        {
            var status = error.StatusCode is { } code ? $"（HTTP {(int)code}）" : "";
            return ProbeFail($"流式请求失败{status}：{error.Message}", stopwatch, client.LastEndpoint);
        }
        catch (Exception error)
        {
            return ProbeFail(error.Message, stopwatch, client.LastEndpoint);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static LlmModelListResult Fail(string message, Stopwatch stopwatch, string? endpoint = null) => new()
    {
        IsSuccess = false,
        Message = message,
        Models = Array.Empty<LlmModelInfo>(),
        Endpoint = endpoint,
        Elapsed = stopwatch.Elapsed
    };

    private static LlmProbeResult ProbeFail(string message, Stopwatch stopwatch, string? endpoint = null) => new()
    {
        IsSuccess = false,
        Message = message,
        Endpoint = endpoint,
        LatencyMs = stopwatch.ElapsedMilliseconds
    };

    private static (List<LlmModelInfo> Models, string? Error) ParseModels(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (new List<LlmModelInfo>(), "服务返回了空响应");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (new List<LlmModelInfo>(), "服务返回的不是合法 JSON，请确认该地址是 OpenAI 兼容接口");
        }

        using (document)
        {
            var root = document.RootElement;
            JsonElement array = default;
            var found = false;

            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
                found = true;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // 绝大多数实现用 data，少量实现用 models / result
                foreach (var name in new[] { "data", "models", "result" })
                {
                    if (TryGetPropertyIgnoreCase(root, name, out var element) &&
                        element.ValueKind == JsonValueKind.Array)
                    {
                        array = element;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    var apiError = ExtractApiError(body);
                    return (new List<LlmModelInfo>(),
                        apiError is null
                            ? "响应中未找到模型列表（缺少 data 数组），请确认服务支持 /models 接口"
                            : $"服务返回错误：{apiError}");
                }
            }
            else
            {
                return (new List<LlmModelInfo>(), "响应格式无法识别");
            }

            var models = new List<LlmModelInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in array.EnumerateArray())
            {
                string? id = null;
                string? ownedBy = null;
                DateTimeOffset? createdAt = null;

                if (item.ValueKind == JsonValueKind.String)
                {
                    id = item.GetString();
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyIgnoreCase(item, "id", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String)
                    {
                        id = idElement.GetString();
                    }

                    if (TryGetPropertyIgnoreCase(item, "owned_by", out var ownerElement) &&
                        ownerElement.ValueKind == JsonValueKind.String)
                    {
                        ownedBy = ownerElement.GetString();
                    }

                    if (TryGetPropertyIgnoreCase(item, "created", out var createdElement) &&
                        createdElement.ValueKind == JsonValueKind.Number &&
                        createdElement.TryGetInt64(out var epoch) &&
                        epoch > 0 && epoch < 4102444800)
                    {
                        createdAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
                    }
                }

                id = id?.Trim();
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                {
                    continue;
                }

                models.Add(new LlmModelInfo(id, ownedBy, createdAt));
            }

            models.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            return (models, null);
        }
    }

    private static string DescribeHttpStatus(HttpStatusCode status, string? detail)
    {
        var code = (int)status;
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "API Key 无效或已过期（401），请确认 Key 属于当前服务商",
            HttpStatusCode.Forbidden => "该 API Key 无权访问该接口（403）",
            HttpStatusCode.NotFound => "该地址不提供此接口（404），可尝试手动填写模型名",
            HttpStatusCode.TooManyRequests => "请求过于频繁或额度不足（429），请稍后再试",
            _ when code >= 500 => $"服务端错误（{code}），请稍后重试",
            _ => $"请求失败（{code}）"
        };

        return string.IsNullOrWhiteSpace(detail) ? message : $"{message}：{detail}";
    }

    private static string? ExtractApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(root, "error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(error.GetString());
                    }

                    if (error.ValueKind == JsonValueKind.Object &&
                        TryGetPropertyIgnoreCase(error, "message", out var message) &&
                        message.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(message.GetString());
                    }
                }

                foreach (var name in new[] { "message", "detail", "msg", "error_msg" })
                {
                    if (TryGetPropertyIgnoreCase(root, name, out var value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(value.GetString());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON（例如网关 HTML 错误页），截取正文给用户
        }

        return Truncate(body);
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= 240 ? singleLine : singleLine[..240] + "…";
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
