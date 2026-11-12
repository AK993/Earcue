using Earcue.Core.Configuration;
using Earcue.Core.Security;
using Xunit;

namespace Earcue.Core.Tests;

public class LlmSettingsTests
{
    [Fact]
    public void ProviderPresets_AreWellFormedAndUnique()
    {
        Assert.NotEmpty(LlmProviderPresets.All);
        Assert.Equal(
            LlmProviderPresets.All.Count,
            LlmProviderPresets.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var preset in LlmProviderPresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Id));
            Assert.False(string.IsNullOrWhiteSpace(preset.DisplayName));

            if (preset.Id == "custom")
            {
                continue;
            }

            Assert.True(
                Uri.TryCreate(preset.BaseUrl, UriKind.Absolute, out var uri),
                $"{preset.DisplayName} 的 BaseUrl 不是绝对地址");
            Assert.True(uri!.Scheme is "http" or "https");
        }

        Assert.NotNull(LlmProviderPresets.FindById("deepseek"));
        Assert.Same(LlmProviderPresets.Custom, LlmProviderPresets.FindById("custom"));
        Assert.Same(LlmProviderPresets.Custom, LlmProviderPresets.All[^1]);
        Assert.Null(LlmProviderPresets.FindById("not-exist"));
    }

    [Fact]
    public void ProviderPresets_CanBeMatchedByBaseUrl()
    {
        var preset = LlmProviderPresets.FindByBaseUrl("https://api.deepseek.com/v1/");
        Assert.NotNull(preset);
        Assert.Equal("deepseek", preset!.Id);

        Assert.Equal("ollama", LlmProviderPresets.FindByBaseUrl("http://localhost:11434/v1")!.Id);
        Assert.Null(LlmProviderPresets.FindByBaseUrl("https://example.com/v1"));
    }

    [Fact]
    public void ResolveDisplay_UsesActualBaseUrlInsteadOfStalePreset()
    {
        Assert.Equal("deepseek", LlmProviderPresets.ResolveDisplay("deepseek", "https://api.deepseek.com/v1/").Id);
        Assert.Equal("deepseek", LlmProviderPresets.ResolveDisplay("deepseek", "https://api.deepseek.com/v1").Id);

        // 选了 DeepSeek 但填了本地代理：显示"自定义接口"，不能误报成 DeepSeek
        Assert.Equal("custom", LlmProviderPresets.ResolveDisplay("deepseek", "http://127.0.0.1:18080/v1").Id);

        // 填了别家地址：以地址为准
        Assert.Equal("openai", LlmProviderPresets.ResolveDisplay("deepseek", "https://api.openai.com/v1").Id);

        Assert.Equal("custom", LlmProviderPresets.ResolveDisplay("custom", string.Empty).Id);
        Assert.Equal("ollama", LlmProviderPresets.ResolveDisplay(null, "http://localhost:11434/v1").Id);
    }

    [Fact]
    public void ApiKey_IsPersistedOnlyAsDpapiCiphertext()
    {
        if (!ApiKeyProtector.IsSupported)
        {
            return;
        }

        const string apiKey = "sk-unit-test-secret-1234567890";
        var config = new EarcueConfig();
        config.Llm.SetApiKey(apiKey);

        Assert.True(ApiKeyProtector.IsProtected(config.Llm.ProtectedApiKey));
        Assert.True(config.Llm.HasApiKey);

        var path = Path.Combine(Path.GetTempPath(), $"earcue_llm_{Guid.NewGuid():N}.json");
        try
        {
            config.SaveToFile(path);
            var json = File.ReadAllText(path);

            // 明文 Key 绝不能出现在配置文件里
            Assert.DoesNotContain(apiKey, json);
            Assert.DoesNotContain("sk-unit-test", json);
            Assert.DoesNotContain("\"apiKey\"", json);
            Assert.Contains("apiKeyProtected", json);

            var loaded = EarcueConfig.LoadFromFile(path);
            Assert.Equal(string.Empty, loaded.Llm.ApiKey);
            Assert.True(loaded.Llm.HasApiKey);
            Assert.True(loaded.Llm.TryLoadApiKey());
            Assert.Equal(apiKey, loaded.Llm.ApiKey);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void MaskedApiKey_NeverRevealsFullKey()
    {
        var options = new LlmOptions();
        Assert.Equal("未配置", options.MaskedApiKey);

        options.SetApiKey("sk-1234567890abcdef");
        var masked = options.MaskedApiKey;

        Assert.StartsWith("sk-", masked);
        Assert.EndsWith("cdef", masked);
        Assert.DoesNotContain("1234567890", masked);
    }

    [Fact]
    public void SetApiKey_Empty_ClearsStoredCiphertext()
    {
        if (!ApiKeyProtector.IsSupported)
        {
            return;
        }

        var options = new LlmOptions();
        options.SetApiKey("sk-something-123456");
        Assert.NotEmpty(options.ProtectedApiKey);

        options.SetApiKey(string.Empty);
        Assert.Empty(options.ApiKey);
        Assert.Empty(options.ProtectedApiKey);
        Assert.False(options.HasApiKey);
    }

    [Fact]
    public void ApiKeyProtector_RejectsPlaintextAndBrokenCiphertext()
    {
        Assert.False(ApiKeyProtector.IsProtected("sk-plain-text"));
        Assert.Null(ApiKeyProtector.Unprotect("sk-plain-text"));
        Assert.Null(ApiKeyProtector.Unprotect("dpapi:v1:not-base64!!"));
        Assert.Null(ApiKeyProtector.Unprotect("dpapi:v1:" + Convert.ToBase64String(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public void ConfigLoad_ToleratesCommentsAndTrailingCommas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"earcue_cfg_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            {
              // 手动编辑的配置也要能读
              "llm": {
                "baseUrl": "https://api.example.com/v1",
                "model": "my-model",
              }
            }
            """);

            var config = EarcueConfig.LoadFromFile(path);
            Assert.Equal("https://api.example.com/v1", config.Llm.BaseUrl);
            Assert.Equal("my-model", config.Llm.Model);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
