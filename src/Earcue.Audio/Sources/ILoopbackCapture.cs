using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Earcue.Audio.Sources;

// 将设备访问与生命周期分开，使断连和驱动错误可用内存设备进行回归测试。
internal interface ILoopbackCapture : IDisposable
{
    WaveFormat WaveFormat { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

internal sealed class WasapiCaptureAdapter : ILoopbackCapture
{
    private readonly WasapiCapture _capture;
    public WasapiCaptureAdapter(bool microphone = false)
    {
        if (microphone)
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            _capture = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 30);
        }
        else
        {
            _capture = new WasapiLoopbackCapture();
        }
        _capture.DataAvailable += (_, e) => DataAvailable?.Invoke(this, e);
        _capture.RecordingStopped += (_, e) => RecordingStopped?.Invoke(this, e);
    }
    public WaveFormat WaveFormat => _capture.WaveFormat;
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public void StartRecording() => _capture.StartRecording();
    public void StopRecording() => _capture.StopRecording();
    public void Dispose() => _capture.Dispose();
}
