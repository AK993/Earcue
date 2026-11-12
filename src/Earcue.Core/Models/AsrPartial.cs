namespace Earcue.Core.Models;

public sealed class AsrPartial
{
    public AudioRole Role { get; }
    public string Text { get; }
    public bool IsFinal { get; }
    public TimeSpan StartTime { get; }
    public TimeSpan EndTime { get; }

    public AsrPartial(AudioRole role, string text, bool isFinal, TimeSpan startTime, TimeSpan endTime)
    {
        Role = role;
        Text = text ?? string.Empty;
        IsFinal = isFinal;
        StartTime = startTime;
        EndTime = endTime;
    }
}
