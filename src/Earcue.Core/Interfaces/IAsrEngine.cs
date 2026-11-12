using Earcue.Core.Models;

namespace Earcue.Core.Interfaces;

public interface IAsrEngine : IDisposable
{
    IAsyncEnumerable<AsrPartial> PushAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    void Reset();
}
