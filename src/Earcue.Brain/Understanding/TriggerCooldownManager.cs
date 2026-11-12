using Earcue.Core.Configuration;

namespace Earcue.Brain.Understanding;

/// <summary>
/// 触发冷却与防刷屏管理器
/// </summary>
public sealed class TriggerCooldownManager
{
    private readonly TriggerOptions _options;
    private DateTime _lastTriggerTime = DateTime.MinValue;
    private string _lastTriggerQuestion = string.Empty;
    private readonly object _lock = new();

    public TriggerCooldownManager(TriggerOptions? options = null)
    {
        _options = options ?? new TriggerOptions();
    }

    /// <summary>
    /// 评估是否允许触发该问题
    /// </summary>
    public bool ShouldTrigger(string question, out string blockReason)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsedMs = (now - _lastTriggerTime).TotalMilliseconds;

            // 1. 连续触发最小间隔限制
            if (elapsedMs < _options.MinIntervalMs)
            {
                blockReason = $"连续触发过密 (距上次 {elapsedMs:0}ms < 最小间隔 {_options.MinIntervalMs}ms)";
                return false;
            }

            // 2. 相似或相同问题冷却（8秒内相同或高度相似不重复触发）
            if (elapsedMs < _options.CooldownMs)
            {
                if (IsSimilarQuestion(question, _lastTriggerQuestion))
                {
                    blockReason = $"与上一问题高度相似且在冷却期内 ({elapsedMs:0}ms < {_options.CooldownMs}ms)";
                    return false;
                }
            }

            _lastTriggerTime = now;
            _lastTriggerQuestion = question;
            blockReason = string.Empty;
            return true;
        }
    }

    private static bool IsSimilarQuestion(string q1, string q2)
    {
        if (string.Equals(q1.Trim(), q2.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        // 计算简易字符重合度
        var set1 = q1.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToHashSet();
        var set2 = q2.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToHashSet();

        if (set1.Count == 0 || set2.Count == 0) return false;

        int intersection = set1.Count(set2.Contains);
        int union = set1.Count + set2.Count - intersection;

        double jaccard = (double)intersection / union;
        return jaccard > 0.75; // 相似度阈值 75%
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastTriggerTime = DateTime.MinValue;
            _lastTriggerQuestion = string.Empty;
        }
    }
}
