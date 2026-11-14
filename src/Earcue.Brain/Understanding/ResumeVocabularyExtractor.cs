using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Earcue.Core.Configuration;
using Earcue.Core.Interfaces;
using Earcue.Core.Models;

namespace Earcue.Brain.Understanding;

public sealed record VocabularyMergeResult(IReadOnlyList<string> Words, int Added, int Omitted);

/// <summary>Extracts a bounded vocabulary and verifies that every returned term occurs in the resume.</summary>
public sealed class ResumeVocabularyExtractor(ILlmClient client)
{
    public const int MaxResumeLength = 30_000;
    public const int MaxExtractedWords = 80;
    private const int MaxResponseLength = 65_536;

    public async Task<IReadOnlyList<string>> ExtractAsync(string resume, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(resume)) throw new ArgumentException("请先在【面试资料】中填写并保存简历，再提取专业词。");
        if (resume.Length > MaxResumeLength) throw new ArgumentException($"简历超过 {MaxResumeLength:N0} 字符，请精简后再提取专业词。");
        var response = new StringBuilder();
        await foreach (var chunk in client.AskAsync(new Prompt { Mode = PromptMode.ResumeVocabulary, ResumeText = resume }, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (response.Length + chunk.Length > MaxResponseLength) throw new InvalidDataException("模型返回的词表过长，请重新提取。");
            response.Append(chunk);
        }
        ct.ThrowIfCancellationRequested();
        return Parse(response.ToString(), resume);
    }

    public static IReadOnlyList<string> Parse(string response, string resume)
    {
        var text = response.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0 && text.EndsWith("```", StringComparison.Ordinal)) text = text[(newline + 1)..^3].Trim();
        }
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("terms", out var terms) || terms.ValueKind != JsonValueKind.Array ||
                terms.GetArrayLength() > MaxExtractedWords)
                throw new InvalidDataException("模型未返回有效的专业词表，请重新提取。");
            var source = NormalizeText(resume);
            var result = new List<string>();
            foreach (var entry in terms.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) throw new InvalidDataException("专业词表格式无效，请重新提取。");
                var term = entry.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(term)) continue;
                term = NormalizeText(term);
                if (term.Length > ProfessionalVocabulary.MaxWordLength || term.Contains('@') ||
                    term.Contains("://", StringComparison.Ordinal) || Regex.IsMatch(term, @"^\+?[\d\s()\-]{7,}$")) continue;
                var boundaryBefore = IsIdentifierEdge(term[0]) ? @"(?<![A-Za-z0-9_+#.])" : "";
                var boundaryAfter = IsIdentifierEdge(term[^1]) ? @"(?![A-Za-z0-9_+#.])" : "";
                if (!Regex.IsMatch(source, boundaryBefore + Regex.Escape(term) + boundaryAfter, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                if (!result.Contains(term, StringComparer.OrdinalIgnoreCase)) result.Add(term);
            }
            return ProfessionalVocabulary.Normalize(result);
        }
        catch (JsonException error) { throw new InvalidDataException("模型返回的专业词表不完整或格式无效，请重新提取。", error); }
    }

    public static VocabularyMergeResult Merge(IEnumerable<string> existing, IEnumerable<string> extracted)
    {
        var merged = ProfessionalVocabulary.Normalize(existing);
        var added = 0;
        var omitted = 0;
        foreach (var term in ProfessionalVocabulary.Normalize(extracted))
        {
            if (merged.Contains(term, StringComparer.OrdinalIgnoreCase)) continue;
            if (merged.Count >= ProfessionalVocabulary.MaxWords) { omitted++; continue; }
            merged.Add(term);
            added++;
        }
        return new(merged, added, omitted);
    }

    private static bool IsIdentifierEdge(char value) => char.IsAsciiLetterOrDigit(value) || "_+#.".Contains(value);
    private static string NormalizeText(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
}
