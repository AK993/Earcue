using Earcue.Speech.Asr;
using Xunit;

namespace Earcue.Speech.Tests;

public class AsrModelTests : IDisposable
{
    private readonly string _tempRoot;

    public AsrModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "earcue_asr_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Catalog_LoadsOfficialModelsWithChecksums()
    {
        var all = AsrModelCatalog.All;
        Assert.True(all.Count >= 2, "模型清单应至少包含中文与中英双语两个模型");

        var zh = AsrModelCatalog.Find("sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23");
        Assert.NotNull(zh);
        Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/", zh!.Url);
        Assert.Equal(64, zh.Sha256.Length);
        Assert.True(zh.SizeBytes > 0);
        Assert.Contains("中文", zh.DisplayName);
    }

    [Fact]
    public void Catalog_FindIsCaseInsensitive()
    {
        var model = AsrModelCatalog.Find("SHERPA-ONNX-STREAMING-ZIPFORMER-ZH-14M-2023-02-23");
        Assert.NotNull(model);
    }

    [Fact]
    public void DefaultModelRoot_IsUnderLocalAppData()
    {
        var root = AsrModelLocator.ResolveRoot(null);
        Assert.Contains("Earcue", root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("models", root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(root), root);
    }

    [Fact]
    public void ResolveRoot_ExpandsEnvironmentVariables()
    {
        var configured = Path.Combine("%TEMP%", "earcue_asr_models");
        var root = AsrModelLocator.ResolveRoot(configured);
        Assert.DoesNotContain("%TEMP%", root);
        Assert.EndsWith("earcue_asr_models", root);
    }

    [Fact]
    public void ResolveRoot_AnchorsRelativePathsToAppDirectory()
    {
        var root = AsrModelLocator.ResolveRoot("my-models");
        Assert.True(Path.IsPathRooted(root));
        Assert.StartsWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), root);
    }

    [Fact]
    public void Locate_PrefersInt8WhenRequestedAndFallsBackToFloat()
    {
        var id = "test-model";
        var dir = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "<blk> 0\n");
        File.WriteAllBytes(Path.Combine(dir, "encoder-epoch-99-avg-1.onnx"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "encoder-epoch-99-avg-1.int8.onnx"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(dir, "decoder-epoch-99-avg-1.onnx"), new byte[] { 3 });
        File.WriteAllBytes(Path.Combine(dir, "decoder-epoch-99-avg-1.int8.onnx"), new byte[] { 4 });
        File.WriteAllBytes(Path.Combine(dir, "joiner-epoch-99-avg-1.onnx"), new byte[] { 5 });
        File.WriteAllBytes(Path.Combine(dir, "joiner-epoch-99-avg-1.int8.onnx"), new byte[] { 6 });

        Assert.True(AsrModelLocator.TryLocate(id, _tempRoot, true, out var int8, out var error), error);
        Assert.NotNull(int8);
        Assert.True(int8!.IsInt8);
        Assert.EndsWith(".int8.onnx", int8.Encoder);
        Assert.EndsWith(".int8.onnx", int8.Decoder);
        Assert.EndsWith(".int8.onnx", int8.Joiner);

        Assert.True(AsrModelLocator.TryLocate(id, _tempRoot, false, out var floatModel, out error), error);
        Assert.NotNull(floatModel);
        Assert.False(floatModel!.IsInt8);
        Assert.EndsWith("encoder-epoch-99-avg-1.onnx", floatModel.Encoder);
    }

    [Fact]
    public void Locate_RejectsPathTraversalAndReportsMissingDirectory()
    {
        Assert.False(AsrModelLocator.TryLocate("..\\secrets", _tempRoot, true, out _, out var traversalError));
        Assert.Contains("不合法", traversalError);

        Assert.False(AsrModelLocator.TryLocate("missing-model", _tempRoot, true, out _, out var missingError));
        Assert.Contains("未找到", missingError);
        Assert.Contains("download-asr-models", missingError);
    }

    [Fact]
    public void Locate_ReportsIncompleteModel()
    {
        var id = "broken-model";
        var dir = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "<blk> 0\n");
        File.WriteAllBytes(Path.Combine(dir, "encoder-x.onnx"), new byte[] { 1 });

        Assert.False(AsrModelLocator.TryLocate(id, _tempRoot, true, out _, out var error));
        Assert.Contains("decoder", error);
        Assert.Contains("joiner", error);
    }

    [Fact]
    public void Pcm16ToFloat_ConvertsFullScaleAndSilence()
    {
        var pcm = new byte[]
        {
            0x00, 0x00,       // 0
            0xFF, 0x7F,       // 32767
            0x00, 0x80        // -32768
        };

        var samples = SherpaAsrEngine.Pcm16ToFloat(pcm);

        Assert.Equal(3, samples.Length);
        Assert.Equal(0f, samples[0]);
        Assert.InRange(samples[1], 0.9999f, 1.0f);
        Assert.Equal(-1.0f, samples[2]);
    }

    [Fact]
    public void ModelFiles_IsCompleteRequiresAllFourFiles()
    {
        var files = new AsrModelFiles
        {
            ModelId = "x",
            ModelDirectory = _tempRoot,
            Encoder = Path.Combine(_tempRoot, "encoder.onnx"),
            Decoder = Path.Combine(_tempRoot, "decoder.onnx"),
            Joiner = Path.Combine(_tempRoot, "joiner.onnx"),
            Tokens = Path.Combine(_tempRoot, "tokens.txt")
        };

        Assert.False(files.IsComplete);

        File.WriteAllBytes(files.Encoder, new byte[] { 1 });
        File.WriteAllBytes(files.Decoder, new byte[] { 2 });
        File.WriteAllBytes(files.Joiner, new byte[] { 3 });
        File.WriteAllText(files.Tokens, "<blk> 0\n");

        Assert.True(files.IsComplete);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败不影响结果
        }
    }
}
