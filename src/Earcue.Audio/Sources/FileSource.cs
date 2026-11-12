using Earcue.Core.Interfaces;
using Earcue.Core.Models;
using NAudio.Wave;

namespace Earcue.Audio.Sources;

/// <summary>
/// 离线音频文件源，用于回放脱敏会议音频（无需真实开会即可全链路调试）
/// </summary>
public sealed class FileSource : IAudioSource
{
    private readonly string _filePath;
    private readonly AudioRole _role;
    private readonly bool _realtimeSimulation;
    private readonly CancellationTokenSource _cts = new();
    private Task? _playbackTask;
    private bool _disposed;

    public event Action<AudioFrame>? FrameReady;
    public bool IsRunning { get; private set; }

    public FileSource(string filePath, AudioRole role = AudioRole.Remote, bool realtimeSimulation = true)
    {
        _filePath = filePath;
        _role = role;
        _realtimeSimulation = realtimeSimulation;
    }

    public void Start()
    {
        if (IsRunning) return;
        if (!File.Exists(_filePath))
            throw new FileNotFoundException($"找不到测试音频文件: {_filePath}");

        IsRunning = true;
        _playbackTask = Task.Run(() => PlaybackLoop(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts.Cancel();
        try
        {
            _playbackTask?.Wait(1000);
        }
        catch (AggregateException) { }
    }

    private void PlaybackLoop(CancellationToken ct)
    {
        using var reader = new AudioFileReader(_filePath);
        // 目标格式: 16kHz 16-bit 单声道
        var outFormat = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, outFormat)
        {
            ResamplerQuality = 60
        };

        // 10ms 帧: 16000 * 0.010 = 160 采样 * 2 字节 = 320 字节
        const int bytesPer10Ms = 320;
        var buffer = new byte[bytesPer10Ms];
        long totalBytesRead = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            int bytesRead = resampler.Read(buffer, 0, bytesPer10Ms);
            if (bytesRead <= 0)
            {
                break; // 文件读取完毕
            }

            totalBytesRead += bytesRead;
            var frameData = new byte[bytesRead];
            Array.Copy(buffer, frameData, bytesRead);

            var timestamp = TimeSpan.FromSeconds((double)totalBytesRead / (16000 * 2));
            var frame = new AudioFrame(
                role: _role,
                data: frameData,
                sampleRate: 16000,
                channels: 1,
                bitsPerSample: 16,
                timestamp: timestamp,
                isSilence: false
            );

            FrameReady?.Invoke(frame);

            if (_realtimeSimulation)
            {
                // 控制在 10ms 节拍
                var expectedElapsed = TimeSpan.FromMilliseconds(totalBytesRead / 32.0);
                var actualElapsed = stopwatch.Elapsed;
                var sleepTime = expectedElapsed - actualElapsed;
                if (sleepTime > TimeSpan.Zero)
                {
                    Thread.Sleep(sleepTime);
                }
            }
        }

        IsRunning = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}
