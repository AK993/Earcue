using System.Runtime.CompilerServices;
using Earcue.Core.Interfaces;
using Earcue.Core.Models;

namespace Earcue.Speech.Asr;

/// <summary>
/// 模拟 ASR 引擎，用于开发自测、离线回放流水线及无 GPU/模型环境下的端到端验证
/// </summary>
public sealed class MockAsrEngine : IAsrEngine
{
    private readonly Func<AudioFrame, string?>? _textGenerator;

    public MockAsrEngine(Func<AudioFrame, string?>? textGenerator = null)
    {
        _textGenerator = textGenerator;
    }

    public async IAsyncEnumerable<AsrPartial> PushAsync(
        AudioFrame frame,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (frame.IsSilence) yield break;

        var recognized = _textGenerator?.Invoke(frame);
        if (!string.IsNullOrEmpty(recognized))
        {
            yield return new AsrPartial(
                role: frame.Role,
                text: recognized,
                isFinal: true,
                startTime: frame.Timestamp,
                endTime: frame.Timestamp + TimeSpan.FromMilliseconds(10)
            );
        }
    }

    public void Reset() { }

    public void Dispose() { }
}
