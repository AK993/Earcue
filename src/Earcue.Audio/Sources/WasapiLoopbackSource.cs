using System.Diagnostics;
using Earcue.Audio.Processing;
using Earcue.Core.Interfaces;
using Earcue.Core.Logging;
using Earcue.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Earcue.Audio.Sources;

public sealed class WasapiLoopbackSource : IAudioSource
{
    private WasapiLoopbackCapture? _capture;
    private AudioRingBuffer? _ringBuffer;
    private AudioFormatConverter? _converter;
    private Thread? _workerThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _captureGate = new();
    private volatile bool _isReconnecting;
    private bool _isDisposed;

    private readonly byte[] _silenceFrame = new byte[320]; // 16kHz * 16bit * 1ch * 10ms = 320 bytes
    private long _totalSamplesDispatched = 0;
    private readonly Stopwatch _wallClock = new();

    public event Action<AudioFrame>? FrameReady;
    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _capture = new WasapiLoopbackCapture();
            var waveFormat = _capture.WaveFormat;
            AppLogger.Instance.Information(
                "WASAPI 环回设备初始化成功: {SampleRate}Hz, {Channels}声道, {Bits}bit, 编码: {Encoding}",
                waveFormat.SampleRate,
                waveFormat.Channels,
                waveFormat.BitsPerSample,
                waveFormat.Encoding);

            bool isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
            _converter = new AudioFormatConverter(waveFormat.SampleRate, waveFormat.Channels, isFloat);
            _ringBuffer = new AudioRingBuffer(1024 * 512); // 512KB

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _totalSamplesDispatched = 0;
            _wallClock.Restart();

            _workerThread = new Thread(ProcessLoop)
            {
                IsBackground = true,
                Name = "Earcue.LoopbackWorker",
                Priority = ThreadPriority.AboveNormal
            };

            _capture.StartRecording();
            _workerThread.Start();
            IsRunning = true;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "启动 WASAPI 环回采集失败");
            Stop();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // 铁律：音频回调中不分配、不加重锁、不重采样，只写入环形缓冲
        _ringBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            AppLogger.Instance.Warning(e.Exception, "WASAPI 环回采集异常停止，尝试重启...");
        }

        if (IsRunning && !_cts.IsCancellationRequested)
        {
            _ = ReconnectLoopAsync();
        }
    }

    private async Task ReconnectLoopAsync()
    {
        if (_isReconnecting || !IsRunning || _cts.IsCancellationRequested)
        {
            return;
        }

        _isReconnecting = true;
        AppLogger.Instance.Warning("WASAPI 环回音频流异常断开，正在尝试自动重连默认输出设备...");

        const int maxAttempts = 20; // 持续重试约 40 秒
        for (int attempt = 1; attempt <= maxAttempts && IsRunning && !_cts.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!IsRunning || _cts.IsCancellationRequested)
            {
                break;
            }

            lock (_captureGate)
            {
                try
                {
                    if (_capture != null)
                    {
                        _capture.DataAvailable -= OnDataAvailable;
                        _capture.RecordingStopped -= OnRecordingStopped;
                        try { _capture.StopRecording(); } catch { }
                        _capture.Dispose();
                        _capture = null;
                    }

                    var newCapture = new WasapiLoopbackCapture();
                    var waveFormat = newCapture.WaveFormat;
                    bool isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

                    _converter = new AudioFormatConverter(waveFormat.SampleRate, waveFormat.Channels, isFloat);
                    _ringBuffer?.Clear();

                    newCapture.DataAvailable += OnDataAvailable;
                    newCapture.RecordingStopped += OnRecordingStopped;

                    newCapture.StartRecording();
                    _capture = newCapture;

                    _wallClock.Restart();
                    _totalSamplesDispatched = 0;

                    _isReconnecting = false;
                    AppLogger.Instance.Information(
                        "WASAPI 环回设备重连成功: {SampleRate}Hz, {Channels}声道, {Bits}bit (第 {Attempt} 次重试生效)",
                        waveFormat.SampleRate,
                        waveFormat.Channels,
                        waveFormat.BitsPerSample,
                        attempt);
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Instance.Warning(ex, "第 {Attempt}/{Max} 次重连音频设备失败，将在 2 秒后继续重试", attempt, maxAttempts);
                }
            }
        }

        _isReconnecting = false;
        if (IsRunning && !_cts.IsCancellationRequested)
        {
            AppLogger.Instance.Error("WASAPI 环回重连超出最大尝试次数，未能自动恢复音频设备");
        }
    }

    private void ProcessLoop()
    {
        var rawBuffer = new byte[8192];
        var pcmRemainder = new List<byte>();
        const int frameBytes10Ms = 320; // 10ms at 16k mono 16bit = 160 samples = 320 bytes

        while (!_cts.Token.IsCancellationRequested)
        {
            if (_isReconnecting)
            {
                Thread.Sleep(50);
                continue;
            }

            int available = _ringBuffer?.AvailableToRead ?? 0;
            if (available > 0)
            {
                int read = _ringBuffer!.Read(rawBuffer, 0, Math.Min(rawBuffer.Length, available));
                if (read > 0 && _converter != null)
                {
                    byte[] pcm16k = _converter.ConvertTo16kMonoPcm(rawBuffer, read);
                    pcmRemainder.AddRange(pcm16k);
                }
            }

            // 发送所有完整的 10ms 帧
            while (pcmRemainder.Count >= frameBytes10Ms)
            {
                var frameData = new byte[frameBytes10Ms];
                pcmRemainder.CopyTo(0, frameData, 0, frameBytes10Ms);
                pcmRemainder.RemoveRange(0, frameBytes10Ms);

                _totalSamplesDispatched += 160;
                var timestamp = TimeSpan.FromSeconds((double)_totalSamplesDispatched / 16000.0);

                var frame = new AudioFrame(
                    role: AudioRole.Remote,
                    data: frameData,
                    sampleRate: 16000,
                    channels: 1,
                    bitsPerSample: 16,
                    timestamp: timestamp,
                    isSilence: false
                );

                FrameReady?.Invoke(frame);
            }

            // 节拍同步与静音填充 (Heartbeat Silence Injection)
            // 计算按照实际墙钟时间，当前应当发送的样本数
            long expectedSamples = (long)(_wallClock.Elapsed.TotalSeconds * 16000.0);
            long sampleDeficit = expectedSamples - _totalSamplesDispatched;

            // 当会议无声、WASAPI无数据包达到 10ms (160 samples) 以上时，补静音帧保持时间轴严格对齐
            while (sampleDeficit >= 160)
            {
                _totalSamplesDispatched += 160;
                var timestamp = TimeSpan.FromSeconds((double)_totalSamplesDispatched / 16000.0);

                var silenceFrame = new AudioFrame(
                    role: AudioRole.Remote,
                    data: _silenceFrame,
                    sampleRate: 16000,
                    channels: 1,
                    bitsPerSample: 16,
                    timestamp: timestamp,
                    isSilence: true
                );

                FrameReady?.Invoke(silenceFrame);
                sampleDeficit -= 160;
            }

            Thread.Sleep(5); // 5ms 循环检测，确保 10ms 帧及时送达
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _cts.Cancel();
        lock (_captureGate)
        {
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    try { _capture.StopRecording(); } catch { }
                    _capture.Dispose();
                    _capture = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warning(ex, "停止 WASAPI 时发生异常");
            }
        }

        _workerThread?.Join(500);
        _workerThread = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _cts.Dispose();
    }
}
