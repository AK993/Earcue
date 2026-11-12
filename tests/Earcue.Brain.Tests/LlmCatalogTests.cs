using System.Net;
using System.Text;
using Earcue.Brain.Llm;
using Xunit;

namespace Earcue.Brain.Tests;

public class LlmCatalogTests
{
    [Theory]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1")]
    [InlineData("api.deepseek.com/v1", "https://api.deepseek.com/v1")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1")]
    [InlineData("https://api.example.com/v1/models", "https://api.example.com/v1")]
    [InlineData("https://api.example.com/", "https://api.example.com")]
    public void TryNormalizeBaseUrl_NormalizesCommonInputs(string input, string expected)
    {
        Assert.True(LlmEndpoints.TryNormalizeBaseUrl(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeBaseUrl_RejectsUnsupportedOrEmpty()
    {
        Assert.False(LlmEndpoints.TryNormalizeBaseUrl(null, out _, out var emptyError));
        Assert.Contains("Base URL", emptyError);

        Assert.False(LlmEndpoints.TryNormalizeBaseUrl("ftp://host/v1", out _, out var schemeError));
        Assert.Contains("http", schemeError);

        Assert.False(LlmEndpoints.TryNormalizeBaseUrl("not a url", out _, out var formatError));
        Assert.Contains("Base URL", formatError);
    }

    [Fact]
    public void ModelAndChatCandidates_HandleBareHostAndVersionedPaths()
    {
        Assert.Equal(
            new[] { "https://api.example.com/v1/models", "https://api.example.com/models" },
            LlmEndpoints.ModelsUrlCandidates("https://api.example.com"));

        Assert.Equal(
            new[] { "https://api.example.com/v1/models" },
            LlmEndpoints.ModelsUrlCandidates("https://api.example.com/v1"));

        Assert.Equal(
            new[] { "https://api.example.com/v1/chat/completions", "https://api.example.com/chat/completions" },
            LlmEndpoints.ChatCompletionsCandidates("https://api.example.com"));

        Assert.Equal(
            new[] { "http://localhost:11434/v1/chat/completions" },
            LlmEndpoints.ChatCompletionsCandidates("http://localhost:11434/v1"));
    }

    [Fact]
    public void IsLoopback_DetectsLocalProvidersOnly()
    {
        Assert.True(LlmEndpoints.IsLoopback("http://localhost:11434/v1"));
        Assert.True(LlmEndpoints.IsLoopback("http://127.0.0.1:1234/v1"));
        Assert.False(LlmEndpoints.IsLoopback("https://api.deepseek.com/v1"));
        Assert.False(LlmEndpoints.IsLoopback("not a url"));
    }

    [Fact]
    public async Task ListModels_SendsBearerTokenAndParsesModels()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {
              "object": "list",
              "data": [
                { "id": "z-model", "owned_by": "vendor", "created": 1700000000 },
                { "id": "a-model", "owned_by": "vendor" },
                { "id": "a-model" },
                { "object": "model" }
              ]
            }
            """));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var result = await catalog.ListModelsAsync("https://api.example.com/v1/", "sk-test-key");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("a-model", result.Models[0].Id);
        Assert.Equal("z-model", result.Models[1].Id);
        Assert.Equal("https://api.example.com/v1/models", handler.RequestUrls[0]);
        Assert.Equal("sk-test-key", handler.BearerTokens[0]);
        Assert.Contains("2", result.Message);
        Assert.Equal("https://api.example.com/v1/models", result.Endpoint);
    }

    [Fact]
    public async Task ListModels_FallsBackToNonVersionedPathWithoutSendingAuth()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/v1/models"
                ? Json(HttpStatusCode.NotFound, """{ "error": { "message": "not found" } }""")
                : Json(HttpStatusCode.OK, """{ "data": [ { "id": "m1" } ] }"""));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var result = await catalog.ListModelsAsync("https://api.example.com", null);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Single(result.Models);
        Assert.Equal(2, handler.RequestUrls.Count);
        Assert.Equal("https://api.example.com/v1/models", handler.RequestUrls[0]);
        Assert.Equal("https://api.example.com/models", handler.RequestUrls[1]);
        Assert.Equal(string.Empty, handler.BearerTokens[1]);
    }

    [Fact]
    public async Task ListModels_ReturnsReadableMessageOnUnauthorized()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.Unauthorized,
            """{ "error": { "message": "Invalid API key" } }"""));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var result = await catalog.ListModelsAsync("https://api.example.com/v1", "bad-key");

        Assert.False(result.IsSuccess);
        Assert.Contains("401", result.Message);
        Assert.Contains("API Key", result.Message);
        Assert.Contains("Invalid API key", result.Message);
    }

    [Fact]
    public async Task ListModels_AcceptsRootArrayAndStringItems()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """["m2", "m1"]"""));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var result = await catalog.ListModelsAsync("https://api.example.com/v1", "k");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new[] { "m1", "m2" }, result.Models.Select(m => m.Id));
    }

    [Fact]
    public async Task ListModels_ReportsInvalidJsonAndInvalidUrl()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "<html>gateway</html>"));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var badJson = await catalog.ListModelsAsync("https://api.example.com/v1", "k");
        Assert.False(badJson.IsSuccess);
        Assert.Contains("JSON", badJson.Message);

        var badUrl = await catalog.ListModelsAsync("not a url", "k");
        Assert.False(badUrl.IsSuccess);
        Assert.Contains("Base URL", badUrl.Message);
    }

    [Fact]
    public async Task ListModels_ReportsZeroModelsAsSuccess()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "data": [] }"""));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var result = await catalog.ListModelsAsync("https://api.example.com/v1", "k");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Models);
        Assert.Contains("0", result.Message);
    }

    [Fact]
    public async Task TestChat_SendsNonStreamingRequestAndExtractsReply()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "choices": [ { "message": { "role": "assistant", "content": "连接正常" } } ] }
            """));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var result = await catalog.TestChatAsync("https://api.example.com/v1", "sk-key", "deepseek-chat");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("连接正常", result.Reply);
        Assert.Equal("https://api.example.com/v1/chat/completions", handler.RequestUrls[0]);
        Assert.Contains("\"model\":\"deepseek-chat\"", handler.RequestBodies[0]);
        Assert.Contains("\"stream\":false", handler.RequestBodies[0]);
        Assert.Equal("sk-key", handler.BearerTokens[0]);
    }

    [Fact]
    public async Task TestChat_ReportsServerErrorBody()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.BadRequest,
            """{ "error": { "message": "model does not exist" } }"""));

        using var client = new HttpClient(handler);
        using var catalog = new OpenAiCompatibleModelCatalog(client);

        var result = await catalog.TestChatAsync("https://api.example.com/v1", "k", "missing-model");

        Assert.False(result.IsSuccess);
        Assert.Contains("400", result.Message);
        Assert.Contains("model does not exist", result.Message);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<string> RequestUrls { get; } = new();

        public List<string> BearerTokens { get; } = new();

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            BearerTokens.Add(request.Headers.Authorization?.Parameter ?? string.Empty);

            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return _responder(request);
        }
    }
}
