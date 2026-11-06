using System.Text;
using CodeAtlas.Explanations;

namespace CodeAtlas.Cli;

/// <summary>Outcome of a tested AI setup. <see cref="KeyLocation"/> names where the key came from or was saved, never the key.</summary>
internal sealed record AiSetupResult(AiConfiguration Configuration, string? KeyLocation, string? KeyWarning);

internal sealed class AiSetup(Func<string, string?> readEnvironment, ICredentialStore credentials,
    Func<string, string?> promptSecret, Func<AiConfiguration, CancellationToken, Task> testConnection)
{
    /// <summary>
    /// Tests the connection before anything is saved. A key from an environment variable or an existing store is used
    /// as is; only a newly entered key (prompted or <paramref name="enteredKey"/>) is saved to the credential store.
    /// </summary>
    internal async Task<AiSetupResult> ConfigureAsync(AiPreferences selected, string path, CancellationToken token,
        string? enteredKey = null)
    {
        token.ThrowIfCancellationRequested();
        string? key = null;
        string? location = null;
        var entered = false;
        if (selected.Provider != "ollama")
        {
            if (!string.IsNullOrEmpty(enteredKey)) { key = enteredKey; entered = true; }
            else if (selected.FindKey(readEnvironment, credentials) is { } found) { key = found.Value; location = found.Location; }
            else
            {
                key = promptSecret(selected.KeyVariable) ?? throw new QueryException("ai_configuration_invalid",
                    $"An API key is required. Run configure-ai in an interactive terminal or set {selected.KeyVariable}.");
                entered = true;
            }
        }
        var configuration = selected.Create(key);
        await testConnection(configuration, token);
        token.ThrowIfCancellationRequested();
        string? warning = null;
        if (entered)
        {
            CredentialSaveResult saved;
            try { saved = credentials.Save(selected.Provider, key!); }
            catch (CredentialStoreException e)
            { throw new QueryException("ai_configuration_invalid", "Connection passed, but the key could not be saved: " + e.Message + " Settings were not replaced."); }
            location = saved.Location;
            warning = saved.Warning;
        }
        AiSettingsStore.Save(path, selected with { Endpoint = configuration.Endpoint.AbsoluteUri, Effort = configuration.Effort });
        return new(configuration, location, warning);
    }

    internal static string? ReadEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null);
    }

    internal static async Task TestConnectionAsync(AiConfiguration configuration, CancellationToken token)
    {
        using var client = CreateHttpClient();
        await new AiProviderClient(client, configuration).TestConnectionAsync(token);
    }

    internal static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };

    internal static AiSetup Interactive(HttpClient client) => new(ReadEnvironment, CredentialStore.ForCurrentUser(), ReadSecret,
        (configuration, token) => new AiProviderClient(client, configuration).TestConnectionAsync(token));

    private static string? ReadSecret(string name)
    {
        if (Console.IsInputRedirected) return null;
        Console.Error.Write($"Enter the API key, or set {name} (hidden; Esc cancels): ");
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
