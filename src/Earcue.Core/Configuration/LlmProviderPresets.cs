namespace Earcue.Core.Configuration;

/// <summary>
/// 一个 OpenAI 兼容服务商的预设：只需选服务商并填 Key，Base URL 与常见模型自动带出。
/// 预设里的模型只是"建议值"，真实可用模型以 /models 接口返回为准，也允许手动输入任意模型 ID。
/// </summary>
public sealed class LlmProviderPreset
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>OpenAI 兼容 Base URL（含 /v1 等版本前缀）。</summary>
    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<string> SuggestedModels { get; init; } = Array.Empty<string>();

    /// <summary>申请 API Key 的控制台地址。</summary>
    public string KeyUrl { get; init; } = string.Empty;

    /// <summary>Key 格式提示 / 是否需要 Key。</summary>
    public string KeyHint { get; init; } = string.Empty;

    /// <summary>该服务商的额外说明（例如模型字段是接入点 ID）。</summary>
    public string Note { get; init; } = string.Empty;

    public bool RequiresApiKey { get; init; } = true;
}

/// <summary>主流 OpenAI 兼容 LLM 服务商预设。</summary>
public static class LlmProviderPresets
{
    public static IReadOnlyList<LlmProviderPreset> All { get; } = new List<LlmProviderPreset>
    {
        new()
        {
            Id = "deepseek",
            DisplayName = "DeepSeek 深度求索",
            BaseUrl = "https://api.deepseek.com/v1",
            SuggestedModels = new[] { "deepseek-chat", "deepseek-reasoner" },
            KeyUrl = "https://platform.deepseek.com/api_keys",
            KeyHint = "以 sk- 开头的 Key",
            Note = "deepseek-chat 适合会议速答；deepseek-reasoner 响应更慢，不建议用于实时提词。"
        },
        new()
        {
            Id = "openai",
            DisplayName = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            SuggestedModels = new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1-mini", "gpt-4.1" },
            KeyUrl = "https://platform.openai.com/api-keys",
            KeyHint = "以 sk- 开头的 Key",
            Note = "国内直连可能不稳定，可配合自建反向代理使用（Base URL 填代理地址）。"
        },
        new()
        {
            Id = "moonshot",
            DisplayName = "月之暗面 Kimi",
            BaseUrl = "https://api.moonshot.cn/v1",
            SuggestedModels = new[] { "kimi-latest", "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k" },
            KeyUrl = "https://platform.moonshot.cn/console/api-keys",
            KeyHint = "以 sk- 开头的 Key",
            Note = "中文口语表达自然，适合中文会议场景。"
        },
        new()
        {
            Id = "dashscope",
            DisplayName = "阿里云百炼 Qwen",
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            SuggestedModels = new[] { "qwen-plus", "qwen-turbo", "qwen-max", "qwen-long" },
            KeyUrl = "https://bailian.console.aliyun.com/?tab=model#/api-key",
            KeyHint = "以 sk- 开头的 DashScope Key",
            Note = "使用百炼的 OpenAI 兼容模式地址；模型名可填 qwen-plus 等。"
        },
        new()
        {
            Id = "zhipu",
            DisplayName = "智谱 GLM",
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            SuggestedModels = new[] { "glm-4-flash", "glm-4-air", "glm-4-plus" },
            KeyUrl = "https://open.bigmodel.cn/usercenter/apikeys",
            KeyHint = "控制台生成的 API Key",
            Note = "glm-4-flash 免费额度较多，适合先跑通链路。"
        },
        new()
        {
            Id = "siliconflow",
            DisplayName = "硅基流动 SiliconFlow",
            BaseUrl = "https://api.siliconflow.cn/v1",
            SuggestedModels = new[] { "Qwen/Qwen2.5-7B-Instruct", "deepseek-ai/DeepSeek-V3", "THUDM/glm-4-9b-chat" },
            KeyUrl = "https://cloud.siliconflow.cn/account/ak",
            KeyHint = "以 sk- 开头的 Key",
            Note = "一个 Key 可调用多家开源模型，模型 ID 带斜杠，例如 Qwen/Qwen2.5-7B-Instruct。"
        },
        new()
        {
            Id = "volcengine",
            DisplayName = "火山方舟 豆包",
            BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
            SuggestedModels = Array.Empty<string>(),
            KeyUrl = "https://console.volcengine.com/ark",
            KeyHint = "方舟 API Key",
            Note = "方舟的 model 字段需要填推理接入点 ID（ep- 开头），请先在控制台创建接入点。"
        },
        new()
        {
            Id = "openrouter",
            DisplayName = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            SuggestedModels = new[] { "openai/gpt-4o-mini", "anthropic/claude-3.5-sonnet", "google/gemini-2.0-flash-001" },
            KeyUrl = "https://openrouter.ai/keys",
            KeyHint = "以 sk-or- 开头的 Key",
            Note = "聚合多家模型，模型 ID 形如 openai/gpt-4o-mini。"
        },
        new()
        {
            Id = "ollama",
            DisplayName = "Ollama 本地模型",
            BaseUrl = "http://localhost:11434/v1",
            SuggestedModels = new[] { "qwen2.5:7b", "llama3.1:8b", "deepseek-r1:7b" },
            KeyUrl = "https://ollama.com/download",
            KeyHint = "本地服务通常无需 Key，可留空",
            Note = "完全离线、不出本机；需要先 ollama pull 对应模型，模型名必须与 ollama list 一致。",
            RequiresApiKey = false
        },
        new()
        {
            Id = "lmstudio",
            DisplayName = "LM Studio 本地模型",
            BaseUrl = "http://localhost:1234/v1",
            SuggestedModels = Array.Empty<string>(),
            KeyUrl = "https://lmstudio.ai/",
            KeyHint = "本地服务通常无需 Key，可留空",
            Note = "在 LM Studio 中启动 Local Server 后使用，模型 ID 以本地加载的模型为准。",
            RequiresApiKey = false
        },
        new()
        {
            Id = "custom",
            DisplayName = "自定义 OpenAI 兼容接口",
            BaseUrl = string.Empty,
            SuggestedModels = Array.Empty<string>(),
            KeyUrl = string.Empty,
            KeyHint = "按接口要求填写，可留空",
            Note = "填写完整 Base URL（通常以 /v1 结尾），然后点【获取可用模型】自动发现模型。",
            RequiresApiKey = true
        }
    };

    public static LlmProviderPreset Custom { get; } = All[^1];

    /// <summary>按 Id 查找预设；找不到返回 null。</summary>
    public static LlmProviderPreset? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        foreach (var preset in All)
        {
            if (string.Equals(preset.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>按 Base URL 反查预设（忽略大小写与结尾斜杠），用于打开设置页时回显服务商。</summary>
    public static LlmProviderPreset? FindByBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var normalized = baseUrl.Trim().TrimEnd('/');
        foreach (var preset in All)
        {
            if (preset.BaseUrl.Length > 0 &&
                string.Equals(preset.BaseUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// 结合保存的预设 Id 与实际 Base URL 判断界面应显示哪个服务商。
    /// 用户把 Base URL 改成了自定义代理或本地地址时，返回"自定义接口"而不是误导性的预设名。
    /// </summary>
    public static LlmProviderPreset ResolveDisplay(string? providerId, string? baseUrl)
    {
        var byUrl = FindByBaseUrl(baseUrl);
        if (byUrl is not null)
        {
            return byUrl;
        }

        var byId = FindById(providerId);
        if (byId is not null && byId.Id != Custom.Id)
        {
            var configured = byId.BaseUrl.TrimEnd('/');
            var current = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (configured.Length > 0 &&
                string.Equals(configured, current, StringComparison.OrdinalIgnoreCase))
            {
                return byId;
            }
        }

        return Custom;
    }
}
