using Earcue.Core.Models;

namespace Earcue.Core.Interfaces;

public interface IAudioSource : IDisposable
{
    event Action<AudioFrame>? FrameReady;
    bool IsRunning { get; }
    void Start();
    void Stop();
}
