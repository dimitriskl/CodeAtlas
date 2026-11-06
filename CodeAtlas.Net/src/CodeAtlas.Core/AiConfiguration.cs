using System.Net.Http.Headers;

namespace CodeAtlas.Explanations;

/// <summary>Process-local optional AI credentials. No credential is exposed in output or derived storage.</summary>
public sealed class AiConfiguration
{
    private readonly string? apiKey;
    public string Model { get; }
    public Uri Endpoint { get; }
    public string Provider { get; }
    public string? Effort { get; }

    public AiConfiguration(string? apiKey, string model, Uri? endpoint = null, string provider = "openai", string? effort = null)
    {
        Provider = NormalizeProvider(provider);
        var requestedEffort = effort?.Trim().ToLowerInvariant();
        Effort = requestedEffort == "default" ? null : requestedEffort;
        var efforts = Provider switch
        {
            "anthropic" => new[] { "low", "medium", "high", "xhigh", "max" },
            "ollama" => ["none", "low", "medium", "high"],
            "kimi" => ["none", "low", "high", "max"],
            _ => ["none", "minimal", "low", "medium", "high", "xhigh", "max"]
        };
        if (Effort is not null && !efforts.Contains(Effort))
            throw new QueryException("ai_configuration_invalid", "Unsupported effort for the selected provider. Use default to omit effort.");
        if (Provider != "ollama" && (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192
            || apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl))
            || string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl))
            throw Invalid();
        endpoint ??= DefaultEndpoint(Provider);
        if (!endpoint.IsAbsoluteUri || !(endpoint.Scheme == Uri.UriSchemeHttps
                || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.IdnHost.Length > 180)
            throw Invalid();
        this.apiKey = Provider == "ollama" ? null : apiKey;
        Model = model;
        Endpoint = endpoint;
    }

    public static AiConfiguration? FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    // A lookup seam avoids changing global process credentials in tests.
    internal static AiConfiguration? FromEnvironment(Func<string, string?> read)
    {
        var key = read("CODEATLAS_AI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var model = read("CODEATLAS_AI_MODEL");
        var endpoint = read("CODEATLAS_AI_ENDPOINT");
        if (model is null || endpoint is not null && !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw Invalid();
        return new(key, model, endpoint is null ? null : new Uri(endpoint, UriKind.Absolute));
    }

    public static string NormalizeProvider(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "llama" or "ollama" => "ollama",
        "openai" => "openai", "azure" => "azure", "anthropic" => "anthropic",
        "kimi" => "kimi", "openrouter" => "openrouter",
        _ => throw new QueryException("ai_configuration_invalid", "Provider must be openai, azure, anthropic, kimi, ollama (or llama), or openrouter.")
    };

    public static Uri DefaultEndpoint(string provider) => new(provider switch
    {
        "openai" => "https://api.openai.com/v1/chat/completions",
        "anthropic" => "https://api.anthropic.com/v1/messages",
        "kimi" => "https://api.moonshot.ai/v1/chat/completions",
        "ollama" => "http://localhost:11434/v1/chat/completions",
        "openrouter" => "https://openrouter.ai/api/v1/chat/completions",
        _ => throw new QueryException("ai_configuration_invalid", "Azure requires --endpoint with its full /openai/v1/chat/completions URL and a deployment name as --model.")
    });

    internal void Authorize(HttpRequestMessage message)
    {
        if (Provider == "anthropic")
        {
            message.Headers.Add("x-api-key", apiKey);
            message.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (apiKey is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static QueryException Invalid() => new("ai_configuration_invalid",
        "AI requires a model and a valid cloud-provider key; its endpoint must use HTTPS (HTTP is allowed only for loopback). Ollama needs no key.");
}
