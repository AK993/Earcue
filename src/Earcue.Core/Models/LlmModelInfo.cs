namespace Earcue.Core.Models;

/// <summary>模型列表接口返回的一条模型信息（对应 OpenAI /v1/models 的 data 数组元素）。</summary>
public sealed class LlmModelInfo
{
    public LlmModelInfo()
    {
    }

    public LlmModelInfo(string id, string? ownedBy = null, DateTimeOffset? createdAt = null)
    {
        Id = id;
        OwnedBy = ownedBy;
        CreatedAt = createdAt;
    }

    /// <summary>模型 ID，调用时填入 llm.model 的值（例如 deepseek-chat）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>提供方 / 归属方，可能为空。</summary>
    public string? OwnedBy { get; init; }

    /// <summary>模型创建时间（服务端返回的 Unix 秒），可能为空。</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    public string OwnerDisplay => string.IsNullOrWhiteSpace(OwnedBy) ? "未知提供方" : OwnedBy!;

    public string CreatedDisplay => CreatedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty;
}
