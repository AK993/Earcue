using System.Text;
using System.Text.RegularExpressions;
using Earcue.Core.Configuration;
using SherpaOnnx;

namespace Earcue.Speech.Asr;

public sealed record AsrHotwordResult(int Applied, int Skipped);

public static class AsrHotwordConfiguration
{
    public static AsrHotwordResult Apply(ref OnlineRecognizerConfig config, AsrOptions options, AsrModelFiles files)
    {
        var words = ProfessionalVocabulary.Normalize(options.Hotwords);
        if (words.Count == 0) return new(0, 0);
        if (files.ModelType != AsrModelType.Transducer) return new(0, words.Count);

        var bilingual = File.Exists(Path.Combine(files.ModelDirectory, "bpe.vocab"));
        var symbols = File.ReadLines(files.Tokens)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .ToHashSet(StringComparer.Ordinal);
        // Skip unsupported symbols rather than letting the native tokenizer receive invalid phrases.
        var supported = words.Select(w => w.ToUpperInvariant()).Where(word =>
            Regex.IsMatch(word, @"^[\p{L}\p{N} '\-]+$") && word.EnumerateRunes().All(rune =>
                Rune.IsWhiteSpace(rune) || (bilingual && rune.IsAscii) || (!rune.IsAscii && symbols.Contains(rune.ToString())))).ToList();
        if (supported.Count == 0) return new(0, words.Count);

        config.DecodingMethod = "modified_beam_search";
        config.ModelConfig.ModelingUnit = bilingual ? "cjkchar+bpe" : "cjkchar";
        if (bilingual) config.ModelConfig.BpeVocab = Path.Combine(files.ModelDirectory, "bpe.vocab");
        config.HotwordsBuf = string.Join("\n", supported) + "\n";
        config.HotwordsBufSize = Encoding.UTF8.GetByteCount(config.HotwordsBuf);
        config.HotwordsScore = float.IsFinite(options.HotwordsScore) ? Math.Clamp(options.HotwordsScore, 0.5f, 5f) : 1.5f;
        return new(supported.Count, words.Count - supported.Count);
    }
}
