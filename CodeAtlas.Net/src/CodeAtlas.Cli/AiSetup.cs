using System.Text;
using CodeAtlas.Explanations;

namespace CodeAtlas.Cli;

internal sealed class AiSetup(Func<string, string?> readEnvironment, Action<string, string> persistCredential,
    Func<string, string?> promptSecret, Func<AiConfiguration, CancellationToken, Task> testConnection)
{
    internal async Task<AiConfiguration> ConfigureAsync(AiPreferences selected, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = selected.ReadKey(readEnvironment);
        if (selected.Provider != "ollama" && key is null)
            key = promptSecret(selected.KeyVariable) ?? throw new QueryException("ai_configuration_invalid",
                $"An API key is required. Run configure-ai in an interactive terminal or set {selected.KeyVariable}.");
        var configuration = selected.Create(key);
        await testConnection(configuration, token);
        token.ThrowIfCancellationRequested();
        if (selected.Provider != "ollama") persistCredential(selected.KeyVariable, key!);
        AiSettingsStore.Save(path, selected with { Endpoint = configuration.Endpoint.AbsoluteUri, Effort = configuration.Effort });
        return configuration;
    }

    internal static string? ReadEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null);
    }

    internal static AiSetup Interactive(HttpClient client) => new(ReadEnvironment, (name, key) =>
    {
        if (!OperatingSystem.IsWindows())
            throw new QueryException("ai_credential_persistence_unavailable", "Persistent user environment variables require Windows. Set the provider key in your shell and use describe directly on other systems.");
        try
        {
            Environment.SetEnvironmentVariable(name, key, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, key);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { throw new QueryException("ai_configuration_invalid", "Connection passed, but the user environment variable could not be saved. Settings were not replaced."); }
    }, ReadSecret, (configuration, token) => new AiProviderClient(client, configuration).TestConnectionAsync(token));

    private static string? ReadSecret(string name)
    {
        if (Console.IsInputRedirected) return null;
        Console.Error.Write($"Enter {name} (hidden; Esc cancels): ");
        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape) { Console.Error.WriteLine(); return null; }
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return secret.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
            else if (!char.IsControl(key.KeyChar) && secret.Length < 8192) secret.Append(key.KeyChar);
        }
    }
}
