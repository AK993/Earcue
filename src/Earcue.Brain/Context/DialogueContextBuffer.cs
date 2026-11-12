using Earcue.Core.Models;

namespace Earcue.Brain.Context;

/// <summary>
/// 会话上下文滚动窗口缓冲区
/// 维护最近 90 秒的会话历史（带 [对方] / [我] 标注）
/// </summary>
public sealed class DialogueContextBuffer
{
    private readonly TimeSpan _windowDuration;
    private readonly List<DialogueTurn> _turns = new();
    private readonly object _lock = new();

    public DialogueContextBuffer(TimeSpan? windowDuration = null)
    {
        _windowDuration = windowDuration ?? TimeSpan.FromSeconds(90);
    }

    public void AddTurn(AudioRole role, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_lock)
        {
            var turn = new DialogueTurn(role, text.Trim(), DateTime.UtcNow);
            _turns.Add(turn);
            PurgeOldTurns();
        }
    }

    public IReadOnlyList<DialogueTurn> GetRecentTurns()
    {
        lock (_lock)
        {
            PurgeOldTurns();
            return _turns.ToList();
        }
    }

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
