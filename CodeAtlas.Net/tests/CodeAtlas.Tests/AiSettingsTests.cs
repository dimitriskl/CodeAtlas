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
        var testedBeforeSavingOptions = false;
        var store = new FakeCredentialStore { OnSave = () => order.Add("persist") };
        var prompted = "";
        var setup = new AiSetup(_ => null, store,
            name => { order.Add("prompt"); prompted = name; return "synthetic-secret"; },
            (_, _) => { order.Add("test"); testedBeforeSavingOptions = AiSettingsStore.Load(SettingsPath) is null; return Task.CompletedTask; });
        var selected = new AiPreferences("openai", "my-model", "low");

        // Act
        var result = await setup.ConfigureAsync(selected, SettingsPath, default);

        // Assert
        Assert.Equal(["prompt", "test", "persist"], order);
        Assert.True(testedBeforeSavingOptions);
        Assert.Equal("CODEATLAS_AI_OPENAI_KEY", prompted);
        Assert.Equal("synthetic-secret", store.Keys["openai"]);
        Assert.Equal("Fake Keychain", result.KeyLocation);
        var saved = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("synthetic-secret", saved);
        Assert.Contains("logging", saved);
        Assert.Equal("my-model", AiSettingsStore.Load(SettingsPath)!.Model);
        Assert.Equal("low", AiSettingsStore.Load(SettingsPath)!.Effort);
    }

    [Fact]
    public async Task EnvironmentKeyIsUsedWithoutPromptingAndNothingIsSaved()
    {
        // Arrange
        var store = new FakeCredentialStore();
        var setup = new AiSetup(name => name == "OPENAI_API_KEY" ? "synthetic-existing-key" : null,
            store, _ => throw new Exception("Unexpected prompt"), (_, _) => Task.CompletedTask);

        // Act
        var result = await setup.ConfigureAsync(new("openai", "chosen-model"), SettingsPath, default);

        // Assert
        Assert.Equal(0, store.Saves);
        Assert.Equal("environment variable OPENAI_API_KEY", result.KeyLocation);
        Assert.DoesNotContain("synthetic-existing-key", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task StoredKeyIsReusedWithoutPromptingOrSavingAgain()
    {
        // Arrange
        var store = new FakeCredentialStore();
        store.Keys["anthropic"] = "synthetic-stored-key";
        string? used = null;
        var setup = new AiSetup(_ => null, store, _ => throw new Exception("Unexpected prompt"), (configuration, _) =>
        {
            using var message = new HttpRequestMessage();
            configuration.Authorize(message);
            used = message.Headers.GetValues("x-api-key").Single();
            return Task.CompletedTask;
        });

        // Act
        var result = await setup.ConfigureAsync(new("anthropic", "claude-model"), SettingsPath, default);

        // Assert
        Assert.Equal("synthetic-stored-key", used);
        Assert.Equal(0, store.Saves);
        Assert.Equal("Fake Keychain", result.KeyLocation);
    }

    [Fact]
    public async Task FailedConnectionPreservesPreviousSettingsAndDoesNotPersistCredential()
    {
        // Arrange
        Directory.CreateDirectory(root);
        const string original = "{\"ai\":{\"provider\":\"ollama\",\"model\":\"old-model\"}}";
        File.WriteAllText(SettingsPath, original);
        var store = new FakeCredentialStore();
        var setup = new AiSetup(_ => null, store, _ => throw new Exception("Unexpected prompt"),
            (_, _) => throw new QueryException("ai_provider_failed", "Connection failed"));

        // Act
        var error = await Record.ExceptionAsync(() => setup.ConfigureAsync(new("openai", "new-model"), SettingsPath, default, "synthetic-secret"));

        // Assert
        Assert.Equal("ai_provider_failed", Assert.IsType<QueryException>(error).Code);
        Assert.Equal(0, store.Saves);
        Assert.Empty(store.Keys);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task KeySaveFailureAfterPassingTestLeavesSettingsUnchanged()
    {
        // Arrange
        var store = new FakeCredentialStore { FailSave = true };
        var setup = new AiSetup(_ => null, store, _ => "synthetic-secret", (_, _) => Task.CompletedTask);

        // Act
        var error = await Record.ExceptionAsync(() => setup.ConfigureAsync(new("kimi", "kimi-model"), SettingsPath, default));

        // Assert
        Assert.Equal("ai_configuration_invalid", Assert.IsType<QueryException>(error).Code);
        Assert.DoesNotContain("synthetic-secret", error.Message);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task OllamaSetupDoesNotReadPromptOrPersistAnyCredential()
    {
        // Arrange
        var tests = 0;
        var setup = new AiSetup(_ => throw new Exception("Unexpected credential read"), new FakeCredentialStore { Forbidden = true },
            _ => throw new Exception("Unexpected prompt"), (_, _) => { tests++; return Task.CompletedTask; });

        // Act
        await setup.ConfigureAsync(new("ollama", "qwen3:8b", "none"), SettingsPath, default);

        // Assert
        Assert.Equal(1, tests);
        Assert.Equal("ollama", AiSettingsStore.Load(SettingsPath)!.Provider);
    }

    [Fact]
    public void KeyLookupPrefersEnvironmentVariablesThenKeychain()
    {
        // Arrange
        var store = new FakeCredentialStore();
        store.Keys["openai"] = "synthetic-stored";
        var preferences = new AiPreferences("openai", "model");
        var environment = new Dictionary<string, string>
        {
            ["CODEATLAS_AI_OPENAI_KEY"] = "synthetic-new", ["CODEATLAS_AI_OPENAI_API_KEY"] = "synthetic-legacy"
        };

        // Act
        var preferred = preferences.FindKey(environment.GetValueOrDefault, store);
        environment.Remove("CODEATLAS_AI_OPENAI_KEY");
        var legacy = preferences.FindKey(environment.GetValueOrDefault, store);
        environment.Clear();
        var stored = preferences.FindKey(environment.GetValueOrDefault, store);

        // Assert
        Assert.Equal(("synthetic-new", "environment variable CODEATLAS_AI_OPENAI_KEY"), (preferred!.Value, preferred.Location));
        Assert.Equal(("synthetic-legacy", "environment variable CODEATLAS_AI_OPENAI_API_KEY"), (legacy!.Value, legacy.Location));
        Assert.Equal(("synthetic-stored", "Fake Keychain"), (stored!.Value, stored.Location));
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
