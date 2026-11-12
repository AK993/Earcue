namespace Earcue.Core.Models;

public sealed class AudioFrame
{
    public AudioRole Role { get; }
    public byte[] Data { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public TimeSpan Timestamp { get; }
    public bool IsSilence { get; }

    public AudioFrame(
        AudioRole role,
        byte[] data,
        int sampleRate,
        int channels,
        int bitsPerSample,
        TimeSpan timestamp,
        bool isSilence = false)
    {
        Role = role;
        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        Timestamp = timestamp;
        IsSilence = isSilence;
    }

    /// <summary>
    /// 获取当前音频帧的均方根能量值 (RMS)
    /// </summary>
    public double CalculateRms()
    {
        if (Data.Length == 0 || BitsPerSample != 16)
            return 0.0;

        long sumSquares = 0;
        int sampleCount = Data.Length / 2;

        for (int i = 0; i < Data.Length; i += 2)
        {
            short sample = (short)(Data[i] | (Data[i + 1] << 8));
            sumSquares += (long)sample * sample;
        }

        double meanSquare = (double)sumSquares / sampleCount;
        return Math.Sqrt(meanSquare);
    }
}
