using Earcue.Audio.Processing;

namespace Earcue.Audio.Tests;

public class StreamingResamplerTests
{
    [Theory]
    [InlineData(44100, 2, true, 8192)]
    [InlineData(48000, 2, true, 8192)]
    [InlineData(48000, 6, true, 8192)]
    [InlineData(44100, 2, false, 1001)]
    [InlineData(16000, 1, false, 1)]
    public void Chunking_DoesNotChangeSamplesOrDuration(int rate, int channels, bool isFloat, int chunkSize)
    {
        var source = new byte[rate * channels * (isFloat ? 4 : 2)];
        for (var frame = 0; frame < rate; frame++)
        for (var channel = 0; channel < channels; channel++)
        {
            var value = (float)(0.5 * Math.Sin(2 * Math.PI * (220 + channel * 50) * frame / rate));
            var sample = isFloat ? BitConverter.GetBytes(value) : BitConverter.GetBytes((short)(value * 32767));
            sample.CopyTo(source, (frame * channels + channel) * sample.Length);
        }
        var whole = new AudioFormatConverter(rate, channels, isFloat).ConvertTo16kMonoPcm(source, source.Length);
        var converter = new AudioFormatConverter(rate, channels, isFloat);
        var chunked = new List<byte>();
        for (var offset = 0; offset < source.Length; offset += chunkSize)
        {
            var chunk = source.AsSpan(offset, Math.Min(chunkSize, source.Length - offset)).ToArray();
            chunked.AddRange(converter.ConvertTo16kMonoPcm(chunk, chunk.Length));
        }

        Assert.Equal(32000, whole.Length);
        Assert.Equal(whole, chunked.ToArray());
    }
}
