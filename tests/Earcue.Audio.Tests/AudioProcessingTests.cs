using Earcue.Audio.Processing;
using Xunit;

namespace Earcue.Audio.Tests;

public class AudioProcessingTests
{
    [Fact]
    public void RingBuffer_WriteAndRead_ReturnsExactBytes()
    {
        var ringBuffer = new AudioRingBuffer(1024);
        var input = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        bool written = ringBuffer.Write(input, 0, input.Length);
        Assert.True(written);
        Assert.Equal(input.Length, ringBuffer.AvailableToRead);

        var output = new byte[input.Length];
        int read = ringBuffer.Read(output, 0, output.Length);

        Assert.Equal(input.Length, read);
        Assert.Equal(input, output);
        Assert.Equal(0, ringBuffer.AvailableToRead);
    }

    [Fact]
    public void RingBuffer_WrapAround_HandlesCorrectly()
    {
        var ringBuffer = new AudioRingBuffer(16);
        var chunk1 = new byte[10] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        ringBuffer.Write(chunk1, 0, chunk1.Length);

        var readBuf = new byte[8];
        ringBuffer.Read(readBuf, 0, 8); // read 8 bytes, leaves 2 bytes, write ptr at 10

        var chunk2 = new byte[10] { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        bool written = ringBuffer.Write(chunk2, 0, chunk2.Length); // should wrap around
        Assert.True(written);

        var finalBuf = new byte[12];
        int totalRead = ringBuffer.Read(finalBuf, 0, 12);
        Assert.Equal(12, totalRead);
        Assert.Equal(9, finalBuf[0]);
        Assert.Equal(10, finalBuf[1]);
        Assert.Equal(11, finalBuf[2]);
        Assert.Equal(20, finalBuf[11]);
    }

    [Fact]
    public void Converter_ResamplesStereoFloat48kToMono16k()
    {
        // 48kHz, 2 channels, Float32.
        // 48000 frames of 1 second = 48000 * 2 * 4 = 384,000 bytes.
        // After resampling to 16kHz mono 16-bit:
        // Expected target length = 16000 samples * 2 bytes = 32,000 bytes.
        var converter = new AudioFormatConverter(48000, 2, isFloat: true);
        var dummyData = new byte[384000];

        byte[] result = converter.ConvertTo16kMonoPcm(dummyData, dummyData.Length);
        Assert.Equal(32000, result.Length);
    }

    [Fact]
    public void RingBuffer_Clear_ResetsIndicesAndAvailableBytes()
    {
        var ringBuffer = new AudioRingBuffer(1024);
        var input = new byte[] { 10, 20, 30, 40, 50 };
        ringBuffer.Write(input, 0, input.Length);
        Assert.Equal(5, ringBuffer.AvailableToRead);

        ringBuffer.Clear();
        Assert.Equal(0, ringBuffer.AvailableToRead);

        var output = new byte[10];
        int read = ringBuffer.Read(output, 0, output.Length);
        Assert.Equal(0, read);
    }
}
