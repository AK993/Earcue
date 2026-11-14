using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Earcue.Core.Models;
using Earcue.Core.Security;

namespace Earcue.Core.Configuration;

public sealed class EarcueConfig
{
    [JsonPropertyName("asr")]
    public AsrOptions Asr { get; set; } = new();

    [JsonPropertyName("segment")]
    public SegmentOptions Segment { get; set; } = new();

    [JsonPropertyName("trigger")]
    public TriggerOptions Trigger { get; set; } = new();

    [JsonPropertyName("autoAnswer")]
    public AutoAnswerOptions AutoAnswer { get; set; } = new();

    [JsonPropertyName("profile")]
    public ProfileOptions Profile { get; set; } = new();

    [JsonPropertyName("llm")]
    public LlmOptions Llm { get; set; } = new();

    [JsonPropertyName("overlay")]
    public OverlayOptions Overlay { get; set; } = new();

    [JsonPropertyName("mic")]
    public MicOptions Mic { get; set; } = new();

    [JsonPropertyName("privacy")]
    public PrivacyOptions Privacy { get; set; } = new();

    private static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        // 让中文说明在 config.json 里保持可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static EarcueConfig LoadFromFile(string path) => LoadFromFile(path, out _);

    public static EarcueConfig LoadFromFile(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
            return new EarcueConfig();

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<EarcueConfig>(json, LoadOptions) ?? new EarcueConfig();
            config.Normalize();
            return config;
        }
        catch (JsonException)
        {
            var backup = path + ".invalid-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(path, backup);
                warning = $"配置格式无效，已使用默认设置。原文件已备份到 {backup}，请重新检查模型设置。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = "配置格式无效，已使用默认设置；原文件无法备份，尚未修改，请先手动保留后重新设置。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = "无法读取配置，已使用默认设置。请检查程序目录的读取权限。";
        }
        return new EarcueConfig();
    }

    private void Normalize()
    {
        Asr ??= new(); Segment ??= new(); Trigger ??= new(); AutoAnswer ??= new();
        Profile ??= new(); Llm ??= new(); Overlay ??= new(); Mic ??= new(); Privacy ??= new();
        Asr.LoopbackModel ??= new AsrOptions().LoopbackModel;
        Asr.MicModel ??= Asr.LoopbackModel;
        Asr.ModelRoot ??= string.Empty;
        Asr.Hotwords ??= new();
        Profile.Role ??= InterviewRolePresets.Candidate;
        Profile.Resume ??= string.Empty;
        Llm.Provider ??= "deepseek";
        Llm.BaseUrl ??= new LlmOptions().BaseUrl;
        Llm.Model ??= new LlmOptions().Model;
        Llm.ProtectedApiKey ??= string.Empty;
        Llm.UserRoleDescription ??= string.Empty;
        Llm.UserStance ??= string.Empty;
        // Migrate the old defaults that prematurely canceled reasoning/long-context streams.
        if (Llm.FirstTokenTimeoutMs == 5000 && Llm.TimeoutMs == 20000)
        {
            Llm.FirstTokenTimeoutMs = 15000;
            Llm.TimeoutMs = 60000;
        }
        Overlay.Position ??= OverlayPositionPresets.BottomLeft;
        Overlay.FontFamily ??= "Microsoft YaHei UI";
        Overlay.AnswerColor ??= "#00F0FF";
    }

    public void SaveToFile(string path)
    {
        Normalize();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, SaveOptions);

        // 先写临时文件再替换，避免写一半时崩溃导致配置损坏
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}

public sealed class AsrOptions
{
    [JsonPropertyName("loopbackModel")]
    public string LoopbackModel { get; set; } = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";

    [JsonPropertyName("micModel")]
    public string MicModel { get; set; } = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";

    /// <summary>模型根目录；留空则使用 %LOCALAPPDATA%\Earcue\models。</summary>
    [JsonPropertyName("modelRoot")]
    public string ModelRoot { get; set; } = string.Empty;

    [JsonPropertyName("hotwords")]
    public List<string> Hotwords { get; set; } = new();

    /// <summary>sherpa-onnx 推理线程数；14M 模型在 2 线程下通常已可实时。</summary>
    [JsonPropertyName("numThreads")]
    public int NumThreads { get; set; } = 2;

    /// <summary>是否优先使用 *.int8.onnx（体积小、CPU 更快；默认开启）。</summary>
    [JsonPropertyName("useInt8")]
    public bool UseInt8 { get; set; } = true;

    /// <summary>sherpa-onnx 解码方法：greedy_search（低延迟）或 modified_beam_search（更准）。</summary>
    [JsonPropertyName("decodingMethod")]
    public string DecodingMethod { get; set; } = "greedy_search";
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

/// <summary>
/// 自动应答：不再判断"是不是疑问句"，而是把最近识别到的全部内容交给 LLM；
/// 当音频停顿、识别结果完成且稳定后，自动生成一条接话提示。
/// </summary>
public sealed class AutoAnswerOptions
{
    [JsonPropertyName("extractAndCorrectQuestion")]
    public bool ExtractAndCorrectQuestion { get; set; } = true;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>音频活动与识别更新均停止多久后触发应答；未完成的草稿会继续等待。</summary>
    [JsonPropertyName("silenceMs")]
    public int SilenceMs { get; set; } = 2000;

    /// <summary>距离上次应答后累计的新增字数达到此值才触发，避免"嗯/好"之类的噪声。</summary>
    [JsonPropertyName("minChars")]
    public int MinChars { get; set; } = 6;

    /// <summary>两次自动应答之间的最小间隔。</summary>
    [JsonPropertyName("cooldownMs")]
    public int CooldownMs { get; set; } = 3000;
}

/// <summary>
/// 面试场景资料：我是候选人还是面试官，以及可用于回答的个人简历/背景。
/// 简历只保存在本机 config.json；发送给模型时会按 MaxResumeChars 截断。
/// </summary>
public sealed class ProfileOptions
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = InterviewRolePresets.Candidate;

    [JsonPropertyName("resume")]
    public string Resume { get; set; } = string.Empty;

    [JsonPropertyName("maxResumeChars")]
    public int MaxResumeChars { get; set; } = 2500;

    [JsonIgnore]
    public InterviewRole RoleValue => InterviewRolePresets.ToRole(Role);

    [JsonIgnore]
    public string RoleDisplayName => InterviewRolePresets.Find(Role).DisplayName;
}

public sealed class LlmOptions
{
    /// <summary>服务商预设 Id（见 <see cref="LlmProviderPresets"/>），仅用于设置页回显。</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "deepseek";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>运行期内存中的明文 Key，绝不写入 config.json。</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的 Key（Base64，带 dpapi:v1: 前缀），这是唯一落盘形式。</summary>
    [JsonPropertyName("apiKeyProtected")]
    public string ProtectedApiKey { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>已废弃：回答不再限制字数，保留字段只为兼容旧 config.json。</summary>
    [JsonPropertyName("maxChars")]
    public int MaxChars { get; set; } = 0;

    /// <summary>单次回答的最大输出 token。这是模型输出安全上限，不是字数限制。</summary>
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 1024;

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 60000;

    [JsonPropertyName("firstTokenTimeoutMs")]
    public int FirstTokenTimeoutMs { get; set; } = 15000;

    [JsonPropertyName("userRoleDescription")]
    public string UserRoleDescription { get; set; } = "技术研发负责人";

    [JsonPropertyName("userStance")]
    public string UserStance { get; set; } = string.Empty;

    /// <summary>内存中是否已有可用 Key（未 Load 时也会检查密文是否存在）。</summary>
    [JsonIgnore]
    public bool HasApiKey =>
        !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(ProtectedApiKey);

    /// <summary>用于界面展示的脱敏 Key，例如 sk-…a1b2。</summary>
    [JsonIgnore]
    public string MaskedApiKey
    {
        get
        {
            var key = ApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.IsNullOrEmpty(ProtectedApiKey) ? "未配置" : "已加密保存";
            }

            return key.Length < 12 ? "已配置" : $"{key[..3]}…{key[^4..]}";
        }
    }

    /// <summary>设置 API Key：同时更新内存明文与 DPAPI 密文；传入空值表示清除。</summary>
    public void SetApiKey(string? apiKey)
    {
        var trimmed = apiKey?.Trim() ?? string.Empty;
        ApiKey = trimmed;
        ProtectedApiKey = trimmed.Length == 0 ? string.Empty : ApiKeyProtector.Protect(trimmed);
    }

    /// <summary>从密文恢复内存明文 Key，返回是否恢复成功。</summary>
    public bool TryLoadApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return true;
        }

        ApiKey = ApiKeyProtector.Unprotect(ProtectedApiKey) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(ApiKey);
    }
}

public sealed class OverlayOptions
{
    private double _legacyOpacity = 0.92;
    private double? _backgroundOpacity;
    private double? _textOpacity;

    /// <summary>旧版整体不透明度，仅用于兼容没有独立透明度字段的配置。</summary>
    [JsonPropertyName("opacity")]
    public double Opacity
    {
        get => _legacyOpacity;
        set => _legacyOpacity = double.IsFinite(value) ? Math.Clamp(value, 0.05, 1) : 0.92;
    }

    /// <summary>背景不透明度，0 完全透明，1 完全不透明；兼容旧背景自带的 alpha。</summary>
    [JsonPropertyName("backgroundOpacity")]
    public double BackgroundOpacity
    {
        get => _backgroundOpacity ?? Opacity * (236.0 / 255);
        set => _backgroundOpacity = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.85;
    }

    /// <summary>文字不透明度，0 完全透明，1 完全不透明。</summary>
    [JsonPropertyName("textOpacity")]
    public double TextOpacity
    {
        get => _textOpacity ?? Opacity;
        set => _textOpacity = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.92;
    }

    /// <summary>绝对 X 坐标；与 PositionY 同时有值时优先于 Position 预设。</summary>
    [JsonPropertyName("positionX")]
    public double? PositionX { get; set; }

    /// <summary>绝对 Y 坐标；与 PositionX 同时有值时优先于 Position 预设。</summary>
    [JsonPropertyName("positionY")]
    public double? PositionY { get; set; }

    /// <summary>预设位置：BottomLeft / BottomRight / TopLeft / TopRight / Custom（拖动后）。</summary>
    [JsonPropertyName("position")]
    public string Position { get; set; } = OverlayPositionPresets.BottomLeft;

    [JsonPropertyName("marginX")]
    public double MarginX { get; set; } = 24;

    [JsonPropertyName("marginY")]
    public double MarginY { get; set; } = 24;

    /// <summary>卡片宽度；拖动右下角手柄可调整。</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; } = 460;

    /// <summary>卡片最小高度；内容更长时仍会自动向上生长。</summary>
    [JsonPropertyName("minHeight")]
    public double MinHeight { get; set; } = 0;

    /// <summary>字体族名称，例如 "Microsoft YaHei UI"。</summary>
    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    [JsonPropertyName("questionFontSize")]
    public double QuestionFontSize { get; set; } = 13;

    [JsonPropertyName("answerFontSize")]
    public double AnswerFontSize { get; set; } = 15;

    /// <summary>答案文字颜色，#RRGGBB。</summary>
    [JsonPropertyName("answerColor")]
    public string AnswerColor { get; set; } = "#00F0FF";

    [JsonPropertyName("excludeFromCapture")]
    public bool ExcludeFromCapture { get; set; } = true;

    /// <summary>是否在无新内容一段时间后自动隐藏；默认关闭，卡片会保留到被新回答替换。</summary>
    [JsonPropertyName("autoHide")]
    public bool AutoHide { get; set; } = false;

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
