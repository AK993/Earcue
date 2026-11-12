using System.Diagnostics.CodeAnalysis;

namespace Earcue.Brain.Llm;

/// <summary>
/// OpenAI 兼容接口的地址解析规则，集中处理用户在设置页可能填出的各种 Base URL：
/// 结尾斜杠、误填 /chat/completions、不带 /v1 的裸域名等。
/// </summary>
public static class LlmEndpoints
{
    private static readonly string[] KnownSuffixes = { "/chat/completions", "/completions", "/models" };

    /// <summary>规范化 Base URL；失败时返回 false 并给出中文错误。</summary>
    public static bool TryNormalizeBaseUrl(
        string? raw,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        error = null;

        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "请填写 Base URL（例如 https://api.deepseek.com/v1）";
            return false;
        }

        // 允许只填域名，默认补 https
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = $"Base URL 格式不正确：{raw}";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Base URL 仅支持 http / https 协议";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "Base URL 缺少主机名";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in KnownSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        normalized = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (normalized.Length == 0)
        {
            error = "Base URL 格式不正确";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 聊天补全接口的候选地址。裸域名优先尝试 /v1/chat/completions（OpenAI 规范），
    /// 再退回 /chat/completions（DeepSeek 等无版本前缀的实现）。
    /// </summary>
    public static IReadOnlyList<string> ChatCompletionsCandidates(string normalizedBaseUrl)
    {
        var list = new List<string>();
        if (HasPath(normalizedBaseUrl))
        {
            list.Add(normalizedBaseUrl + "/chat/completions");
        }
        else
        {
            list.Add(normalizedBaseUrl + "/v1/chat/completions");
            list.Add(normalizedBaseUrl + "/chat/completions");
        }

        return list;
    }

    /// <summary>
    /// 模型列表接口的候选地址。带路径的 Base URL 只试 {base}/models；
    /// 裸域名先试 /v1/models，再试 /models。
    /// </summary>
    public static IReadOnlyList<string> ModelsUrlCandidates(string normalizedBaseUrl)
    {
        var list = new List<string>();
        if (HasPath(normalizedBaseUrl))
        {
            list.Add(normalizedBaseUrl + "/models");
        }
        else
        {
            list.Add(normalizedBaseUrl + "/v1/models");
            list.Add(normalizedBaseUrl + "/models");
        }

        return list;
    }

    /// <summary>是否为本机地址（本地模型通常允许空 API Key）。</summary>
    public static bool IsLoopback(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || host == "127.0.0.1"
               || host == "::1"
               || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从完整 URL 中取出 host，用于日志脱敏展示。</summary>
    public static string DescribeHost(string? url)
    {
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
            : "(无效地址)";
    }

    private static bool HasPath(string normalizedBaseUrl)
    {
        return Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var uri)
               && uri.AbsolutePath.Trim('/').Length > 0;
    }
}
