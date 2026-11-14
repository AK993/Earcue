using System.Text;

namespace Earcue.Core.Configuration;

/// <summary>User vocabulary shared by native recognition and question correction.</summary>
public static class ProfessionalVocabulary
{
    public const int MaxWords = 200;
    public const int MaxWordLength = 64;

    public static List<string> Parse(string? text) => Normalize((text ?? "").Split('\n'));

    public static List<string> Normalize(IEnumerable<string>? words)
    {
        var result = new List<string>();
        foreach (var value in words ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var word = value.Trim().Normalize(NormalizationForm.FormKC);
            if (word.Length > MaxWordLength || word.Any(char.IsControl))
                throw new ArgumentException($"每个专业词最多 {MaxWordLength} 个字符，且不能包含控制字符。请每行填写一个词。");
            if (!result.Contains(word, StringComparer.OrdinalIgnoreCase)) result.Add(word);
            if (result.Count > MaxWords) throw new ArgumentException($"专业词最多填写 {MaxWords} 个，请优先保留最常用的词。");
        }
        return result;
    }
}
