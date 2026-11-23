using Earcue.Core.Configuration;
using Earcue.Core.Models;

namespace Earcue.Speech.Asr;

/// <summary>按角色创建本地 ASR：环回用 asr.loopbackModel，麦克风用 asr.micModel。</summary>
public static class AsrEngineFactory
{
    public static SherpaAsrEngine Create(EarcueConfig config, AudioRole role, string? selectedModel = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var modelId = selectedModel ?? (role == AudioRole.Remote
            ? config.Asr.LoopbackModel
            : config.Asr.MicModel);

        if (!AsrModelLocator.TryLocate(
                modelId,
                config.Asr.ModelRoot,
                config.Asr.UseInt8,
                out var files,
                out var error) ||
            files is null)
        {
            throw new FileNotFoundException(error, modelId);
        }

        return new SherpaAsrEngine(config.Asr, config.Segment, role, files);
    }
}
