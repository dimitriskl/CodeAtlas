using System.Net;
using System.Text;
using System.Text.Json;
using CodeAtlas.Explanations;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class ExplanationGeneratorTests
{
    [Fact]
    public void MissingKeyDisablesAiBeforeReadingOtherConfiguration()
    {
        // Arrange
        var lookups = new List<string>();

        // Act
        var configuration = AiConfiguration.FromEnvironment(name => { lookups.Add(name); return null; });

        // Assert
        Assert.Null(configuration);
        Assert.Equal(["CODEATLAS_AI_API_KEY"], lookups);
    }

    [Fact]
    public void CredentialsRequireAModelAndNeverAppearInConfigurationOutput()
    {
        // Arrange
        const string secret = "synthetic-test-key";
        var configuration = new AiConfiguration(secret, "configured-model");

        // Act
        var error = Record.Exception(() => AiConfiguration.FromEnvironment(name =>
            name == "CODEATLAS_AI_API_KEY" ? secret : null));
        var output = JsonSerializer.Serialize(configuration) + configuration;

        // Assert
        Assert.Equal("ai_configuration_invalid", Assert.IsType<QueryException>(error).Code);
        Assert.DoesNotContain(secret, output);
        Assert.DoesNotContain(secret, error.ToString());
        Assert.Equal("https://api.openai.com/v1/chat/completions", configuration.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://provider.example/chat")]
    [InlineData("https://user:password@provider.example/chat")]
    [InlineData("https://provider.example/chat?key=secret")]
    [InlineData("/relative")]
    public void EndpointRejectsInsecureOrCredentialBearingAddresses(string endpoint)
    {
        // Arrange
        var uri = new Uri(endpoint, UriKind.RelativeOrAbsolute);

        // Act
        var error = Record.Exception(() => new AiConfiguration("synthetic-key", "test-model", uri));

        // Assert
        Assert.Equal("ai_configuration_invalid", Assert.IsType<QueryException>(error).Code);
        Assert.DoesNotContain(endpoint, error.Message);
    }

    [Fact]
    public async Task SendsOnlySelectedEvidenceAndConstructsValidatedIdentityAndUtf16CitationsLocally()
    {
        // Arrange
        var request = Request();
        string? sent = null;
        string? authorization = null;
        Uri? endpoint = null;
        using var client = Client(async (message, token) =>
        {
            sent = await message.Content!.ReadAsStringAsync(token);
            authorization = message.Headers.Authorization?.ToString();
            endpoint = message.RequestUri;
            return Success();
        });
        var generator = new ExplanationGenerator(client,
            new("synthetic-key", "configured-model", new Uri("http://127.0.0.1:1234/chat")));

        // Act
        var response = await generator.GenerateAsync(request);

        // Assert
        using var wire = JsonDocument.Parse(sent!);
        Assert.Equal("Bearer synthetic-key", authorization);
        Assert.Equal("http://127.0.0.1:1234/chat", endpoint!.AbsoluteUri);
        Assert.Equal("configured-model", wire.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", wire.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(2048, wire.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(wire.RootElement.GetProperty("store").GetBoolean());
        var messages = wire.RootElement.GetProperty("messages");
        Assert.Contains("untrusted data", messages[0].GetProperty("content").GetString());
        Assert.Contains(request.Evidence[0].Text,
            JsonDocument.Parse(messages[1].GetProperty("content").GetString()!).RootElement
                .GetProperty("evidence")[0].GetProperty("text").GetString());
        Assert.DoesNotContain(request.RequestId, sent);
        Assert.DoesNotContain(request.Inputs[0].Sha256, sent);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(request.ContextFingerprint, response.ContextFingerprint);
        Assert.Equal(request.IndexId, response.IndexId);
        Assert.Equal(request.Selection.SymbolId, response.SymbolId);
        Assert.Equal("actual-provider-model", response.Provenance.Model);
        var claim = Assert.Single(response.Claims);
        var citation = Assert.Single(claim.Evidence);
        Assert.Equal("interpretation", claim.Category);
        Assert.Equal(3, citation.Offset); // emoji occupies two UTF-16 code units, followed by a space.
        Assert.Equal(4, citation.Length);
        ExplanationService.ValidateResponse(request, response);
    }

    [Fact]
    public async Task ProviderErrorsAndNetworkExceptionsAreRedacted()
    {
        // Arrange
        const string secretAndSource = "synthetic-key private source text";
        using var denied = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent(secretAndSource) }));
        using var disconnected = Client((_, _) => throw new HttpRequestException(secretAndSource));
        var config = new AiConfiguration("synthetic-key", "test-model");

        // Act
        var deniedError = await Record.ExceptionAsync(() => new ExplanationGenerator(denied, config).GenerateAsync(Request()));
        var networkError = await Record.ExceptionAsync(() => new ExplanationGenerator(disconnected, config).GenerateAsync(Request()));

        // Assert
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(deniedError).Code);
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(networkError).Code);
        Assert.DoesNotContain(secretAndSource, deniedError.ToString() + networkError);
        Assert.Null(deniedError.InnerException);
        Assert.Null(networkError.InnerException);
    }

    [Theory]
    [InlineData("{", "stop", null)]
    [InlineData("{}", "stop", null)]
    [InlineData("null", "stop", null)]
    [InlineData("{\"claims\":[],\"limitations\":[]}", "stop", null)]
    [InlineData("{}", "length", null)]
    [InlineData("{}", "stop", "refused")]
    public async Task IncompleteRefusedOrMalformedResponsesAreExplicitFailures(string content, string finish, string? refusal)
    {
        // Arrange
        using var client = Client((_, _) => Task.FromResult(Envelope(content, finish, refusal)));
        var generator = new ExplanationGenerator(client, new("synthetic-key", "test-model"));

        // Act
        var error = await Record.ExceptionAsync(() => generator.GenerateAsync(Request()));

        // Assert
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(error).Code);
    }

    [Fact]
    public async Task UnmatchedQuoteIsRejectedInsteadOfInventingEvidence()
    {
        // Arrange
        using var client = Client((_, _) => Task.FromResult(Success("missing source quote")));
        var generator = new ExplanationGenerator(client, new("synthetic-key", "test-model"));

        // Act
        var error = await Record.ExceptionAsync(() => generator.GenerateAsync(Request()));

        // Assert
        Assert.Equal("invalid_explanation", Assert.IsType<QueryException>(error).Code);
        Assert.DoesNotContain("missing source quote", error.Message);
    }

    [Fact]
    public async Task UnknownLengthBodyCannotReadBeyondTheProviderBound()
    {
        // Arrange
        using var source = new CountingStream(new byte[300_000]);
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(source) }));
        var generator = new ExplanationGenerator(client, new("synthetic-key", "test-model"));

        // Act
        var error = await Record.ExceptionAsync(() => generator.GenerateAsync(Request()));

        // Assert
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(error).Code);
        Assert.Equal(262_144, source.BytesRead);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWhileProviderTimeoutIsRedacted()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var cancelled = Client((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
        using var timedOut = Client((_, _) => throw new TaskCanceledException("synthetic provider detail"));
        var config = new AiConfiguration("synthetic-key", "test-model");

        // Act
        var cancellationError = await Record.ExceptionAsync(() =>
            new ExplanationGenerator(cancelled, config).GenerateAsync(Request(), cancellation.Token));
        var timeoutError = await Record.ExceptionAsync(() => new ExplanationGenerator(timedOut, config).GenerateAsync(Request()));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(cancellationError);
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(timeoutError).Code);
        Assert.DoesNotContain("synthetic provider detail", timeoutError.ToString());
    }

    [Fact]
    public async Task ContextBudgetFailsBeforeAnyNetworkRequest()
    {
        // Arrange
        var request = Request() with { Limits = new(MaxContextBytes: 1) };
        request = request with { ContextFingerprint = ExplanationContract.ContextHash(request) };
        var calls = 0;
        using var client = Client((_, _) => { calls++; return Task.FromResult(Success()); });
        var generator = new ExplanationGenerator(client, new("synthetic-key", "test-model"));

        // Act
        var error = await Record.ExceptionAsync(() => generator.GenerateAsync(request));

        // Assert
        Assert.Equal("explanation_budget_exceeded", Assert.IsType<QueryException>(error).Code);
        Assert.Equal(0, calls);
    }

    private static ExplanationRequest Request()
    {
        var request = new ExplanationRequest(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "",
            "dependency", ExplanationContract.GenerationVersion, DateTimeOffset.UtcNow,
            new("Fixture::M:Double", "Double", "method", "int Double(int value)", "Fixture.csproj", null),
            DeveloperDocumentation.Missing, [], [new("e1", "Fixture.cs", 0, 1, 1, "😀 int Double(int value) => value * 2;", "declaration")],
            [new("Fixture.cs", new string('A', 64), "declaration", true)], [], new(), ExplanationContract.Instructions);
        return request with { ContextFingerprint = ExplanationContract.ContextHash(request) };
    }

    private static HttpResponseMessage Success(string quote = "int ") => Envelope(JsonSerializer.Serialize(new
    {
        claims = new[] { new { text = "The selected method doubles its argument.", evidence = new[] { new { evidenceId = "e1", quote } } } },
        limitations = Array.Empty<string>()
    }), "stop", null);

    private static HttpResponseMessage Envelope(string content, string finish, string? refusal) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "actual-provider-model",
            choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content, refusal } } }
        }), Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
