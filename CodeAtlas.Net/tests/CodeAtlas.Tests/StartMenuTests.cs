using System.Diagnostics;
using System.Text;
using CodeAtlas.Cli;
using CodeAtlas.Explanations;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class StartMenuTests : IDisposable
{
    private const string Secret = "sk-synthetic-menu-secret-42";
    private readonly string root = Path.Combine(Path.GetTempPath(), "codeatlas-start-menu-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> environment = new(StringComparer.Ordinal);
    private readonly FakeCredentialStore store = new();
    private readonly FakeMenuWork work = new();
    private string SettingsPath => Path.Combine(root, ".codeatlas", "appsettings.json");
    private string Solution => Path.Combine(root, "App.sln");

    [Fact]
    public async Task ZeroOnTheFirstScreenExitsWithoutWork()
    {
        // Arrange
        var console = new FakeMenuConsole("0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("  1  Map a solution\n  2  Map a solution and add AI descriptions\n  3  Change AI settings\n  0  Exit", console.Text);
        Assert.Empty(work.Events);
    }

    [Fact]
    public async Task InvalidChoicesPromptAgainUntilANumberIsValid()
    {
        // Arrange
        var console = new FakeMenuConsole("9", "x", "-1", "", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(4, Occurrences(console.Text, "Enter a number from 0 to 3."));
    }

    [Fact]
    public async Task MapSolutionBackReturnsToTheFirstScreen()
    {
        // Arrange
        var console = new FakeMenuConsole("1", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(2, Occurrences(console.Text, "  0  Exit"));
        Assert.Empty(work.Events);
    }

    [Fact]
    public async Task MapSolutionRepromptsForMissingPathsAndMapsWithoutAi()
    {
        // Arrange
        var console = new FakeMenuConsole("1", Path.Combine(root, "missing.sln"), Path.Combine(root, "readme.txt"), "\"" + Solution + "\"", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(2, Occurrences(console.Text, "Enter the path of an existing .sln or .slnx file"));
        Assert.Equal(["map " + Solution], work.Events);
        Assert.Contains("This makes no AI calls.", console.Text);
        Assert.Contains("Index: " + FakeMenuWork.IndexPath, console.Text);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task EndOfInputCancelsCleanlyWithoutSavingAnything()
    {
        // Arrange
        var console = new FakeMenuConsole("3", "1", "2", "gpt-test");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(StartMenu.CancelledExitCode, exit);
        Assert.EndsWith("Cancelled.\n", console.Text);
        Assert.Empty(store.Keys);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task CtrlCDuringMappingCancelsCleanly()
    {
        // Arrange
        work.CancelMapping = true;
        var console = new FakeMenuConsole("1", Solution, "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(StartMenu.CancelledExitCode, exit);
        Assert.Contains("Cancelled.", console.Text);
    }

    [Fact]
    public async Task CtrlCAtAPromptCancelsCleanly()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var console = new FakeMenuConsole("1") { AfterRead = cancellation.Cancel };

        // Act
        var exit = await RunAsync(console, cancellation.Token);

        // Assert
        Assert.Equal(StartMenu.CancelledExitCode, exit);
        Assert.Empty(work.Events);
    }

    [Fact]
    public async Task AiSettingsBackWorksAtEveryLevel()
    {
        // Arrange: AI settings -> provider list -> back -> back -> exit.
        var console = new FakeMenuConsole("3", "1", "0", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("Current: AI off (no provider chosen).", console.Text);
        Assert.Contains("  1  Ollama (local, no key)\n  2  OpenAI (cloud)\n  3  Azure OpenAI (cloud)\n  4  Anthropic (cloud)\n  5  Kimi (cloud)\n  6  OpenRouter (cloud)\n  0  Back", console.Text);
        Assert.Equal(2, Occurrences(console.Text, "\nAI settings\n"));
        Assert.Empty(work.Events);
    }

    [Fact]
    public async Task OllamaSetupTestsTheConnectionAndSavesWithoutAKey()
    {
        // Arrange
        var console = new FakeMenuConsole("3", "1", "1", "qwen3:8b", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(["test ollama qwen3:8b"], work.Events);
        Assert.Equal(0, console.SecretReads);
        Assert.Contains("source excerpts stay local", console.Text);
        Assert.Contains("Testing connection... OK", console.Text);
        Assert.Equal("ollama", AiSettingsStore.Load(SettingsPath)!.Provider);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task ModelBackReturnsToTheProviderList()
    {
        // Arrange: OpenAI -> model back -> provider back -> settings back -> exit.
        var console = new FakeMenuConsole("3", "1", "2", "0", "0", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(2, Occurrences(console.Text, "AI provider\n"));
        Assert.Empty(work.Events);
    }

    [Fact]
    public async Task CloudKeyIsTypedHiddenTestedThenSavedOutsideSettings()
    {
        // Arrange
        var console = new FakeMenuConsole("3", "1", "2", "gpt-test", Secret, "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal([Secret], console.SecretInputs);
        Assert.Contains("cloud service: describing sends selected source excerpts", console.Text);
        Assert.Contains("API key (hidden, 0 = back): ", console.Text);
        Assert.Contains("Key saved in: Fake Keychain", console.Text);
        Assert.Equal(Secret, store.Keys["openai"]);
        Assert.Equal(["test openai gpt-test"], work.Events);
        Assert.DoesNotContain(Secret, console.Text);
        Assert.DoesNotContain(Secret, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task KeyPromptBackReturnsToTheModelPrompt()
    {
        // Arrange: OpenAI -> model -> key back -> model back -> provider back -> settings back -> exit.
        var console = new FakeMenuConsole("3", "1", "2", "gpt-test", "0", "0", "0", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(2, Occurrences(console.Text, "Model (0 = back): "));
        Assert.Empty(work.Events);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task FailedConnectionSavesNothingAndAsksForTheKeyAgain()
    {
        // Arrange
        work.FailConnection = true;
        var console = new FakeMenuConsole("3", "1", "2", "gpt-test", Secret, "0", "0", "0", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("Testing connection... FAILED", console.Text);
        Assert.Contains("Nothing was saved.", console.Text);
        Assert.Equal(2, console.SecretReads);
        Assert.Empty(store.Keys);
        Assert.False(File.Exists(SettingsPath));
        Assert.DoesNotContain(Secret, console.Text);
    }

    [Fact]
    public async Task EnvironmentKeyIsUsedWithoutAPromptAndNothingIsSaved()
    {
        // Arrange
        environment["OPENAI_API_KEY"] = Secret;
        var console = new FakeMenuConsole("3", "1", "2", "gpt-test", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(0, console.SecretReads);
        Assert.Equal(0, store.Saves);
        Assert.Contains("Using the key from environment variable OPENAI_API_KEY. Nothing will be saved.", console.Text);
        Assert.Equal(["test openai gpt-test"], work.Events);
    }

    [Fact]
    public async Task SavedKeyCanBeReusedAfterGoingBack()
    {
        // Arrange: key menu back -> model again -> use saved key.
        store.Keys["anthropic"] = Secret;
        var console = new FakeMenuConsole("3", "1", "4", "claude-test", "0", "claude-test", "1", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("  1  Use saved key (Fake Keychain)\n  2  Enter a new key\n  0  Back", console.Text);
        Assert.Equal(["test anthropic claude-test"], work.Events);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, console.SecretReads);
    }

    [Fact]
    public async Task RemovingASavedKeyNamesTheStoreAndLeavesEnvironmentVariablesAlone()
    {
        // Arrange
        store.Keys["openai"] = Secret;
        environment["CODEATLAS_AI_OPENAI_API_KEY"] = "synthetic-legacy";
        var console = new FakeMenuConsole("3", "2", "1", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("  1  OpenAI (in Fake Keychain)", console.Text);
        Assert.Contains("Removed from: Fake Keychain", console.Text);
        Assert.Contains("environment variable CODEATLAS_AI_OPENAI_API_KEY. CodeAtlas does not change environment variables", console.Text);
        Assert.Empty(store.Keys);
        Assert.Equal("synthetic-legacy", environment["CODEATLAS_AI_OPENAI_API_KEY"]);
    }

    [Fact]
    public async Task DescribingRequiresAnExplicitBatchChoice()
    {
        // Arrange: map + AI -> Ollama -> invalid batch answers -> first 10.
        var console = new FakeMenuConsole("2", Solution, "1", "qwen3:8b", "", "7", "1", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("250 classes/types and methods have no description.", console.Text);
        Assert.Contains("  1  First 10 symbols\n  2  First 100 symbols\n  3  All 250 symbols (~250 requests)\n  0  Back", console.Text);
        Assert.Equal(2, Occurrences(console.Text, "Enter a number from 0 to 3."));
        Assert.Equal(["test ollama qwen3:8b", "map " + Solution, "count", "describe 10"], work.Events);
        Assert.Contains("Source excerpts stay local.", console.Text);
    }

    [Fact]
    public async Task DescribingEverySymbolNeedsItsOwnNumber()
    {
        // Arrange
        work.Missing = 16_979;
        var console = new FakeMenuConsole("2", Solution, "1", "qwen3:8b", "3", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("All 16,979 symbols (~16,979 requests)", console.Text);
        Assert.Equal("describe 16979", work.Events[^1]);
    }

    [Fact]
    public async Task DescribeBackKeepsTheMapAndDescribesNothing()
    {
        // Arrange
        var console = new FakeMenuConsole("2", Solution, "1", "qwen3:8b", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("No descriptions requested. The map is saved.", console.Text);
        Assert.DoesNotContain(work.Events, item => item.StartsWith("describe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SmallSolutionsOfferOnlyTheExactCount()
    {
        // Arrange
        work.Missing = 5;
        var console = new FakeMenuConsole("2", Solution, "1", "qwen3:8b", "1", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("Describe\n  5 classes/types and methods have no description. Each needs one request to Ollama.\n  1  All 5 symbols (~5 requests)\n  0  Back", console.Text);
        Assert.Equal("describe 5", work.Events[^1]);
    }

    [Fact]
    public async Task BackFromAiChoiceReturnsToTheSolutionPrompt()
    {
        // Arrange: no AI yet -> provider list back -> solution prompt back -> exit.
        var console = new FakeMenuConsole("2", Solution, "0", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(2, Occurrences(console.Text, "Solution path (0 = back): "));
        Assert.Empty(work.Events);
    }

    [Fact]
    public async Task SavedCloudSettingsAreTestedBeforeMappingAndShowWhatLeavesTheMachine()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        AiSettingsStore.Save(SettingsPath, new AiPreferences("openai", "gpt-test"));
        store.Keys["openai"] = Secret;
        var console = new FakeMenuConsole("2", Solution, "1", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("Current: OpenAI, model gpt-test (key in Fake Keychain).", console.Text);
        Assert.Contains("Selected source excerpts will be sent to api.openai.com.", console.Text);
        Assert.Equal(["test openai gpt-test", "map " + Solution, "count"], work.Events);
        Assert.DoesNotContain(Secret, console.Text);
    }

    [Fact]
    public async Task FailedTestOfSavedSettingsDoesNotMap()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        AiSettingsStore.Save(SettingsPath, new AiPreferences("ollama", "qwen3:8b"));
        work.FailConnection = true;
        var console = new FakeMenuConsole("2", Solution, "1", "0", "0", "0");

        // Act
        var exit = await RunAsync(console);

        // Assert
        Assert.Equal(0, exit);
        Assert.Contains("Testing connection... FAILED", console.Text);
        Assert.DoesNotContain(work.Events, item => item.StartsWith("map", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new string[0], false, false, true)]
    [InlineData(new[] { "--help" }, false, false, false)]
    [InlineData(new[] { "index", "--solution", "App.sln" }, false, false, false)]
    [InlineData(new string[0], true, false, false)]
    [InlineData(new string[0], false, true, false)]
    public void MenuOpensOnlyWithoutArgumentsInAnInteractiveTerminal(string[] args, bool inputRedirected, bool outputRedirected, bool expected)
    {
        // Act
        var opens = StartMenu.ShouldOpen(args, inputRedirected, outputRedirected);

        // Assert
        Assert.Equal(expected, opens);
    }

    [Fact]
    public async Task RedirectedRunWithoutArgumentsNeverPrompts()
    {
        // Arrange
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx"))) repository = repository.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository!.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(cli);

        // Act
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(60_000);
        if (!exited) process.Kill(entireProcessTree: true);

        // Assert
        Assert.True(exited);
        Assert.Equal(2, process.ExitCode);
        Assert.DoesNotContain("Choice:", await output);
        Assert.Contains("\"error\"", await error);
    }

    private Task<int> RunAsync(FakeMenuConsole console, CancellationToken token = default, SqliteStatus? sqlite = null)
    {
        var menu = new StartMenu(console, work, store, name => environment.GetValueOrDefault(name), SettingsPath,
            path => Path.GetFullPath(path) == Solution, "0.00.00-test", sqlite is null ? null : () => sqlite);
        return menu.RunAsync(token);
    }

    [Fact]
    public async Task UnavailableSqliteOffersInstructionsAndBackWithoutMapping()
    {
        // Arrange
        var unavailable = new SqliteStatus(false, null, false, "Native library blocked.")
        {
            Instructions = ["Debian/Ubuntu: sudo apt install libsqlite3-0"]
        };
        var console = new FakeMenuConsole("1", "1", "0", "2", "0", "0");

        // Act
        var exit = await RunAsync(console, sqlite: unavailable);

        // Assert
        Assert.Equal(0, exit);
        Assert.Equal(3, Occurrences(console.Text, "SQLite is unavailable"));
        Assert.Contains("  1  Show install instructions\n  0  Back", console.Text);
        Assert.Contains("Native library blocked.", console.Text);
        Assert.Equal(1, Occurrences(console.Text, "sudo apt install libsqlite3-0"));
        Assert.Contains("does not install anything", console.Text);
        Assert.DoesNotContain("Solution path", console.Text);
        Assert.Empty(work.Events);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

/// <summary>Scripted terminal. Running out of input behaves like Ctrl+C.</summary>
internal sealed class FakeMenuConsole(params string[] inputs) : IMenuConsole
{
    private readonly Queue<string> inputs = new(inputs);
    private readonly StringBuilder output = new();
    public string Text => output.ToString();
    public int SecretReads { get; private set; }
    public List<string> SecretInputs { get; } = [];
    public Action? AfterRead { get; init; }

    public void Write(string text) => output.Append(text);
    public void WriteLine(string text = "") => output.Append(text).Append('\n');

    public Task<string?> ReadLineAsync(CancellationToken token) => Task.FromResult(Next());

    public Task<string?> ReadSecretAsync(CancellationToken token)
    {
        SecretReads++;
        var secret = Next();
        if (secret is not null and not "0") SecretInputs.Add(secret);
        return Task.FromResult(secret);
    }

    private string? Next()
    {
        var line = inputs.TryDequeue(out var value) ? value : null;
        AfterRead?.Invoke();
        return line;
    }
}

internal sealed class FakeMenuWork : IStartMenuWork
{
    internal const string IndexPath = "fake/.codeatlas/solutions/app/current.json";
    public List<string> Events { get; } = [];
    public int Missing { get; set; } = 250;
    public bool FailConnection { get; set; }
    public bool CancelMapping { get; set; }

    public Task<MappedSolution> MapAsync(string solution, CancellationToken token)
    {
        if (CancelMapping) throw new OperationCanceledException();
        Events.Add("map " + solution);
        return Task.FromResult(new MappedSolution(solution, IndexPath, 2, 5, 40, 0, null));
    }

    public Task<int> CountMissingDescriptionsAsync(string indexPath, CancellationToken token)
    {
        Events.Add("count");
        return Task.FromResult(Missing);
    }

    public Task<DescribeOutcome> DescribeAsync(string indexPath, AiConfiguration configuration, int limit, Action<int> progress,
        CancellationToken token)
    {
        Events.Add("describe " + limit);
        progress(1);
        return Task.FromResult(new DescribeOutcome(limit, 0, Missing - limit, null));
    }

    public Task TestConnectionAsync(AiConfiguration configuration, CancellationToken token)
    {
        Events.Add($"test {configuration.Provider} {configuration.Model}");
        return FailConnection ? throw new QueryException("ai_provider_failed", "Connection test failed.") : Task.CompletedTask;
    }
}
