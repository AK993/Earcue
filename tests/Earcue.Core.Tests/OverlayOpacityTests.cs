using System.Text.Json;
using Earcue.Core.Configuration;

namespace Earcue.Core.Tests;

public class OverlayOpacityTests
{
    [Fact]
    public void LegacyOpacityPreservesTextAndEffectiveBackgroundAlpha()
    {
        var options = JsonSerializer.Deserialize<OverlayOptions>("{\"opacity\":0.75}")!;
        Assert.Equal(0.75, options.TextOpacity);
        Assert.Equal(0.75 * 236 / 255, options.BackgroundOpacity, 10);
    }

    [Theory]
    [InlineData("{\"opacity\":0.4,\"backgroundOpacity\":0.2,\"textOpacity\":0.9}")]
    [InlineData("{\"backgroundOpacity\":0.2,\"textOpacity\":0.9,\"opacity\":0.4}")]
    public void ExplicitLayerOpacityWinsOverLegacyRegardlessOfJsonOrder(string json)
    {
        var options = JsonSerializer.Deserialize<OverlayOptions>(json)!;
        Assert.Equal(0.2, options.BackgroundOpacity);
        Assert.Equal(0.9, options.TextOpacity);
    }

    [Fact]
    public void PartiallyMigratedConfigurationFallsBackOnlyForMissingLayer()
    {
        var options = JsonSerializer.Deserialize<OverlayOptions>("{\"opacity\":0.6,\"backgroundOpacity\":0}")!;
        Assert.Equal(0, options.BackgroundOpacity);
        Assert.Equal(0.6, options.TextOpacity);
    }

    [Fact]
    public void LayerValuesAreIndependentAndSupportFullyTransparentAndOpaque()
    {
        var options = new OverlayOptions { BackgroundOpacity = 0, TextOpacity = 1 };
        Assert.Equal(0, options.BackgroundOpacity);
        Assert.Equal(1, options.TextOpacity);
        options.TextOpacity = 0;
        options.BackgroundOpacity = 1;
        Assert.Equal(1, options.BackgroundOpacity);
        Assert.Equal(0, options.TextOpacity);
    }

    [Fact]
    public void IndependentValuesSurviveConfigSaveAndReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"earcue_opacity_{Guid.NewGuid():N}.json");
        try
        {
            var config = new EarcueConfig();
            config.Overlay.BackgroundOpacity = 0.25;
            config.Overlay.TextOpacity = 0.8;
            config.SaveToFile(path);
            var loaded = EarcueConfig.LoadFromFile(path);
            Assert.Equal(0.25, loaded.Overlay.BackgroundOpacity);
            Assert.Equal(0.8, loaded.Overlay.TextOpacity);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(-0.1, 0)]
    [InlineData(1.1, 1)]
    public void OutOfRangeLayerValuesAreClamped(double input, double expected)
    {
        var options = new OverlayOptions { BackgroundOpacity = input, TextOpacity = input };
        Assert.Equal(expected, options.BackgroundOpacity);
        Assert.Equal(expected, options.TextOpacity);
    }

    [Fact]
    public void NonFiniteLayerValuesCannotBreakWpfOpacity()
    {
        var options = new OverlayOptions { BackgroundOpacity = double.NaN, TextOpacity = double.PositiveInfinity };
        Assert.InRange(options.BackgroundOpacity, 0, 1);
        Assert.InRange(options.TextOpacity, 0, 1);
    }
}
