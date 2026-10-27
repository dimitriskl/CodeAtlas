using System.Globalization;
using System.Text;
using CodeAtlas.Explanations;

namespace CodeAtlas.Cli;

/// <summary>Terminal input and output for the start menu. A null read means the input ended or the user cancelled.</summary>
internal interface IMenuConsole
{
    void Write(string text);
    void WriteLine(string text = "");
    Task<string?> ReadLineAsync(CancellationToken token);
    Task<string?> ReadSecretAsync(CancellationToken token);
}

internal sealed record MappedSolution(string Solution, string IndexPath, int Projects, int Types, int Methods,
    int WarningCount, string? WarningsFile);

internal sealed record DescribeOutcome(int Generated, int Skipped, int Remaining, string? WarningsFile);

/// <summary>The work behind the menu: mapping, counting and describing (Core), and the provider connection test.</summary>
internal interface IStartMenuWork
{
    Task<MappedSolution> MapAsync(string solution, CancellationToken token);
    Task<int> CountMissingDescriptionsAsync(string indexPath, CancellationToken token);
    Task<DescribeOutcome> DescribeAsync(string indexPath, AiConfiguration configuration, int limit, Action<int> progress,
        CancellationToken token);
    Task TestConnectionAsync(AiConfiguration configuration, CancellationToken token);
}

/// <summary>
/// Numbered start menu shown when <c>codeatlas</c> runs without arguments in an interactive terminal.
/// 0 means back (exit on the first screen). AI stays off unless the user chooses it.
/// </summary>
internal sealed class StartMenu(IMenuConsole console, IStartMenuWork work, ICredentialStore credentials,
    Func<string, string?> readEnvironment, string settingsPath, Func<string, bool> fileExists, string version,
    Func<SqliteStatus>? sqliteStatus = null)
{
    internal const int CancelledExitCode = 130;

    private static readonly (string Id, string Name, string Label)[] Providers =
    [
        ("ollama", "Ollama", "Ollama (local, no key)"),
        ("openai", "OpenAI", "OpenAI (cloud)"),
        ("azure", "Azure OpenAI", "Azure OpenAI (cloud)"),
        ("anthropic", "Anthropic", "Anthropic (cloud)"),
        ("kimi", "Kimi", "Kimi (cloud)"),
        ("openrouter", "OpenRouter", "OpenRouter (cloud)")
    ];

    /// <summary>Agents, scripts and MCP pass arguments or redirect input/output; they never see a prompt.</summary>
    internal static bool ShouldOpen(string[] args, bool inputRedirected, bool outputRedirected) =>
        args.Length == 0 && !inputRedirected && !outputRedirected;

    internal static async Task<int> RunInConsoleAsync()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var menu = new StartMenu(new SystemMenuConsole(), new StartMenuWork(), CredentialStore.ForCurrentUser(),
            AiSetup.ReadEnvironment, Path.GetFullPath(AiSettingsStore.DefaultPath), File.Exists, CodeAtlasVersion.Current);
        try { return await menu.RunAsync(cancellation.Token); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine("CodeAtlas stopped: " + e.Message);
            return 1;
        }
    }

    internal async Task<int> RunAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                var choice = await ChooseAsync("CodeAtlas " + version,
                    ["Map a solution", "Map a solution and add AI descriptions", "Change AI settings"], "Exit", token);
                if (choice == 0) return 0;
                if (choice == 1) await MapOnlyAsync(token);
                else if (choice == 2) await MapAndDescribeAsync(token);
                else await AiSettingsAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            console.WriteLine();
            console.WriteLine("Cancelled.");
            return CancelledExitCode;
        }
    }

    private async Task MapOnlyAsync(CancellationToken token)
    {
        if (!await SqliteReadyAsync(token)) return;
        var solution = await AskSolutionAsync(token);
        if (solution is not null) await MapAsync(solution, token);
    }

    private async Task MapAndDescribeAsync(CancellationToken token)
    {
        if (!await SqliteReadyAsync(token)) return;
        while (true)
        {
            var solution = await AskSolutionAsync(token);
            if (solution is null) return;
            var ai = await ChooseAiForRunAsync(token);
            if (ai is null) continue;
            console.WriteLine(DataNotice(ai));
            var mapped = await MapAsync(solution, token);
            if (mapped is not null) await DescribeAsync(mapped, ai, token);
            return;
        }
    }

    /// <summary>Mapping needs the SQLite store. When the library is unusable, explain it and offer the
    /// platform instructions; nothing is installed, and 0 returns to the start menu.</summary>
    private async Task<bool> SqliteReadyAsync(CancellationToken token)
    {
        var status = (sqliteStatus ?? SqliteAvailability.Check)();
        if (status.Available) return true;
        while (true)
        {
            var choice = await ChooseAsync("SQLite is unavailable", ["Show install instructions"], "Back", token,
                "CodeAtlas stores each map in SQLite and cannot map a solution without it. " + status.Failure);
            if (choice == 0) return false;
            console.WriteLine();
            foreach (var line in status.Instructions) console.WriteLine("  " + line);
            console.WriteLine("  CodeAtlas does not install anything itself. Run the step for your platform, then start CodeAtlas again.");
        }
    }

    private async Task<string?> AskSolutionAsync(CancellationToken token)
    {
        console.WriteLine();
        while (true)
        {
            var answer = (await ReadAsync("Solution path (0 = back): ", token)).Trim('"', '\'').Trim();
            if (answer == "0") return null;
            if (Path.GetExtension(answer).ToLowerInvariant() is ".sln" or ".slnx" && fileExists(answer))
                return Path.GetFullPath(answer);
            console.WriteLine("Enter the path of an existing .sln or .slnx file, or 0 to go back.");
        }
    }

    private async Task<MappedSolution?> MapAsync(string solution, CancellationToken token)
    {
        console.WriteLine($"Mapping {solution}. This makes no AI calls.");
        try
        {
            var mapped = await work.MapAsync(solution, token);
            console.WriteLine($"Mapped {N(mapped.Projects)} projects, {N(mapped.Types)} types and {N(mapped.Methods)} methods.");
            console.WriteLine("Index: " + mapped.IndexPath);
            if (mapped.WarningCount > 0) console.WriteLine($"{N(mapped.WarningCount)} warnings recorded in {mapped.WarningsFile}");
            return mapped;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            console.WriteLine("Mapping failed: " + e.Message);
            return null;
        }
    }

    private async Task<AiConfiguration?> ChooseAiForRunAsync(CancellationToken token)
    {
        while (true)
        {
            var current = Current();
            if (current.Configuration is null)
            {
                console.WriteLine();
                console.WriteLine("AI is not set up. Choose a provider (0 = back).");
                return await ConfigureAiAsync(token);
            }
            var choice = await ChooseAsync("AI descriptions", ["Use current AI settings", "Change AI settings"], "Back", token,
                "Current: " + current.Summary);
            if (choice == 0) return null;
            if (choice == 2)
            {
                if (await ConfigureAiAsync(token) is { } configured) return configured;
                continue;
            }
            console.Write("Testing connection... ");
            try
            {
                await work.TestConnectionAsync(current.Configuration, token);
                console.WriteLine("OK");
                return current.Configuration;
            }
            catch (QueryException e)
            {
                console.WriteLine("FAILED");
                console.WriteLine(e.Message);
            }
        }
    }

    private async Task DescribeAsync(MappedSolution mapped, AiConfiguration ai, CancellationToken token)
    {
        int missing;
        try { missing = await work.CountMissingDescriptionsAsync(mapped.IndexPath, token); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            console.WriteLine("Symbols could not be counted: " + e.Message);
            return;
        }
        if (missing == 0)
        {
            console.WriteLine("Every class/type and method already has a description.");
            return;
        }
        var options = new List<(string Label, int Limit)>();
        if (missing > 10) options.Add(("First 10 symbols", 10));
        if (missing > 100) options.Add(("First 100 symbols", 100));
        options.Add(($"All {N(missing)} symbols (~{N(missing)} requests)", missing));
        var choice = await ChooseAsync("Describe", options.Select(option => option.Label).ToArray(), "Back", token,
            $"{N(missing)} classes/types and methods have no description. Each needs one request to {ProviderName(ai.Provider)}.");
        if (choice == 0)
        {
            console.WriteLine("No descriptions requested. The map is saved.");
            return;
        }
        var limit = options[choice - 1].Limit;
        console.WriteLine($"Describing up to {N(limit)} symbols with {ProviderName(ai.Provider)} ({ai.Model}). Ctrl+C stops; finished descriptions stay saved.");
        try
        {
            var outcome = await work.DescribeAsync(mapped.IndexPath, ai, limit,
                done => console.Write($"\rDescribed {N(done)} of {N(limit)}"), token);
            console.WriteLine();
            console.WriteLine($"Generated {N(outcome.Generated)}, skipped {N(outcome.Skipped)}, still without a description {N(outcome.Remaining)}.");
            if (outcome.WarningsFile is not null) console.WriteLine("Skipped symbols are listed in " + outcome.WarningsFile);
            console.WriteLine($"Search them with: codeatlas search-symbols --index \"{mapped.IndexPath}\" --query \"<text>\"");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            console.WriteLine();
            console.WriteLine("Describing stopped: " + e.Message + " Descriptions already saved remain available.");
        }
    }

    private async Task AiSettingsAsync(CancellationToken token)
    {
        while (true)
        {
            var choice = await ChooseAsync("AI settings", ["Choose provider and model", "Remove a saved key"], "Back", token,
                "Current: " + Current().Summary);
            if (choice == 0) return;
            if (choice == 1) await ConfigureAiAsync(token);
            else await RemoveKeyAsync(token);
        }
    }

    private async Task<AiConfiguration?> ConfigureAiAsync(CancellationToken token)
    {
        while (true)
        {
            var choice = await ChooseAsync("AI provider", Providers.Select(provider => provider.Label).ToArray(), "Back", token);
            if (choice == 0) return null;
            var provider = Providers[choice - 1].Id;
            console.WriteLine(provider == "ollama"
                ? "Ollama runs on your machine by default: source excerpts stay local."
                : $"{ProviderName(provider)} is a cloud service: describing sends selected source excerpts to it. The connection test sends only a fixed prompt.");
            if (await ConfigureProviderAsync(provider, token) is { } configured) return configured;
        }
    }

    private async Task<AiConfiguration?> ConfigureProviderAsync(string provider, CancellationToken token)
    {
        var saved = LoadSaved() is { } previous && Normalize(previous.Provider) == provider ? previous : null;
        string? endpoint = null;
        string? enteredKey = null;
        AiPreferences? selected = null;
        var step = provider == "azure" ? 0 : 1;
        while (true)
        {
            if (step == 0)
            {
                endpoint = await AskAsync("Endpoint URL (https://<resource>.openai.azure.com/openai/v1/chat/completions)",
                    saved?.Endpoint, token);
                if (endpoint is null) return null;
                step = 1;
            }
            else if (step == 1)
            {
                var model = await AskAsync(provider == "azure" ? "Deployment name" : "Model", saved?.Model, token);
                if (model is null)
                {
                    if (provider != "azure") return null;
                    step = 0;
                    continue;
                }
                try { selected = Select(provider, model, endpoint, saved); }
                catch (QueryException e)
                {
                    console.WriteLine(e.Message);
                    step = provider == "azure" ? 0 : 1;
                    continue;
                }
                step = 2;
            }
            else if (step == 2)
            {
                var key = await ChooseKeyAsync(selected!, token);
                if (key.Back) { step = 1; continue; }
                enteredKey = key.Secret;
                step = 3;
            }
            else
            {
                console.Write("Testing connection... ");
                var setup = new AiSetup(readEnvironment, credentials, _ => null, work.TestConnectionAsync);
                try
                {
                    var result = await setup.ConfigureAsync(selected!, settingsPath, token, enteredKey);
                    console.WriteLine("OK");
                    if (result.KeyLocation is not null)
                        console.WriteLine((enteredKey is null ? "Using the key from: " : "Key saved in: ") + result.KeyLocation);
                    if (result.KeyWarning is not null) console.WriteLine("Warning: " + result.KeyWarning);
                    console.WriteLine("AI settings saved in: " + settingsPath);
                    return result.Configuration;
                }
                catch (QueryException e)
                {
                    console.WriteLine("FAILED");
                    console.WriteLine(e.Message);
                    console.WriteLine("Nothing was saved.");
                    // A newly typed key may be wrong: ask for it again. Otherwise return to the model prompt.
                    step = enteredKey is not null ? 2 : 1;
                }
            }
        }
    }

    private async Task<(bool Back, string? Secret)> ChooseKeyAsync(AiPreferences selected, CancellationToken token)
    {
        if (selected.Provider == "ollama") return (false, null);
        if (selected.FindEnvironmentKey(readEnvironment) is { } environment)
        {
            console.WriteLine($"Using the key from {environment.Location}. Nothing will be saved.");
            return (false, null);
        }
        var stored = credentials.Find(selected.Provider);
        while (true)
        {
            if (stored is not null)
            {
                var choice = await ChooseAsync("API key", [$"Use saved key ({stored.Location})", "Enter a new key"], "Back", token);
                if (choice == 0) return (true, null);
                if (choice == 1) return (false, null);
            }
            var secret = await AskSecretAsync(token);
            if (secret is not null) return (false, secret);
            if (stored is null) return (true, null);
        }
    }

    private async Task RemoveKeyAsync(CancellationToken token)
    {
        while (true)
        {
            var saved = Providers.Where(provider => provider.Id != "ollama")
                .Select(provider => (provider.Id, provider.Name, Found: credentials.Find(provider.Id)))
                .Where(entry => entry.Found is not null).ToArray();
            if (saved.Length == 0)
            {
                console.WriteLine("No keys are saved in the keychain or the credentials file.");
                foreach (var provider in Providers.Where(provider => provider.Id != "ollama")) EnvironmentNote(provider.Id);
                return;
            }
            var choice = await ChooseAsync("Remove a saved key",
                saved.Select(entry => $"{entry.Name} (in {entry.Found!.Location})").ToArray(), "Back", token);
            if (choice == 0) return;
            var (id, _, _) = saved[choice - 1];
            var removed = credentials.Remove(id);
            console.WriteLine(removed.Count == 0 ? "Nothing was removed." : "Removed from: " + string.Join(", ", removed));
            EnvironmentNote(id);
        }
    }

    private void EnvironmentNote(string provider)
    {
        if (new AiPreferences(provider, "-").FindEnvironmentKey(readEnvironment) is { } environment)
            console.WriteLine($"{ProviderName(provider)} still has a key in {environment.Location}. CodeAtlas does not change environment variables; remove it yourself if you no longer need it.");
    }

    private (AiConfiguration? Configuration, string Summary) Current()
    {
        try
        {
            var selected = AiPreferences.Select(new Dictionary<string, string>(), AiSettingsStore.Load(settingsPath), readEnvironment);
            if (selected is null) return (null, "AI off (no provider chosen).");
            var key = selected.FindKey(readEnvironment, credentials);
            var keyText = selected.Provider == "ollama" ? "local, no key" : key is null ? "no key found" : "key in " + key.Location;
            var configuration = key is null && selected.Provider != "ollama" ? null : selected.Create(key?.Value);
            return (configuration, $"{ProviderName(selected.Provider)}, model {selected.Model} ({keyText}).");
        }
        catch (QueryException e) { return (null, e.Message); }
    }

    private AiPreferences? LoadSaved()
    {
        try { return AiSettingsStore.Load(settingsPath); }
        catch (QueryException) { return null; }
    }

    private AiPreferences Select(string provider, string model, string? endpoint, AiPreferences? saved)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal) { ["--provider"] = provider, ["--model"] = model };
        if (endpoint is not null) options["--endpoint"] = endpoint;
        return AiPreferences.Select(options, saved, readEnvironment)!;
    }

    private async Task<string?> AskAsync(string label, string? fallback, CancellationToken token)
    {
        var prompt = label + " (0 = back)" + (fallback is null ? "" : $" [{fallback}]") + ": ";
        while (true)
        {
            var answer = await ReadAsync(prompt, token);
            if (answer == "0") return null;
            if (answer.Length > 0) return answer;
            if (fallback is not null) return fallback;
            console.WriteLine("Enter a value, or 0 to go back.");
        }
    }

    private async Task<string?> AskSecretAsync(CancellationToken token)
    {
        while (true)
        {
            console.Write("API key (hidden, 0 = back): ");
            var secret = await console.ReadSecretAsync(token);
            token.ThrowIfCancellationRequested();
            secret = (secret ?? throw new OperationCanceledException()).Trim();
            if (secret == "0") return null;
            if (secret.Length > 0) return secret;
            console.WriteLine("Enter the key, or 0 to go back.");
        }
    }

    private async Task<int> ChooseAsync(string title, IReadOnlyList<string> options, string zero, CancellationToken token,
        string? note = null)
    {
        console.WriteLine();
        console.WriteLine(title);
        if (note is not null) console.WriteLine("  " + note);
        for (var i = 0; i < options.Count; i++) console.WriteLine($"  {i + 1}  {options[i]}");
        console.WriteLine($"  0  {zero}");
        while (true)
        {
            var answer = await ReadAsync("Choice: ", token);
            if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= options.Count)
                return number;
            console.WriteLine($"Enter a number from 0 to {options.Count}.");
        }
    }

    private async Task<string> ReadAsync(string prompt, CancellationToken token)
    {
        console.Write(prompt);
        var line = await console.ReadLineAsync(token);
        token.ThrowIfCancellationRequested();
        return line?.Trim() ?? throw new OperationCanceledException();
    }

    private static string DataNotice(AiConfiguration ai) => ai.Endpoint.IsLoopback
        ? $"AI: {ProviderName(ai.Provider)} ({ai.Model}) on this machine. Source excerpts stay local."
        : $"AI: {ProviderName(ai.Provider)} ({ai.Model}). Selected source excerpts will be sent to {ai.Endpoint.Host}.";

    private static string ProviderName(string provider) =>
        Providers.FirstOrDefault(entry => entry.Id == provider).Name ?? provider;

    private static string Normalize(string provider)
    {
        try { return AiConfiguration.NormalizeProvider(provider); }
        catch (QueryException) { return provider; }
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>Real terminal. Reads run in the background so Ctrl+C (the token) ends a wait immediately.</summary>
internal sealed class SystemMenuConsole : IMenuConsole
{
    public void Write(string text) => Console.Write(text);
    public void WriteLine(string text = "") => Console.WriteLine(text);

    public Task<string?> ReadLineAsync(CancellationToken token) => WaitAsync(Task.Run(Console.ReadLine), token);

    public Task<string?> ReadSecretAsync(CancellationToken token) => WaitAsync(Task.Run<string?>(() =>
    {
        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return "0"; }
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return secret.ToString(); }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length == 0) continue;
                secret.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar) && secret.Length < 8192)
            {
                secret.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }), token);

    private static async Task<string?> WaitAsync(Task<string?> read, CancellationToken token)
    {
        var finished = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, token));
        if (finished != read) throw new OperationCanceledException(token);
        return await read;
    }
}

/// <summary>Connects the menu to the same Core operations the refresh and describe commands use.</summary>
internal sealed class StartMenuWork : IStartMenuWork
{
    public async Task<MappedSolution> MapAsync(string solution, CancellationToken token)
    {
        using var progress = new IndexProgressReporter();
        var index = await SolutionAnalysis.RefreshIndexAsync(solution, token, progress, 2);
        progress.Report(new(IndexStage.Saving));
        var snapshot = await IndexStore.PublishAsync(index, (stream, cancellation) =>
            TimingReport.WriteAsync(stream, index.Solution, index.IndexId, progress.SnapshotTimings(), cancellation), token);
        progress.Complete();
        return new(index.Solution, IndexStore.DefaultPath(index.Solution), index.Projects.Length, index.Types.Length,
            index.Methods.Length, index.Warnings.Length, snapshot.WarningsPath);
    }

    public async Task<int> CountMissingDescriptionsAsync(string indexPath, CancellationToken token)
    {
        var snapshot = await IndexStore.LoadSnapshotAsync(indexPath, token);
        return await DescriptionBatch.CountMissingAsync(snapshot, DescriptionBatch.Candidates(snapshot.Index), token);
    }

    public async Task<DescribeOutcome> DescribeAsync(string indexPath, AiConfiguration configuration, int limit,
        Action<int> progress, CancellationToken token)
    {
        var snapshot = await IndexStore.LoadSnapshotAsync(indexPath, token);
        await IndexStore.RequireFreshAsync(snapshot.Index, token);
        var symbols = DescriptionBatch.Candidates(snapshot.Index);
        var result = await DescriptionBatch.RunAsync(snapshot, symbols, configuration, limit, progress, token);
        return new(result.Generated, result.Failed.Count, symbols.Length - result.Generated - result.Cached, result.FailureFile);
    }

    public Task TestConnectionAsync(AiConfiguration configuration, CancellationToken token) =>
        AiSetup.TestConnectionAsync(configuration, token);
}
