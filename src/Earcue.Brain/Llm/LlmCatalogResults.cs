using Earcue.Core.Models;

namespace Earcue.Brain.Llm;

/// <summary>获取模型列表的结果。</summary>
public sealed class LlmModelListResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<LlmModelInfo> Models { get; init; } = Array.Empty<LlmModelInfo>();

    /// <summary>实际请求成功的地址（便于用户确认 Base URL 是否正确）。</summary>
    public string? Endpoint { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>测试某个模型是否可用的结果。</summary>
public sealed class LlmProbeResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Reply { get; init; } = string.Empty;

    public long LatencyMs { get; init; }
    public long? FirstTextMs { get; init; }

    public string? Endpoint { get; init; }
}
