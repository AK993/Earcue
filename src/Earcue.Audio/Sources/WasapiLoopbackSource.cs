using System.Diagnostics;
using Earcue.Audio.Processing;
using Earcue.Core.Interfaces;
using Earcue.Core.Logging;
using Earcue.Core.Models;
using NAudio.Wave;

namespace Earcue.Audio.Sources;

public sealed class WasapiLoopbackSource : IAudioSource
{
    private ILoopbackCapture? _capture;
    private readonly Func<ILoopbackCapture> _createCapture;
    private readonly AudioRole _role;
    private string DeviceLabel => _role == AudioRole.Local ? "麦克风" : "输出设备";
    private readonly TimeSpan _reconnectDelay;
    private readonly int _maxReconnectAttempts;
    private AudioRingBuffer? _ringBuffer;
    private AudioFormatConverter? _converter;
    private Thread? _workerThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly object _captureGate = new();
    private readonly object _processingGate = new();
    private int _reconnecting;
    private bool IsReconnecting => Volatile.Read(ref _reconnecting) != 0;
    private volatile bool _isRunning;
    private bool _isDisposed;

    private readonly byte[] _silenceFrame = new byte[320]; // 16kHz * 16bit * 1ch * 10ms = 320 bytes
    private long _totalSamplesDispatched = 0;
    private readonly Stopwatch _wallClock = new();

    public event Action<AudioFrame>? FrameReady;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? CaptureFailed;
    public bool IsRunning => _isRunning;
    public bool IsReceivingAudio => IsRunning && !IsReconnecting;

    public WasapiLoopbackSource() : this(() => new WasapiCaptureAdapter(), TimeSpan.FromSeconds(2), 20) { }

    public static WasapiLoopbackSource CreateMicrophone() =>
        new(() => new WasapiCaptureAdapter(microphone: true), TimeSpan.FromSeconds(2), 20, AudioRole.Local);

    internal WasapiLoopbackSource(Func<ILoopbackCapture> createCapture, TimeSpan reconnectDelay, int maxReconnectAttempts,
        AudioRole role = AudioRole.Remote)
    {
        _token = _cts.Token;
        _createCapture = createCapture;
        _reconnectDelay = reconnectDelay;
        _maxReconnectAttempts = maxReconnectAttempts;
        _role = role;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (IsRunning) return;
        if (_token.IsCancellationRequested)
            throw new InvalidOperationException("采集源已停止，请创建新的音频采集源。");

        try
        {
            _capture = _createCapture();
            var waveFormat = _capture.WaveFormat;
            AppLogger.Instance.Information(
                "WASAPI {Device}初始化成功: {SampleRate}Hz, {Channels}声道, {Bits}bit, 编码: {Encoding}",
                DeviceLabel,
                waveFormat.SampleRate,
                waveFormat.Channels,
                waveFormat.BitsPerSample,
                waveFormat.Encoding);

            _converter = CreateConverter(waveFormat);
            _ringBuffer = new AudioRingBuffer(1024 * 512); // 512KB

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _totalSamplesDispatched = 0;
            _wallClock.Restart();

            _workerThread = new Thread(ProcessLoop)
            {
                IsBackground = true,
                Name = $"Earcue.Audio.{_role}",
                Priority = ThreadPriority.AboveNormal
            };

            _isRunning = true;
            _capture.StartRecording();
            _workerThread.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "启动 WASAPI {Device}采集失败", DeviceLabel);
            Stop();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!ReferenceEquals(sender, _capture)) return;
        // 铁律：音频回调中不分配、不加重锁、不重采样，只写入环形缓冲
        _ringBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _capture)) return;
        if (e.Exception != null)
        {
            AppLogger.Instance.Warning(e.Exception, "WASAPI {Device}采集异常停止，尝试重启", DeviceLabel);
        }

        if (IsRunning && !_token.IsCancellationRequested)
        {
            _ = ReconnectLoopAsync();
        }
    }

    private async Task ReconnectLoopAsync()
    {
        if (!IsRunning || _token.IsCancellationRequested ||
            Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
        {
            return;
        }

        StatusChanged?.Invoke($"音频已断开，正在重连默认{DeviceLabel}…");
        AppLogger.Instance.Warning("WASAPI 音频流异常断开，正在重连默认{Device}", DeviceLabel);

        var maxAttempts = _maxReconnectAttempts; // 默认持续重试约 40 秒
        for (int attempt = 1; attempt <= maxAttempts && IsRunning && !_token.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(_reconnectDelay, _token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!IsRunning || _token.IsCancellationRequested)
            {
                break;
            }

            lock (_captureGate)
            {
                if (!IsRunning || _token.IsCancellationRequested) break;
                ILoopbackCapture? newCapture = null;
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

                    newCapture = _createCapture();
                    var waveFormat = newCapture.WaveFormat;

                    lock (_processingGate)
                    {
                        _converter = CreateConverter(waveFormat);
                        _ringBuffer?.Clear();
                        // 保持全流时间戳单调递增，跳过重连期间的空缺，不突发补发几十秒静音。
                        _totalSamplesDispatched = Math.Max(_totalSamplesDispatched,
                            (long)(_wallClock.Elapsed.TotalSeconds * 100) * 160);
                    }

                    newCapture.DataAvailable += OnDataAvailable;
                    newCapture.RecordingStopped += OnRecordingStopped;

                    _capture = newCapture;
                    newCapture.StartRecording();

                    Interlocked.Exchange(ref _reconnecting, 0);
                    StatusChanged?.Invoke(_role == AudioRole.Local ? "麦克风已重连" : "音频重连成功，正在监听并识别系统声音");
                    AppLogger.Instance.Information(
                        "WASAPI {Device}重连成功: {SampleRate}Hz, {Channels}声道, {Bits}bit (第 {Attempt} 次重试生效)",
                        DeviceLabel,
                        waveFormat.SampleRate,
                        waveFormat.Channels,
                        waveFormat.BitsPerSample,
                        attempt);
                    return;
                }
                catch (Exception ex)
                {
                    if (newCapture != null)
                    {
                        newCapture.DataAvailable -= OnDataAvailable;
                        newCapture.RecordingStopped -= OnRecordingStopped;
                        newCapture.Dispose();
                        if (ReferenceEquals(_capture, newCapture)) _capture = null;
                    }
                    AppLogger.Instance.Warning(ex, "第 {Attempt}/{Max} 次重连音频设备失败，将在 2 秒后继续重试", attempt, maxAttempts);
                }
            }
        }

        Interlocked.Exchange(ref _reconnecting, 0);
        if (IsRunning && !_token.IsCancellationRequested)
        {
            AppLogger.Instance.Error("WASAPI {Device}重连超出最大尝试次数", DeviceLabel);
            FailCapture(new IOException($"{DeviceLabel}重连失败，已停止该路采集。"));
        }
    }

    private static AudioFormatConverter CreateConverter(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        if (!isFloat && (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16))
            throw new NotSupportedException($"不支持的音频格式：{format.Encoding} {format.BitsPerSample} bit。");
        return new AudioFormatConverter(format.SampleRate, format.Channels, isFloat);
    }

    private void ProcessLoop()
    {
        try
        {
            ProcessFrames();
        }
        catch (Exception ex)
        {
            if (_token.IsCancellationRequested) return;
            AppLogger.Instance.Error(ex, "音频处理线程异常，已停止采集");
            FailCapture(new InvalidOperationException("音频处理失败，请重新启动采集。", ex));
        }
    }

    private void FailCapture(Exception error)
    {
        if (!IsRunning) return;
        _isRunning = false;
        _cts.Cancel();
        CaptureFailed?.Invoke(error);
    }

    private void ProcessFrames()
    {
        var rawBuffer = new byte[8192];
        var pcmRemainder = new List<byte>();
        const int frameBytes10Ms = 320; // 10ms at 16k mono 16bit = 160 samples = 320 bytes

        AudioFormatConverter? currentConverter = null;
        while (!_token.IsCancellationRequested)
        {
            if (IsReconnecting)
            {
                Thread.Sleep(50);
                continue;
            }

            lock (_processingGate)
            {
                if (!ReferenceEquals(currentConverter, _converter))
                {
                    pcmRemainder.Clear();
                    currentConverter = _converter;
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
                while (pcmRemainder.Count >= frameBytes10Ms && !_token.IsCancellationRequested)
                {
                    var frameData = new byte[frameBytes10Ms];
                    pcmRemainder.CopyTo(0, frameData, 0, frameBytes10Ms);
                    pcmRemainder.RemoveRange(0, frameBytes10Ms);

                    _totalSamplesDispatched += 160;
                    var timestamp = TimeSpan.FromSeconds((double)_totalSamplesDispatched / 16000.0);

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
                }

                // 节拍同步与静音填充 (Heartbeat Silence Injection)
                // 计算按照实际墙钟时间，当前应当发送的样本数
                long expectedSamples = (long)(_wallClock.Elapsed.TotalSeconds * 16000.0);
                long sampleDeficit = expectedSamples - _totalSamplesDispatched;

                // 当会议无声、WASAPI无数据包达到 10ms (160 samples) 以上时，补静音帧保持时间轴严格对齐
                while (sampleDeficit >= 160 && !_token.IsCancellationRequested)
                {
                    _totalSamplesDispatched += 160;
                    var timestamp = TimeSpan.FromSeconds((double)_totalSamplesDispatched / 16000.0);

                    var silenceFrame = new AudioFrame(
                        role: _role,
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
            }

            Thread.Sleep(5); // 5ms 循环检测，确保 10ms 帧及时送达
        }
    }

    public void Stop()
    {
        _isRunning = false;

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

        if (_workerThread is { IsAlive: true } && _workerThread != Thread.CurrentThread)
            _workerThread.Join(1000);
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
