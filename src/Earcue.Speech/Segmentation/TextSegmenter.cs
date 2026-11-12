using System.Text;
using Earcue.Core.Configuration;

namespace Earcue.Speech.Segmentation;

/// <summary>
/// 智能断句器
/// 依据：静音 ≥ 600ms 或 时长 ≥ 10s 或 句末强标点 (。！？) 三者取先切分完整句子
/// </summary>
public sealed class TextSegmenter
{
    private readonly SegmentOptions _options;
    private readonly StringBuilder _currentBuffer = new();
    private DateTime _segmentStartTime = DateTime.UtcNow;
    private DateTime _lastSpeechTime = DateTime.UtcNow;

    public event Action<string>? SentenceCompleted;

    public TextSegmenter(SegmentOptions? options = null)
    {
        _options = options ?? new SegmentOptions();
    }

    /// <summary>
    /// 接收增量转录文本
    /// </summary>
    public void PushText(string text, bool isSilence)
    {
        var now = DateTime.UtcNow;

        if (isSilence)
        {
            var silenceDurationMs = (now - _lastSpeechTime).TotalMilliseconds;
            if (silenceDurationMs >= _options.SilenceMs && _currentBuffer.Length > 0)
            {
                FlushSegment();
            }
            return;
        }

        _lastSpeechTime = now;
        if (!string.IsNullOrEmpty(text))
        {
            _currentBuffer.Append(text);
        }

        // 检查是否达到最长持续时间 (例如 10s)
        var segmentDurationMs = (now - _segmentStartTime).TotalMilliseconds;
        if (segmentDurationMs >= _options.MaxMs && _currentBuffer.Length > 0)
        {
            FlushSegment();
            return;
        }

        // 检查标点切分
        var bufferStr = _currentBuffer.ToString();
        if (bufferStr.EndsWith("？") || bufferStr.EndsWith("?") ||
            bufferStr.EndsWith("。") || bufferStr.EndsWith("！") || bufferStr.EndsWith("!"))
        {
            FlushSegment();
        }
    }

    private void FlushSegment()
    {
        var sentence = _currentBuffer.ToString().Trim();
        _currentBuffer.Clear();
        _segmentStartTime = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(sentence))
        {
            SentenceCompleted?.Invoke(sentence);
        }
    }

    public void Reset()
    {
        _currentBuffer.Clear();
        _segmentStartTime = DateTime.UtcNow;
        _lastSpeechTime = DateTime.UtcNow;
    }
}
