using System.Diagnostics;
using System.Runtime.CompilerServices;
using Earcue.Core.Configuration;
using Earcue.Core.Interfaces;
using Earcue.Core.Logging;
using Earcue.Core.Models;
using SherpaOnnx;

namespace Earcue.Speech.Asr;

/// <summary>
/// 基于 sherpa-onnx 的本地流式 Zipformer / Paraformer 识别引擎。
/// 输入固定为 16kHz / 单声道 / 16-bit PCM 的 10ms 帧，输出当前句子的累计文本：
/// <see cref="AsrPartial.IsFinal"/> = false 表示实时草稿，true 表示端点已结束、可以作为一句完整话处理。
/// 端点规则由 <see cref="SegmentOptions"/> 映射：SilenceMs 控制句末静音，MaxMs 控制最长句长。
/// </summary>
public sealed class SherpaAsrEngine : IAsrEngine
{
    private const int SampleRate = 16000;
    private const float LeftPaddingSeconds = 0.3f;

    private readonly OnlineRecognizer _recognizer;
    private readonly AudioRole _role;
    private readonly bool _endpointEnabled;
    private readonly object _gate = new();

    private OnlineStream _stream;
    private string _lastText = string.Empty;
    private TimeSpan _utteranceStart = TimeSpan.Zero;
    private TimeSpan _lastFrameEnd = TimeSpan.Zero;
    private bool _disposed;

    public SherpaAsrEngine(
        AsrOptions options,
        SegmentOptions segmentOptions,
        AudioRole role,
        AsrModelFiles files)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(segmentOptions);
        ArgumentNullException.ThrowIfNull(files);

        if (!files.IsComplete)
        {
            throw new FileNotFoundException("ASR 模型文件不完整，请重新下载模型。", files.ModelDirectory);
        }

        Role = role;
        _role = role;
        ModelId = files.ModelId;
        Files = files;
        _endpointEnabled = true;

        var config = new OnlineRecognizerConfig
        {
            DecodingMethod = string.IsNullOrWhiteSpace(options.DecodingMethod)
                ? "greedy_search"
                : options.DecodingMethod.Trim(),
            MaxActivePaths = 4,
            EnableEndpoint = 1,
            Rule1MinTrailingSilence = 1.5f, // 一直没检测到文字时的静音端点
            Rule2MinTrailingSilence = Math.Clamp(segmentOptions.SilenceMs / 1000f, 0.3f, 3.0f),
            Rule3MinUtteranceLength = Math.Clamp(segmentOptions.MaxMs / 1000f, 1.0f, 60.0f)
        };

        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;

        if (files.ModelType == AsrModelType.Paraformer)
        {
            config.ModelConfig.Paraformer.Encoder = files.Encoder;
            config.ModelConfig.Paraformer.Decoder = files.Decoder;
        }
        else
        {
            config.ModelConfig.Transducer.Encoder = files.Encoder;
            config.ModelConfig.Transducer.Decoder = files.Decoder;
            config.ModelConfig.Transducer.Joiner = files.Joiner;
        }
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = Math.Clamp(options.NumThreads, 1, 8);
        config.ModelConfig.Debug = 0;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _recognizer = new OnlineRecognizer(config);
            _stream = CreateStream();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"加载本地 ASR 模型失败（{files.ModelId}）：{ex.Message}",
                ex);
        }

        stopwatch.Stop();
        LoadTime = stopwatch.Elapsed;

        AppLogger.Instance.Information(
            "本地 ASR 已加载: model={Model}, role={Role}, int8={Int8}, threads={Threads}, elapsed={Elapsed}ms",
            files.ModelId,
            role,
            files.IsInt8,
            config.ModelConfig.NumThreads,
            stopwatch.ElapsedMilliseconds);
    }

    public AudioRole Role { get; }

    public string ModelId { get; }

    public AsrModelFiles Files { get; }

    public TimeSpan LoadTime { get; }

    public bool IsDisposed => _disposed;

    /// <summary>同步处理一帧音频，返回本帧产生的增量事件（草稿或最终句）。</summary>
    public IReadOnlyList<AsrPartial> Process(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.SampleRate != SampleRate || frame.Channels != 1 || frame.BitsPerSample != 16)
        {
            throw new ArgumentException(
                $"ASR 只接受 16kHz/单声道/16-bit PCM，实际为 {frame.SampleRate}Hz/{frame.Channels}ch/{frame.BitsPerSample}bit",
                nameof(frame));
        }

        lock (_gate)
        {
            ThrowIfDisposed();

            var samples = Pcm16ToFloat(frame.Data);
            _stream.AcceptWaveform(SampleRate, samples);

            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
            }

            var text = (_recognizer.GetResult(_stream).Text ?? string.Empty).Trim();
            var frameEnd = frame.Timestamp + TimeSpan.FromSeconds((double)frame.Data.Length / 2 / SampleRate);
            _lastFrameEnd = frameEnd;
            var events = new List<AsrPartial>(2);

            if (!string.Equals(text, _lastText, StringComparison.Ordinal))
            {
                _lastText = text;
                if (text.Length > 0)
                {
                    events.Add(new AsrPartial(_role, text, isFinal: false, _utteranceStart, frameEnd));
                }
            }

            if (_endpointEnabled && _recognizer.IsEndpoint(_stream))
            {
                if (text.Length > 0)
                {
                    events.Add(new AsrPartial(_role, text, isFinal: true, _utteranceStart, frameEnd));
                }

                ResetStream(frameEnd);
            }

            return events;
        }
    }

    /// <summary>
    /// 结束当前流并取回最后一句（用于停止采集时）。
    /// 返回的 final 事件里 Text 是整句文本，不是增量。
    /// </summary>
    public IReadOnlyList<AsrPartial> Flush()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Array.Empty<AsrPartial>();
            }

            var events = new List<AsrPartial>(1);
            try
            {
                _stream.InputFinished();
                while (_recognizer.IsReady(_stream))
                {
                    _recognizer.Decode(_stream);
                }

                var text = (_recognizer.GetResult(_stream).Text ?? string.Empty).Trim();
                if (text.Length > 0)
                {
                    events.Add(new AsrPartial(_role, text, isFinal: true, _utteranceStart, _lastFrameEnd));
                    _lastText = string.Empty;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warning(ex, "ASR Flush 失败");
            }

            return events;
        }
    }

    /// <summary>把 16-bit little-endian PCM 转成 sherpa-onnx 需要的 float[-1, 1]。</summary>
    public static float[] Pcm16ToFloat(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = sample / 32768.0f;
        }

        return samples;
    }

    public async IAsyncEnumerable<AsrPartial> PushAsync(
        AudioFrame frame,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var partial in Process(frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return partial;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ResetStream(_lastFrameEnd);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _stream.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warning(ex, "释放 ASR 流失败");
            }

            try
            {
                _recognizer.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warning(ex, "释放 ASR 识别器失败");
            }
        }
    }

    private OnlineStream CreateStream()
    {
        var stream = _recognizer.CreateStream();
        // 流式模型开头补 0.3s 静音，避免首字被切掉
        stream.AcceptWaveform(SampleRate, new float[(int)(SampleRate * LeftPaddingSeconds)]);
        return stream;
    }

    private void ResetStream(TimeSpan nextUtteranceStart)
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // ignore
        }

        _stream = CreateStream();
        _lastText = string.Empty;
        _utteranceStart = nextUtteranceStart;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
