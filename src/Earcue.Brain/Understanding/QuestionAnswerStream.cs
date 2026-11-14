using System.Text;
using System.Text.Json;

namespace Earcue.Brain.Understanding;

public sealed record QuestionExtraction(IReadOnlyList<string> Questions, bool Uncertain)
{
    public string Text => string.Join("\n", Questions.Select((question, index) =>
        Questions.Count > 1 ? $"{index + 1}. {question}" : question));
}

/// <summary>Separates question metadata from the visible answer without a second model request.</summary>
public sealed class QuestionAnswerStream
{
    public const string QuestionMarker = "[EARCUE_QUESTION]";
    public const string AnswerMarker = "[EARCUE_ANSWER]";
    private const int MaxHeaderChars = 4096;
    private readonly StringBuilder _header = new();
    private readonly StringBuilder _answer = new();
    private bool _readingAnswer;
    private bool _structured;
    private bool _completed;
    private bool _outerFence;
    private bool _trimAnswerStart;
    public QuestionExtraction? Extraction { get; private set; }
    public bool UsedFallback { get; private set; }
    public string Answer => _answer.ToString();

    public void Push(string chunk)
    {
        if (_completed) throw new InvalidOperationException("回答流已结束。");
        if (_readingAnswer) { AppendAnswer(chunk); return; }
        _header.Append(chunk);
        var text = _header.ToString().TrimStart();
        if (text.Length == 0 || "```".StartsWith(text, StringComparison.Ordinal))
        {
            CheckHeaderLength();
            return;
        }
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            if (newline < 0 && text.Length < 32) return;
            if (newline >= 0)
            {
                var insideFence = text[(newline + 1)..].TrimStart();
                if (QuestionMarker.StartsWith(insideFence, StringComparison.Ordinal)) { CheckHeaderLength(); return; }
                if (insideFence.StartsWith(QuestionMarker, StringComparison.Ordinal))
                {
                    text = insideFence;
                    _outerFence = true;
                }
            }
        }
        if (QuestionMarker.StartsWith(text, StringComparison.Ordinal)) { CheckHeaderLength(); return; }
        if (!text.StartsWith(QuestionMarker, StringComparison.Ordinal))
        {
            // Older/nonconforming models can still provide a plain answer.
            UsedFallback = true;
            _readingAnswer = true;
            _answer.Append(_header);
            _header.Clear();
            return;
        }

        _structured = true;
        var split = FindAnswerMarker(text, out var closedMetadataFence);
        if (split < 0)
        {
            if (text.Length > MaxHeaderChars)
                throw new InvalidDataException("问题整理结果过长，请重试或关闭自动问题校正。");
            return;
        }
        if (split > MaxHeaderChars)
            throw new InvalidDataException("问题整理结果过长，请重试或关闭自动问题校正。");

        var metadata = text[QuestionMarker.Length..split].Trim();
        if (closedMetadataFence)
        {
            metadata = metadata[..^3].TrimEnd();
            _outerFence = false;
        }
        Extraction = ParseExtraction(metadata);
        UsedFallback = Extraction is null;
        _readingAnswer = true;
        _trimAnswerStart = true;
        AppendAnswer(text[(split + AnswerMarker.Length)..]);
        _header.Clear();
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        if (!_readingAnswer)
        {
            if (_structured || _header.ToString().Contains("[EARCUE_", StringComparison.Ordinal))
                throw new InvalidDataException("问题整理未完成，模型没有返回回答，请重试。");
            UsedFallback = true;
            _answer.Append(_header);
            _header.Clear();
        }
        if (_outerFence)
        {
            var answer = _answer.ToString().TrimEnd();
            if (answer.EndsWith("```", StringComparison.Ordinal))
                _answer.Clear().Append(answer[..^3].TrimEnd());
        }
        if (string.IsNullOrWhiteSpace(Answer))
            throw new InvalidDataException("模型未返回有效回答，请重试。");
    }

    private static QuestionExtraction? ParseExtraction(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text.Trim());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() > 4 ||
                !root.TryGetProperty("uncertain", out var uncertain) ||
                uncertain.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var questions = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) return null;
                var question = entry.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(question) || question.Length > 500) return null;
                if (!questions.Contains(question, StringComparer.Ordinal)) questions.Add(question);
            }
            return new QuestionExtraction(questions, uncertain.GetBoolean());
        }
        catch (JsonException) { return null; }
    }

    private void CheckHeaderLength()
    {
        if (_header.Length > MaxHeaderChars)
            throw new InvalidDataException("问题整理结果过长，请重试或关闭自动问题校正。");
    }

    private void AppendAnswer(string text)
    {
        if (_trimAnswerStart)
        {
            text = text.TrimStart('\r', '\n');
            if (text.Length > 0) _trimAnswerStart = false;
        }
        _answer.Append(text);
    }

    private static int FindAnswerMarker(string text, out bool closedMetadataFence)
    {
        closedMetadataFence = false;
        // Read the JSON value first so a quoted marker inside a question is ordinary data.
        var bytes = Encoding.UTF8.GetBytes(text[QuestionMarker.Length..]);
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
            if (!JsonDocument.TryParseValue(ref reader, out var document)) return -1;
            document!.Dispose();
            var start = QuestionMarker.Length + Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)reader.BytesConsumed));
            var tail = text[start..].TrimStart();
            if (tail.StartsWith("```", StringComparison.Ordinal))
            {
                closedMetadataFence = true;
                tail = tail[3..].TrimStart();
            }
            return tail.StartsWith(AnswerMarker, StringComparison.Ordinal)
                ? text.IndexOf(AnswerMarker, start, StringComparison.Ordinal) : -1;
        }
        catch (JsonException)
        {
            // Invalid metadata may fall back to the original question; only accept a standalone delimiter.
            var start = QuestionMarker.Length;
            while (true)
            {
                var index = text.IndexOf(AnswerMarker, start, StringComparison.Ordinal);
                if (index < 0) return -1;
                var lineStart = text.LastIndexOf('\n', index - 1) + 1;
                if (string.IsNullOrWhiteSpace(text[lineStart..index])) return index;
                start = index + AnswerMarker.Length;
            }
        }
    }
}
