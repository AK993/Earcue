using System.Diagnostics;
using Earcue.Audio.Sources;
using Earcue.Core.Models;

Console.WriteLine("==================================================");
Console.WriteLine(" 附耳 Earcue · S1 环回采集与静音对齐验证 Spike");
Console.WriteLine("==================================================");
Console.WriteLine("按 Ctrl+C 退出测试\n");

var source = new WasapiLoopbackSource();
long frameCount = 0;
long silenceFrameCount = 0;
long speechFrameCount = 0;
var stopwatch = Stopwatch.StartNew();
var lastPrint = Stopwatch.StartNew();

source.FrameReady += (AudioFrame frame) =>
{
    Interlocked.Increment(ref frameCount);
    if (frame.IsSilence)
    {
        Interlocked.Increment(ref silenceFrameCount);
    }
    else
    {
        Interlocked.Increment(ref speechFrameCount);
    }

    if (lastPrint.ElapsedMilliseconds >= 1000)
    {
        lastPrint.Restart();
        double rms = frame.CalculateRms();
        double elapsedSec = stopwatch.Elapsed.TotalSeconds;
        long expectedFrames = (long)(elapsedSec * 100); // 100 frames/sec (10ms each)
        long driftFrames = Math.Abs(frameCount - expectedFrames);
        double driftMs = driftFrames * 10.0;

        Console.WriteLine($"[{elapsedSec:000.0}s] 累计帧: {frameCount} (有效音: {speechFrameCount}, 补静音: {silenceFrameCount}) | 当前RMS: {rms:0.0} | 漂移估算: {driftMs:0.0}ms");
    }
};

try
{
    source.Start();
    Console.WriteLine("WASAPI 环回采集中... 请播放会议音频或保持静音观察。");
    
    // 如果是自动化环境，运行 3 秒钟快速自检
    if (args.Length > 0 && args[0] == "--ci")
    {
        Thread.Sleep(3000);
        Console.WriteLine("CI 自动化验证完成，退出。");
    }
    else
    {
        while (true)
        {
            Thread.Sleep(500);
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"异常: {ex.Message}");
}
finally
{
    source.Stop();
    source.Dispose();
}
