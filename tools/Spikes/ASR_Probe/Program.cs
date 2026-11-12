using System.Diagnostics;
using Earcue.Core.Configuration;
using Earcue.Core.Models;
using Earcue.Speech.Asr;
using NAudio.Wave;

var root = Path.Combine(Environment.CurrentDirectory, "tools", "models");
var modelId = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";
string? wav = null;
var threads = 2;
var useInt8 = true;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root":
            root = args[++i];
            break;
        case "--model":
            modelId = args[++i];
            break;
        case "--wav":
            wav = args[++i];
            break;
        case "--threads":
            threads = int.Parse(args[++i]);
            break;
        case "--float":
            useInt8 = false;
            break;
        default:
            Console.Error.WriteLine($"未知参数: {args[i]}");
            return 2;
    }
}

if (!AsrModelLocator.TryLocate(modelId, root, useInt8, out var files, out var error) || files is null)
{
    Console.Error.WriteLine(error);
    return 2;
}

wav ??= Path.Combine(files.ModelDirectory, "test_wavs", "0.wav");
if (!File.Exists(wav))
{
    Console.Error.WriteLine($"WAV 不存在: {wav}");
    return 2;
}

using var reader = new WaveFileReader(wav);
if (reader.WaveFormat.SampleRate != 16000 ||
    reader.WaveFormat.Channels != 1 ||
    reader.WaveFormat.BitsPerSample != 16)
{
    Console.Error.WriteLine(
        $"只支持 16kHz/单声道/16-bit WAV，实际: {reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch/{reader.WaveFormat.BitsPerSample}bit");
    return 2;
}

var options = new AsrOptions
{
    LoopbackModel = modelId,
    ModelRoot = root,
    NumThreads = threads,
    UseInt8 = useInt8,
    DecodingMethod = "greedy_search"
};
var segment = new SegmentOptions { SilenceMs = 600, MaxMs = 10000 };

Console.WriteLine($"model : {files.ModelId}");
Console.WriteLine($"dir   : {files.ModelDirectory}");
Console.WriteLine($"files : {files.EncoderFileName}");
Console.WriteLine($"wav   : {wav}  ({reader.TotalTime.TotalSeconds:0.00}s)");

var initWatch = Stopwatch.StartNew();
using var engine = new SherpaAsrEngine(options, segment, AudioRole.Remote, files);
initWatch.Stop();
Console.WriteLine($"load  : {initWatch.ElapsedMilliseconds} ms");

var decodeWatch = Stopwatch.StartNew();
var finals = new List<AsrPartial>();
var frameCount = 0L;
var buffer = new byte[320];
var tailSilenceFrames = 80; // 0.8s，触发句末端点
var trailing = 0;

while (true)
{
    var read = reader.Read(buffer, 0, buffer.Length);
    if (read <= 0)
    {
        if (trailing++ >= tailSilenceFrames)
        {
            break;
        }

        Array.Clear(buffer);
    }
    else if (read < buffer.Length)
    {
        Array.Clear(buffer, read, buffer.Length - read);
    }

    var timestamp = TimeSpan.FromMilliseconds(frameCount * 10);
    var frame = new AudioFrame(AudioRole.Remote, (byte[])buffer.Clone(), 16000, 1, 16, timestamp);
    foreach (var partial in engine.Process(frame))
    {
        Print(partial, timestamp);
        if (partial.IsFinal)
        {
            finals.Add(partial);
        }
    }

    frameCount++;
}

foreach (var partial in engine.Flush())
{
    Print(partial, TimeSpan.FromMilliseconds(frameCount * 10));
    if (partial.IsFinal)
    {
        finals.Add(partial);
    }
}

decodeWatch.Stop();
var audioSeconds = frameCount * 0.01;
Console.WriteLine($"frames: {frameCount} ({audioSeconds:0.00}s audio)");
Console.WriteLine($"decode: {decodeWatch.ElapsedMilliseconds} ms  (RTF {decodeWatch.Elapsed.TotalSeconds / audioSeconds:0.000})");
Console.WriteLine("----------------------------------------");
Console.WriteLine("FINAL TEXT: " + string.Join(" | ", finals.Select(f => f.Text)));

if (finals.Count == 0 || finals.All(f => string.IsNullOrWhiteSpace(f.Text)))
{
    Console.Error.WriteLine("没有识别出任何文本");
    return 1;
}

return 0;

static void Print(AsrPartial partial, TimeSpan now)
{
    var tag = partial.IsFinal ? "FINAL" : "draft";
    Console.WriteLine($"[{now.TotalSeconds,7:0.00}s] {tag,-5}  {partial.Text}");
}
