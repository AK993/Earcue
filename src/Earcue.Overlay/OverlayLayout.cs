namespace Earcue.Overlay;

/// <summary>用户在编辑模式下拖动/缩放后的卡片布局。</summary>
public sealed record OverlayLayout(double Left, double Top, double Width, double MinHeight);
