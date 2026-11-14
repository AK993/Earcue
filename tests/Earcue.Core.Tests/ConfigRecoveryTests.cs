using Earcue.Core.Configuration;

namespace Earcue.Core.Tests;

public class ConfigRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "earcue-config-" + Guid.NewGuid().ToString("N"));

    public ConfigRecoveryTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    public void InvalidConfig_UsesDefaultsAndPreservesOriginal(string json)
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, json);

        var config = EarcueConfig.LoadFromFile(path, out var warning);

        Assert.NotNull(warning);
        Assert.False(string.IsNullOrWhiteSpace(config.Llm.Model));
        Assert.Equal(json, File.ReadAllText(path));
        var backup = Assert.Single(Directory.GetFiles(_directory, "config.json.invalid-*"));
        Assert.Equal(json, File.ReadAllText(backup));
    }

    [Fact]
    public void NullSectionsAndStrings_AreUsableAfterLoading()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, """
            {"llm":{"apiKeyProtected":null,"model":null},"asr":{"hotwords":null},
             "autoAnswer":null,"profile":null,"overlay":null,"privacy":null,"segment":null,"mic":null,"trigger":null}
            """);

        var config = EarcueConfig.LoadFromFile(path);

        Assert.False(config.Llm.TryLoadApiKey());
        Assert.NotEmpty(config.Llm.Model);
        Assert.Empty(config.Asr.Hotwords);
        Assert.True(config.AutoAnswer.SilenceMs > 0);
        Assert.NotNull(config.Profile.Resume);
        Assert.NotNull(config.Overlay.FontFamily);
        Assert.False(config.Privacy.SaveTranscript);
        config.SaveToFile(path);
        Assert.NotNull(EarcueConfig.LoadFromFile(path).Segment);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
