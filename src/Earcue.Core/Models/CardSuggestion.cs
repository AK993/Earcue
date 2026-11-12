namespace Earcue.Core.Models;

public sealed class CardSuggestion
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsStreaming { get; set; } = false;
    public double LatencyMs { get; set; } = 0.0;
}
