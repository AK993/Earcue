namespace Earcue.Core.Configuration;

public sealed class OverlayPositionPreset
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>浮层四个角的预设位置。</summary>
public static class OverlayPositionPresets
{
    public const string BottomLeft = "BottomLeft";
    public const string BottomRight = "BottomRight";
    public const string TopLeft = "TopLeft";
    public const string TopRight = "TopRight";
    public const string Custom = "Custom";

    public static IReadOnlyList<OverlayPositionPreset> All { get; } = new List<OverlayPositionPreset>
    {
        new() { Id = BottomLeft, DisplayName = "左下角（默认）" },
        new() { Id = BottomRight, DisplayName = "右下角" },
        new() { Id = TopLeft, DisplayName = "左上角" },
        new() { Id = TopRight, DisplayName = "右上角" },
        new() { Id = Custom, DisplayName = "自定义（鼠标拖动）" }
    };

    public static OverlayPositionPreset Find(string? id)
    {
        foreach (var preset in All)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return All[0];
    }
}
