namespace Earcue.Brain.Context;

public sealed record PendingSpeechSnapshot(long ThroughId, string Text);

/// <summary>Preserves a whole pending turn across ASR endpoints and acknowledges only its snapshot.</summary>
public sealed class PendingSpeechBuffer
{
    private readonly Queue<(long Id, string Text)> _segments = new();
    private readonly int _maxChars;
    private long _nextId;
    public int CharacterCount { get; private set; }

    public PendingSpeechBuffer(int maxChars = 12000) => _maxChars = Math.Max(1, maxChars);

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.Length > _maxChars) text = text[^_maxChars..];
        _segments.Enqueue((++_nextId, text));
        CharacterCount += text.Length;
        while (CharacterCount > _maxChars && _segments.Count > 1)
            CharacterCount -= _segments.Dequeue().Text.Length;
    }

    public PendingSpeechSnapshot Snapshot() => new(_nextId, string.Join("\n", _segments.Select(segment => segment.Text)));

    public void Acknowledge(PendingSpeechSnapshot snapshot)
    {
        while (_segments.TryPeek(out var first) && first.Id <= snapshot.ThroughId)
            CharacterCount -= _segments.Dequeue().Text.Length;
    }

    public void Clear()
    {
        _segments.Clear();
        CharacterCount = 0;
        // IDs remain monotonic so an old request cannot acknowledge a later turn.
    }
}
