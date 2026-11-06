using CodeAtlas.Explanations;
using System.Net;
using System.Text.Json;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class AiProviderSelectionTests
{
    [Fact]
    public void OllamaSelectionNeedsNoCredential()
    {
        // Arrange
        const string model = "qwen3:8b";

        // Act
        var configuration = new AiConfiguration(null, model, provider: "ollama", effort: "none");

        // Assert
        Assert.Equal(model, configuration.Model);
        Assert.Equal("ollama", configuration.Provider);
        Assert.Equal("none", configuration.Effort);
        Assert.Equal("http://localhost:11434/v1/chat/completions", configuration.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("openai", "max_completion_tokens", "low")]
    [InlineData("azure", "max_completion_tokens", "low")]
    [InlineData("kimi", "max_tokens", "low")]
    [InlineData("openrouter", "max_tokens", "low")]
    [InlineData("ollama", "max_tokens", "none")]
    public async Task ConnectionTestUsesSelectedModelEffortAndProviderProtocol(string provider, string budgetField, string effort)
    {
        // Arrange
        string? payload = null;
        string? authorization = null;
        using var client = new HttpClient(new Handler(async message =>
        {
            payload = await message.Content!.ReadAsStringAsync();
            authorization = message.Headers.Authorization?.ToString();
            return Reply(new { model = "selected-model", choices = new[] { new { finish_reason = "stop", message = new { content = "{\"ok\":true}" } } } });
        }));
        var configuration = new AiConfiguration(provider == "ollama" ? null : "synthetic-secret", "selected-model",
            new Uri("http://127.0.0.1:1234/chat"), provider, effort);

        // Act
        await new AiProviderClient(client, configuration).TestConnectionAsync();

        // Assert
        using var json = JsonDocument.Parse(payload!);
        Assert.Equal("selected-model", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(effort, json.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(2048, json.RootElement.GetProperty(budgetField).GetInt32());
        Assert.Equal(provider == "ollama" ? null : "Bearer synthetic-secret", authorization);
        Assert.DoesNotContain("synthetic-secret", payload);
        Assert.Contains("Connection test", payload);
        Assert.DoesNotContain("selection", payload);
    }

    [Fact]
    public async Task AnthropicUsesNativeHeadersAndMessagesWithEffort()
    {
        // Arrange
        string? payload = null;
        string? key = null;
        string? version = null;
        string? authorization = null;
        using var client = new HttpClient(new Handler(async message =>
        {
            payload = await message.Content!.ReadAsStringAsync();
            key = message.Headers.GetValues("x-api-key").Single();
            version = message.Headers.GetValues("anthropic-version").Single();
            authorization = message.Headers.Authorization?.ToString();
            return Reply(new { model = "test-claude", stop_reason = "end_turn", content = new[] { new { type = "text", text = "{\"ok\":true}" } } });
        }));
        var configuration = new AiConfiguration("synthetic-secret", "test-claude", provider: "anthropic", effort: "high");

        // Act
        await new AiProviderClient(client, configuration).TestConnectionAsync();

        // Assert
        using var json = JsonDocument.Parse(payload!);
        Assert.Equal("synthetic-secret", key);
        Assert.Equal("2023-06-01", version);
        Assert.Null(authorization);
        Assert.Equal("high", json.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.True(json.RootElement.TryGetProperty("system", out _));
        Assert.False(json.RootElement.TryGetProperty("response_format", out _));
        Assert.DoesNotContain("synthetic-secret", payload);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("tool_calls")]
    public async Task ConnectionTestRejectsIncompleteResponses(string reason)
    {
        // Arrange
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Reply(new
        { choices = new[] { new { finish_reason = reason, message = new { content = "{\"ok\":true}" } } } }))));

        // Act
        var error = await Record.ExceptionAsync(() => new AiProviderClient(client, new("synthetic", "model")).TestConnectionAsync());

        // Assert
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(error).Code);
    }

    [Theory]
    [InlineData("anthropic", "none")]
    [InlineData("ollama", "max")]
    [InlineData("kimi", "medium")]
    [InlineData("openai", "invented")]
    public void UnsupportedEffortFailsBeforeNetwork(string provider, string effort)
    {
        // Arrange
        const string secret = "synthetic-secret";

        // Act
        var error = Record.Exception(() => new AiConfiguration(secret, "model", provider: provider, effort: effort));

        // Assert
        Assert.Equal("ai_configuration_invalid", Assert.IsType<QueryException>(error).Code);
        Assert.DoesNotContain(secret, error.ToString());
    }

    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value)) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message);
    }
}
