using Earcue.Core.Configuration;
using Earcue.Core.Models;

namespace Earcue.Overlay;

public sealed class OverlayManager
{
    private readonly OverlayWindow _window;
    private readonly OverlayOptions _options;

    public OverlayManager(OverlayOptions options)
    {
        _options = options;
        _window = new OverlayWindow();
        _window.Configure(_options.Opacity, _options.ExcludeFromCapture, _options.AutoFadeoutSeconds);
    }

    public void Show()
    {
        _window.Show();
    }

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
