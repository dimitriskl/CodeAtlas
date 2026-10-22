using CodeAtlas.Cli;
using CodeAtlas.Explanations;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class AiSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "codeatlas-ai-settings-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(root, "appsettings.json");

    [Fact]
    public async Task SetupPromptsOnlyForMissingKeyTestsBeforePersistingAndSavesNoSecret()
    {
        // Arrange
        Directory.CreateDirectory(root);
        File.WriteAllText(SettingsPath, "{\"logging\":{\"level\":\"warning\"}}");
        var order = new List<string>();
        var savedKeys = new Dictionary<string, string>();
        var testedBeforePersisting = false;
        var testedBeforeSavingOptions = false;
        var setup = new AiSetup(_ => null, (name, key) => { order.Add("persist"); savedKeys[name] = key; },
            name => { order.Add("prompt"); return "synthetic-secret"; },
            (_, _) => { order.Add("test"); testedBeforePersisting = savedKeys.Count == 0; testedBeforeSavingOptions = AiSettingsStore.Load(SettingsPath) is null; return Task.CompletedTask; });
        var selected = new AiPreferences("openai", "my-model", "low");

        // Act
        await setup.ConfigureAsync(selected, SettingsPath, default);

        // Assert
        Assert.Equal(["prompt", "test", "persist"], order);
        Assert.True(testedBeforePersisting);
        Assert.True(testedBeforeSavingOptions);
        Assert.Equal("synthetic-secret", savedKeys["CODEATLAS_AI_OPENAI_API_KEY"]);
        var saved = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("synthetic-secret", saved);
        Assert.Contains("logging", saved);
        Assert.Equal("my-model", AiSettingsStore.Load(SettingsPath)!.Model);
        Assert.Equal("low", AiSettingsStore.Load(SettingsPath)!.Effort);
    }

    [Fact]
    public async Task ExistingProviderKeyIsReusedWithoutPromptingOrChangingItsOriginalVariable()
    {
        // Arrange
        var persisted = new Dictionary<string, string>();
        var setup = new AiSetup(name => name == "OPENAI_API_KEY" ? "synthetic-existing-key" : null,
            (name, key) => persisted[name] = key, _ => throw new Exception("Unexpected prompt"), (_, _) => Task.CompletedTask);

        // Act
        await setup.ConfigureAsync(new("openai", "chosen-model"), SettingsPath, default);

        // Assert
        var saved = Assert.Single(persisted);
        Assert.Equal("CODEATLAS_AI_OPENAI_API_KEY", saved.Key);
        Assert.Equal("synthetic-existing-key", saved.Value);
        Assert.DoesNotContain("synthetic-existing-key", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task FailedConnectionPreservesPreviousSettingsAndDoesNotPersistCredential()
    {
        // Arrange
        Directory.CreateDirectory(root);
        const string original = "{\"ai\":{\"provider\":\"ollama\",\"model\":\"old-model\"}}";
        File.WriteAllText(SettingsPath, original);
        var saves = 0;
        var setup = new AiSetup(_ => "synthetic-secret", (_, _) => saves++, _ => throw new Exception("Unexpected prompt"),
            (_, _) => throw new QueryException("ai_provider_failed", "Connection failed"));

        // Act
        var error = await Record.ExceptionAsync(() => setup.ConfigureAsync(new("openai", "new-model"), SettingsPath, default));

        // Assert
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(error).Code);
        Assert.Equal(0, saves);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task OllamaSetupDoesNotReadPromptOrPersistAnyCredential()
    {
        // Arrange
        var tests = 0;
        var setup = new AiSetup(_ => throw new Exception("Unexpected credential read"), (_, _) => throw new Exception("Unexpected credential write"),
            _ => throw new Exception("Unexpected prompt"), (_, _) => { tests++; return Task.CompletedTask; });

        // Act
        await setup.ConfigureAsync(new("ollama", "qwen3:8b", "none"), SettingsPath, default);

        // Assert
        Assert.Equal(1, tests);
        Assert.Equal("ollama", AiSettingsStore.Load(SettingsPath)!.Provider);
    }

    [Fact]
    public void ProviderChangesDiscardOldEndpointEffortAndDoNotReuseAnotherProvidersKey()
    {
        // Arrange
        var options = new Dictionary<string, string> { ["--provider"] = "ollama", ["--model"] = "local-model" };
        var saved = new AiPreferences("openai", "cloud-model", "max", "https://old.example/chat");

        // Act
        var selected = AiPreferences.Select(options, saved, name => name is "CODEATLAS_AI_API_KEY" or "OPENAI_API_KEY" ? "synthetic-openai-key" : null)!;

        // Assert
        Assert.Null(selected.Effort);
        Assert.Equal("http://localhost:11434/v1/chat/completions", selected.Endpoint);
        Assert.Null(selected.ReadKey(_ => "synthetic-openai-key"));
        Assert.NotNull(selected.Configuration(_ => null));
    }

    [Fact]
    public void CliOverridesEnvironmentWhichOverridesSavedOptionsAndDefaultClearsEffort()
    {
        // Arrange
        var saved = new AiPreferences("openai", "saved-model", "high");
        var options = new Dictionary<string, string> { ["--model"] = "cli-model", ["--effort"] = "default" };

        // Act
        var selected = AiPreferences.Select(options, saved, name => name == "CODEATLAS_AI_MODEL" ? "environment-model" : null)!;

        // Assert
        Assert.Equal("cli-model", selected.Model);
        Assert.Null(selected.Effort);
    }

    [Fact]
    public void AnUnrelatedOpenAiKeyDoesNotEnableUnconfiguredDescriptions()
    {
        // Arrange
        var options = new Dictionary<string, string>();

        // Act
        var selected = AiPreferences.Select(options, null, name => name == "OPENAI_API_KEY" ? "synthetic-secret" : null);

        // Assert
        Assert.Null(selected);
    }

    [Theory]
    [InlineData("azure", "AZURE_OPENAI_API_KEY")]
    [InlineData("azure", "AZUREAI_API_KEY")]
    [InlineData("anthropic", "ANTHROPIC_API_KEY")]
    [InlineData("kimi", "MOONSHOT_API_KEY")]
    [InlineData("openrouter", "OPENROUTER_API_KEY")]
    public void ProviderKeysAreResolvedIndependently(string provider, string variable)
    {
        // Arrange
        var preferences = new AiPreferences(provider, "selected-model");

        // Act
        var key = preferences.ReadKey(name => name == variable ? "synthetic-secret" : name == "CODEATLAS_AI_API_KEY" ? "wrong-provider" : null);

        // Assert
        Assert.Equal("synthetic-secret", key);
    }

    [Fact]
    public void CorruptSettingsFailWithoutPrintingOrReplacingContents()
    {
        // Arrange
        Directory.CreateDirectory(root);
        const string original = "{secret-canary";
        File.WriteAllText(SettingsPath, original);

        // Act
        var error = Record.Exception(() => AiSettingsStore.Load(SettingsPath));

        // Assert
        Assert.Equal("ai_configuration_invalid", Assert.IsType<QueryException>(error).Code);
        Assert.DoesNotContain("secret-canary", error.Message);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
