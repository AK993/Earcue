namespace Earcue.Speech.Asr;

/// <summary>一个可被 sherpa-onnx 加载的流式 transducer 模型文件集合。</summary>
public sealed class AsrModelFiles
{
    public string ModelId { get; init; } = string.Empty;

    public string ModelDirectory { get; init; } = string.Empty;

    public string Encoder { get; init; } = string.Empty;

    public string Decoder { get; init; } = string.Empty;

    public string Joiner { get; init; } = string.Empty;

    public string Tokens { get; init; } = string.Empty;

    public bool IsInt8 { get; init; }

    public string EncoderFileName => Path.GetFileName(Encoder);

    public bool IsComplete =>
        File.Exists(Encoder) && File.Exists(Decoder) && File.Exists(Joiner) && File.Exists(Tokens);
}

/// <summary>
/// 负责找到本地已安装的 ASR 模型。默认安装目录为
/// <c>%LOCALAPPDATA%\Earcue\models\{modelId}</c>，可通过 asr.modelRoot 覆盖。
/// </summary>
public static class AsrModelLocator
{
    public const string TokensFileName = "tokens.txt";

    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Earcue",
            "models");

    /// <summary>解析模型根目录；空值使用 LocalAppData，支持环境变量；相对路径以程序目录为基准。</summary>
    public static string ResolveRoot(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(DefaultRoot);
        }

        var expanded = Environment.ExpandEnvironmentVariables(configuredRoot.Trim());
        if (!Path.IsPathRooted(expanded))
        {
            expanded = Path.Combine(AppContext.BaseDirectory, expanded);
        }

        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// 在模型根目录下定位 <paramref name="modelId"/> 的 encoder/decoder/joiner/tokens。
    /// 文件按 encoder*.onnx / decoder*.onnx / joiner*.onnx 自动发现，优先 int8 量化版本。
    /// </summary>
    public static bool TryLocate(
        string? modelId,
        string? configuredRoot,
        bool preferInt8,
        out AsrModelFiles? files,
        out string error)
    {
        files = null;
        error = string.Empty;

        var id = modelId?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            error = "未配置 ASR 模型 ID（asr.loopbackModel / asr.micModel）";
            return false;
        }

        // 防止配置里写入路径穿越，模型 ID 只能是单层目录名
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains("..", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal))
        {
            error = $"ASR 模型 ID 不合法：{id}";
            return false;
        }

        var root = ResolveRoot(configuredRoot);
        var dir = Path.Combine(root, id);
        if (!Directory.Exists(dir))
        {
            // 如果默认 LocalAppData 目录下没有，且未显式指定根目录，自动回退到程序目录下的 models/{id}
            var appLocalDir = Path.Combine(AppContext.BaseDirectory, "models", id);
            if (string.IsNullOrWhiteSpace(configuredRoot) && Directory.Exists(appLocalDir))
            {
                dir = appLocalDir;
            }
            else
            {
                error =
                    $"未找到 ASR 模型目录：{dir}{Environment.NewLine}" +
                    "请先运行 tools/download-asr-models.ps1 下载模型，或把模型解压到该目录。";
                return false;
            }
        }

        var tokens = Path.Combine(dir, TokensFileName);
        if (!File.Exists(tokens))
        {
            error = $"模型目录缺少 {TokensFileName}：{dir}";
            return false;
        }

        var encoder = FindModelFile(dir, "encoder", preferInt8);
        var decoder = FindModelFile(dir, "decoder", preferInt8);
        var joiner = FindModelFile(dir, "joiner", preferInt8);

        if (encoder is null || decoder is null || joiner is null)
        {
            var missing = new List<string>();
            if (encoder is null) missing.Add("encoder*.onnx");
            if (decoder is null) missing.Add("decoder*.onnx");
            if (joiner is null) missing.Add("joiner*.onnx");
            error = $"模型目录不完整（缺少 {string.Join("、", missing)}）：{dir}";
            return false;
        }

        files = new AsrModelFiles
        {
            ModelId = id,
            ModelDirectory = dir,
            Encoder = encoder,
            Decoder = decoder,
            Joiner = joiner,
            Tokens = tokens,
            IsInt8 = IsInt8File(encoder)
        };
        return true;
    }

    /// <summary>按前缀查找模型文件；preferInt8 为 true 时优先 *.int8.onnx。</summary>
    public static string? FindModelFile(string directory, string prefix, bool preferInt8)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var candidates = Directory
            .EnumerateFiles(directory, prefix + "*.onnx", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => IsInt8File(path) == preferInt8)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            // 兼容部分模型把文件放在 exp/ 等子目录的结构
            candidates = Directory
                .EnumerateFiles(directory, prefix + "*.onnx", SearchOption.AllDirectories)
                .OrderByDescending(path => IsInt8File(path) == preferInt8)
                .ThenBy(path => path.Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return candidates.FirstOrDefault();
    }

    public static bool IsInt8File(string path) =>
        path.Contains(".int8.", StringComparison.OrdinalIgnoreCase);
}
