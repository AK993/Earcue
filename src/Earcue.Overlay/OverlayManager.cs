using Earcue.Core.Configuration;
using Earcue.Core.Models;

namespace Earcue.Overlay;

public sealed class OverlayManager
{
    private readonly OverlayWindow _window;
    private OverlayOptions _options;

    public OverlayManager(OverlayOptions options)
    {
        _options = options ?? new OverlayOptions();
        _window = new OverlayWindow();
        _window.Configure(_options);
        _window.LayoutChanged += layout => LayoutChanged?.Invoke(layout);
    }

    public event Action<OverlayLayout>? LayoutChanged;

    public void Show()
    {
        _window.Show();
        // 启动时不要显示 XAML 里的示例卡片；第一次回答/预览/调整模式时再显示
        _window.HideImmediately();
    }

    public void UpdateOptions(OverlayOptions options)
    {
        _options = options ?? new OverlayOptions();
        _window.Configure(_options);
    }

    public void SetEditMode(bool enabled)
    {
        _window.SetEditMode(enabled);
    }

    public bool IsEditMode => _window.IsEditMode;

    public void Hide()
    {
        _window.FadeOut();
    }

    public void ShowSuggestion(CardSuggestion card)
    {
        _window.DisplayCard($"[对方] {card.Question}", card.Answer, card.LatencyMs / 1000.0);
    }

    public void UpdateAnswerStream(string answer)
    {
        _window.UpdateStreamingAnswer(answer);
    }

    public void Close()
    {
        _window.Close();
    }
}
