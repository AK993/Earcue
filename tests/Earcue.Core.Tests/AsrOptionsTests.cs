using Earcue.Core.Configuration;
using Xunit;

namespace Earcue.Core.Tests;

public class AsrOptionsTests
{
    [Fact]
    public void Defaults_MatchLocalSherpaStreamingSetup()
    {
        var options = new AsrOptions();

        Assert.Contains("sherpa-onnx-streaming-zipformer", options.LoopbackModel);
        Assert.Equal(options.LoopbackModel, options.MicModel);
        Assert.Equal(string.Empty, options.ModelRoot);
        Assert.Equal(2, options.NumThreads);
        Assert.True(options.UseInt8);
        Assert.Equal("greedy_search", options.DecodingMethod);
    }

    [Fact]
    public void AsrOptions_RoundTripThroughConfigFile()
    {
        var config = new EarcueConfig();
        config.Asr.ModelRoot = @"D:\EarcueModels";
        config.Asr.LoopbackModel = "zh-model";
        config.Asr.MicModel = "bilingual-model";
        config.Asr.NumThreads = 4;
        config.Asr.UseInt8 = false;
        config.Asr.DecodingMethod = "modified_beam_search";
        config.Asr.Hotwords.Add("灰度发布");

        var path = Path.Combine(Path.GetTempPath(), $"earcue_asr_{Guid.NewGuid():N}.json");
        try
        {
            config.SaveToFile(path);
            var loaded = EarcueConfig.LoadFromFile(path);

            Assert.Equal(@"D:\EarcueModels", loaded.Asr.ModelRoot);
            Assert.Equal("zh-model", loaded.Asr.LoopbackModel);
            Assert.Equal("bilingual-model", loaded.Asr.MicModel);
            Assert.Equal(4, loaded.Asr.NumThreads);
            Assert.False(loaded.Asr.UseInt8);
            Assert.Equal("modified_beam_search", loaded.Asr.DecodingMethod);
            Assert.Contains("灰度发布", loaded.Asr.Hotwords);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}
