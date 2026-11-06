using CodeAtlas.Explanations;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class AiConfigurationTests
{
    [Theory]
    [InlineData("openai", "https://api.openai.com/v1/chat/completions")]
    [InlineData("anthropic", "https://api.anthropic.com/v1/messages")]
    [InlineData("kimi", "https://api.moonshot.ai/v1/chat/completions")]
    [InlineData("ollama", "http://localhost:11434/v1/chat/completions")]
    [InlineData("openrouter", "https://openrouter.ai/api/v1/chat/completions")]
    public void EachProviderWithAPublicServiceHasADefaultEndpoint(string provider, string expected)
    {
        // Arrange
        var name = AiConfiguration.NormalizeProvider(provider);

        // Act
        var endpoint = AiConfiguration.DefaultEndpoint(name);

        // Assert
        Assert.Equal(expected, endpoint.AbsoluteUri);
    }

    [Fact]
    public void AzureHasNoDefaultEndpoint()
    {
        // Arrange
        var name = AiConfiguration.NormalizeProvider("azure");

        // Act
        var error = Record.Exception(() => AiConfiguration.DefaultEndpoint(name));

        // Assert
        Assert.Equal("ai_configuration_invalid", Assert.IsType<QueryException>(error).Code);
        Assert.Contains("--endpoint", error.Message);
    }

    [Fact]
    public void EnvironmentEndpointReplacesTheDefault()
    {
        // Arrange
        var values = new Dictionary<string, string>
        {
            ["CODEATLAS_AI_API_KEY"] = "synthetic-test-key",
            ["CODEATLAS_AI_MODEL"] = "configured-model",
            ["CODEATLAS_AI_ENDPOINT"] = "https://provider.example/v1/chat/completions"
        };

        // Act
        var configuration = AiConfiguration.FromEnvironment(name => values.GetValueOrDefault(name));

        // Assert
        Assert.NotNull(configuration);
        Assert.Equal("https://provider.example/v1/chat/completions", configuration.Endpoint.AbsoluteUri);
        Assert.Equal("configured-model", configuration.Model);
    }

    [Fact]
    public void EnvironmentEndpointThatIsNotAnAbsoluteAddressIsRejected()
    {
        // Arrange
        var values = new Dictionary<string, string>
        {
            ["CODEATLAS_AI_API_KEY"] = "synthetic-test-key",
            ["CODEATLAS_AI_MODEL"] = "configured-model",
            ["CODEATLAS_AI_ENDPOINT"] = "provider.example/chat"
        };

        // Act
        var error = Record.Exception(() => AiConfiguration.FromEnvironment(name => values.GetValueOrDefault(name)));

        // Assert
        Assert.Equal("ai_configuration_invalid", Assert.IsType<QueryException>(error).Code);
    }
}
