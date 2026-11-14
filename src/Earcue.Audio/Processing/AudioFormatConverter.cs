namespace Earcue.Audio.Processing;

/// <summary>有状态的 PCM/Float 下混与线性重采样器。每个音频流使用一个实例。</summary>
public sealed class AudioFormatConverter
{
    private const int TargetSampleRate = 16000;
    private readonly int _sourceSampleRate;
    private readonly int _sourceChannels;
    private readonly bool _isFloat;
    private readonly int _blockAlign;
    private readonly byte[] _pendingBytes;
    private int _pendingCount;
    private long _sourceFrames;
    private long _nextOutputSample;
    private float _previousSample;

    public AudioFormatConverter(int sourceSampleRate, int sourceChannels, bool isFloat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);
        _sourceSampleRate = sourceSampleRate;
        _sourceChannels = sourceChannels;
        _isFloat = isFloat;
        _blockAlign = checked(sourceChannels * (isFloat ? 4 : 2));
        _pendingBytes = new byte[_blockAlign];
    }

    public byte[] ConvertTo16kMonoPcm(byte[] sourceBytes, int length)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (length < 0 || length > sourceBytes.Length) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return Array.Empty<byte>();

        var data = new byte[_pendingCount + length];
        _pendingBytes.AsSpan(0, _pendingCount).CopyTo(data);
        sourceBytes.AsSpan(0, length).CopyTo(data.AsSpan(_pendingCount));
        var frames = data.Length / _blockAlign;
        var output = new List<byte>((int)((long)frames * TargetSampleRate / _sourceSampleRate * 2 + 8));
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < _sourceChannels; channel++)
            {
                var offset = frame * _blockAlign + channel * (_isFloat ? 4 : 2);
                sum += _isFloat ? BitConverter.ToSingle(data, offset) : BitConverter.ToInt16(data, offset) / 32768f;
            }
            var sample = sum / _sourceChannels;
            if (!float.IsFinite(sample)) sample = 0;

            // 使用全流整数相位，避免每个数据块独立 Floor 导致持续丢样。
            // 前后两个源采样点跨调用保留，数据分块方式不影响输出内容。
            while (_nextOutputSample * _sourceSampleRate <= _sourceFrames * TargetSampleRate)
            {
                var fraction = (_nextOutputSample * _sourceSampleRate - (_sourceFrames - 1) * TargetSampleRate)
                    / (double)TargetSampleRate;
                var value = _sourceFrames == 0 ? sample : _previousSample + (sample - _previousSample) * fraction;
                var pcm = (short)Math.Round(Math.Clamp(value, -1, 1) * 32767);
                output.Add((byte)(pcm & 0xff));
                output.Add((byte)((pcm >> 8) & 0xff));
                _nextOutputSample++;
            }
            _previousSample = sample;
            _sourceFrames++;
        }

        _pendingCount = data.Length - frames * _blockAlign;
        data.AsSpan(frames * _blockAlign).CopyTo(_pendingBytes);
        return output.ToArray();
    }
}
