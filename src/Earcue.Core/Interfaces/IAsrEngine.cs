using Earcue.Core.Models;

namespace Earcue.Core.Interfaces;

/// <summary>
/// 流式 ASR 引擎。返回的 <see cref="AsrPartial.Text"/> 是当前句子的累计文本（不是增量）；
/// <see cref="AsrPartial.IsFinal"/> = true 表示该句已结束，可以直接送入理解层。
/// </summary>
public interface IAsrEngine : IDisposable
{
    IAsyncEnumerable<AsrPartial> PushAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    void Reset();
}
