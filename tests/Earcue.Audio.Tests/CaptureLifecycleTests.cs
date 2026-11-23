using Earcue.Audio.Sources;
using Earcue.Core.Models;
using NAudio.Wave;

namespace Earcue.Audio.Tests;

public class CaptureLifecycleTests
{
    [Fact]
    public async Task ExhaustedReconnect_StopsSourceAndReportsFailure()
    {
        var device = new FakeCapture();
        var attempts = 0;
        using var source = new WasapiLoopbackSource(() => ++attempts == 1 ? device : throw new IOException("设备不可用"),
            TimeSpan.Zero, 3);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.CaptureFailed += error => failure.TrySetResult(error);
        source.Start();

        device.Disconnect();
        var error = await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(source.IsRunning);
        Assert.Equal(4, attempts);
        Assert.True(device.Disposed);
        Assert.Contains("重连失败", error.Message);
        source.Stop(); // 失败后仍可安全清理。
    }

    [Theory]
    [InlineData(AudioRole.Remote)]
    [InlineData(AudioRole.Local)]
    public async Task Reconnect_PreservesSpeakerRoleAndIncreasingTimestamps(AudioRole role)
    {
        var first = new FakeCapture();
        var second = new FakeCapture();
        var attempts = 0;
        using var source = new WasapiLoopbackSource(() => ++attempts == 1 ? first : second, TimeSpan.Zero, 3, role);
        var before = new TaskCompletionSource<AudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = new TaskCompletionSource<AudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = false;
        source.FrameReady += frame =>
        {
            if (Volatile.Read(ref reconnected)) after.TrySetResult(frame);
            else before.TrySetResult(frame);
        };
        source.StatusChanged += status =>
        {
            if (status.Contains("重连成功") || status == "麦克风已重连") Volatile.Write(ref reconnected, true);
        };
        source.Start();
        var initial = await before.Task.WaitAsync(TimeSpan.FromSeconds(3));
        first.Disconnect();
        var resumed = await after.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(source.IsRunning);
        Assert.True(first.Disposed);
        Assert.Equal(role, initial.Role);
        Assert.Equal(role, resumed.Role);
        Assert.True(resumed.Timestamp > initial.Timestamp);
        source.Stop();
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task WorkerException_IsReportedWithoutCrashingProcess()
    {
        using var source = new WasapiLoopbackSource(() => new FakeCapture(), TimeSpan.Zero, 1);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.FrameReady += _ => throw new InvalidOperationException("模拟识别异常");
        source.CaptureFailed += error => failure.TrySetResult(error);
        source.Start();

        await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(source.IsRunning);
        source.Stop();
    }

    private sealed class FakeCapture : ILoopbackCapture
    {
        public WaveFormat WaveFormat => new(48000, 16, 2);
        public bool Disposed { get; private set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() { }
        public void Disconnect() => RecordingStopped?.Invoke(this, new StoppedEventArgs(new IOException("断开设备")));
        public void Dispose() => Disposed = true;
    }
}
