using Earcue.Core.Models;
using System.Text;

namespace Earcue.Brain.Context;

/// <summary>
/// 会话上下文滚动窗口缓冲区
/// 保留近期实际发言，带 [对方] / [我] 标注。
/// </summary>
public sealed class DialogueContextBuffer
{
    private readonly TimeSpan _windowDuration;
    private const int MaxTurns = 100;
    private const int MaxChars = 8000;
    private readonly List<DialogueTurn> _turns = new();
    private readonly object _lock = new();

    public DialogueContextBuffer(TimeSpan? windowDuration = null)
    {
        _windowDuration = windowDuration ?? TimeSpan.FromMinutes(10);
    }

    public void AddTurn(AudioRole role, string text, DateTime? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_lock)
        {
            text = text.Trim();
            if (text.Length > MaxChars) text = text[^MaxChars..];
            var turn = new DialogueTurn(role, text, timestamp ?? DateTime.UtcNow);
            _turns.Add(turn);
            if (_turns.Count > MaxTurns) _turns.RemoveAt(0);
            PurgeOldTurns();
        }
    }

    public IReadOnlyList<DialogueTurn> GetRecentTurns()
    {
        lock (_lock)
        {
            PurgeOldTurns();
            var recent = new List<DialogueTurn>();
            var charsLeft = MaxChars;
            foreach (var turn in _turns.OrderBy(turn => turn.Timestamp).Reverse())
            {
                if (charsLeft < turn.Text.Length) break;
                recent.Add(turn);
                charsLeft -= turn.Text.Length;
            }
            recent.Reverse();
            return recent;
        }
    }

    public void RemoveLocalTurns()
    {
        lock (_lock) _turns.RemoveAll(turn => turn.Role == AudioRole.Local);
    }

    public bool IsLikelyLocalPlayback(string text, DateTime timestamp)
    {
        // Only suppress a near-simultaneous, verbatim copy of a substantial mic segment.
        // A question that refers to what I said often starts the same way but must still trigger.
        var normalized = NormalizeSpeech(text);
        if (normalized.Length < 8) return false;
        lock (_lock)
        {
            return _turns.Any(turn => turn.Role == AudioRole.Local &&
                (timestamp - turn.Timestamp).TotalMilliseconds is >= 120 and <= 1500 &&
                NormalizeSpeech(turn.Text) == normalized);
        }
    }

    private static string NormalizeSpeech(string text) =>
        string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private void PurgeOldTurns()
    {
        var cutoff = DateTime.UtcNow - _windowDuration;
        _turns.RemoveAll(t => t.Timestamp < cutoff);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _turns.Clear();
        }
    }
}
