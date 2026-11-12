using System.Text.Json;
using System.Text.Json.Serialization;

namespace Earcue.Core.Configuration;

public sealed class EarcueConfig
{
    [JsonPropertyName("asr")]
    public AsrOptions Asr { get; set; } = new();

    [JsonPropertyName("segment")]
    public SegmentOptions Segment { get; set; } = new();

    [JsonPropertyName("trigger")]
    public TriggerOptions Trigger { get; set; } = new();

    [JsonPropertyName("llm")]
    public LlmOptions Llm { get; set; } = new();

    [JsonPropertyName("overlay")]
    public OverlayOptions Overlay { get; set; } = new();

    [JsonPropertyName("mic")]
    public MicOptions Mic { get; set; } = new();

    [JsonPropertyName("privacy")]
    public PrivacyOptions Privacy { get; set; } = new();

    public static EarcueConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return new EarcueConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EarcueConfig>(json) ?? new EarcueConfig();
    }

    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

public sealed class AsrOptions
{
    [JsonPropertyName("loopbackModel")]
    public string LoopbackModel { get; set; } = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";

    [JsonPropertyName("micModel")]
    public string MicModel { get; set; } = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";

    [JsonPropertyName("hotwords")]
    public List<string> Hotwords { get; set; } = new();
}

public sealed class SegmentOptions
{
    [JsonPropertyName("silenceMs")]
    public int SilenceMs { get; set; } = 600;

    [JsonPropertyName("maxMs")]
    public int MaxMs { get; set; } = 10000;
}

public sealed class TriggerOptions
{
    [JsonPropertyName("cooldownMs")]
    public int CooldownMs { get; set; } = 8000;

    [JsonPropertyName("minIntervalMs")]
    public int MinIntervalMs { get; set; } = 5000;

    [JsonPropertyName("minChars")]
    public int MinChars { get; set; } = 5;
}

public sealed class LlmOptions
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "OpenAI-Compatible";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    [JsonPropertyName("maxChars")]
    public int MaxChars { get; set; } = 40;

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 20000;

    [JsonPropertyName("firstTokenTimeoutMs")]
    public int FirstTokenTimeoutMs { get; set; } = 5000;

    [JsonPropertyName("userRoleDescription")]
    public string UserRoleDescription { get; set; } = "技术研发负责人";

    [JsonPropertyName("userStance")]
    public string UserStance { get; set; } = string.Empty;
}

public sealed class OverlayOptions
{
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.92;

    [JsonPropertyName("positionX")]
    public double? PositionX { get; set; }

    [JsonPropertyName("positionY")]
    public double? PositionY { get; set; }

    [JsonPropertyName("excludeFromCapture")]
    public bool ExcludeFromCapture { get; set; } = true;

    [JsonPropertyName("autoFadeoutSeconds")]
    public int AutoFadeoutSeconds { get; set; } = 20;
}

public sealed class MicOptions
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("echoGate")]
    public bool EchoGate { get; set; } = true;
}

public sealed class PrivacyOptions
{
    [JsonPropertyName("saveTranscript")]
    public bool SaveTranscript { get; set; } = false;

    [JsonPropertyName("diagnosticMode")]
    public bool DiagnosticMode { get; set; } = false;
}
