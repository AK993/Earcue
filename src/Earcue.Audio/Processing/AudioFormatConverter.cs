namespace Earcue.Audio.Processing;

/// <summary>
/// 高性能音频格式重采样与下混器
/// 支持将系统原生混音格式（多声道、Float32/PCM16、44.1k/48k等）转换为标准 16kHz 单声道 16-bit PCM
/// </summary>
public sealed class AudioFormatConverter
{
    private readonly int _sourceSampleRate;
    private readonly int _sourceChannels;
    private readonly bool _isFloat;
    private readonly int _targetSampleRate = 16000;

    public AudioFormatConverter(int sourceSampleRate, int sourceChannels, bool isFloat)
    {
        _sourceSampleRate = sourceSampleRate;
        _sourceChannels = Math.Max(1, sourceChannels);
        _isFloat = isFloat;
    }

    /// <summary>
    /// 将原生 WASAPI 缓冲区转换为 16kHz 单声道 16-bit PCM
    /// </summary>
    public byte[] ConvertTo16kMonoPcm(byte[] sourceBytes, int length)
    {
        if (length <= 0) return Array.Empty<byte>();

        // 1. 提取单声道浮点样本 [-1.0f, 1.0f]
        float[] monoSamples;

        if (_isFloat)
        {
            int totalFloatSamples = length / 4;
            int frames = totalFloatSamples / _sourceChannels;
            monoSamples = new float[frames];

            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < _sourceChannels; c++)
                {
                    int offset = (f * _sourceChannels + c) * 4;
                    float sample = BitConverter.ToSingle(sourceBytes, offset);
                    sum += sample;
                }
                monoSamples[f] = sum / _sourceChannels;
            }
        }
        else
        {
            // 假设 16-bit PCM
            int totalShortSamples = length / 2;
            int frames = totalShortSamples / _sourceChannels;
            monoSamples = new float[frames];

            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < _sourceChannels; c++)
                {
                    int offset = (f * _sourceChannels + c) * 2;
                    short sample = (short)(sourceBytes[offset] | (sourceBytes[offset + 1] << 8));
                    sum += sample / 32768.0f;
                }
                monoSamples[f] = sum / _sourceChannels;
            }
        }

        // 2. 线性插值重采样至 16kHz
        double ratio = (double)_sourceSampleRate / _targetSampleRate;
        int targetLength = (int)Math.Floor(monoSamples.Length / ratio);
        var targetPcm = new byte[targetLength * 2];

        for (int i = 0; i < targetLength; i++)
        {
            double srcIdx = i * ratio;
            int idxFloor = (int)Math.Floor(srcIdx);
            int idxCeil = Math.Min(idxFloor + 1, monoSamples.Length - 1);
            double frac = srcIdx - idxFloor;

            float sample = (float)((1.0 - frac) * monoSamples[idxFloor] + frac * monoSamples[idxCeil]);

            // 夹取限幅 [-1.0, 1.0]
            if (sample > 1.0f) sample = 1.0f;
            else if (sample < -1.0f) sample = -1.0f;

            short pcmSample = (short)Math.Round(sample * 32767.0f);
            targetPcm[i * 2] = (byte)(pcmSample & 0xFF);
            targetPcm[i * 2 + 1] = (byte)((pcmSample >> 8) & 0xFF);
        }

        return targetPcm;
    }
}
