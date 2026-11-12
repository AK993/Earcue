namespace Earcue.Core.Models;

public sealed class DialogueTurn
{
    public AudioRole Role { get; }
    public string Text { get; }
    public DateTime Timestamp { get; }

    public DialogueTurn(AudioRole role, string text, DateTime timestamp)
    {
        Role = role;
        Text = text ?? string.Empty;
        Timestamp = timestamp;
    }

    public override string ToString()
    {
        var roleTag = Role == AudioRole.Remote ? "[对方]" : "[我]";
        return $"{roleTag} {Text}";
    }
}
