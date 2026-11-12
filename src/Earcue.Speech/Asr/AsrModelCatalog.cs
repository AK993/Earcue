using System.Text.Json;

namespace Earcue.Speech.Asr;

/// <summary>一个可下载的官方 ASR 模型（清单来自 asr-models.json 嵌入资源）。</summary>
public sealed class AsrModelDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string Description { get; init; } = string.Empty;

    public string SizeDisplay => SizeBytes <= 0
        ? "未知大小"
        : SizeBytes >= 1024L * 1024 * 1024
            ? $"{SizeBytes / 1024.0 / 1024.0 / 1024.0:0.0} GB"
            : $"{SizeBytes / 1024.0 / 1024.0:0} MB";
}

/// <summary>ASR 模型清单：默认中文 14M，可选完整中英双语模型。</summary>
public static class AsrModelCatalog
{
    private const string ResourceName = "Earcue.Speech.Asr.asr-models.json";

    private static readonly Lazy<IReadOnlyList<AsrModelDescriptor>> Cache = new(Load);

    public static IReadOnlyList<AsrModelDescriptor> All => Cache.Value;

    public static AsrModelDescriptor? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        foreach (var model in All)
        {
            if (string.Equals(model.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }

    private static IReadOnlyList<AsrModelDescriptor> Load()
    {
        var assembly = typeof(AsrModelCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return Array.Empty<AsrModelDescriptor>();
        }

        var models = JsonSerializer.Deserialize<List<AsrModelDescriptor>>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return models ?? (IReadOnlyList<AsrModelDescriptor>)Array.Empty<AsrModelDescriptor>();
    }
}
