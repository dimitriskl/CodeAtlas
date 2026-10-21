using System.Net.Http.Headers;

namespace CodeAtlas.Explanations;

/// <summary>Process-local optional AI credentials. No credential is exposed in output or derived storage.</summary>
public sealed class AiConfiguration
{
    private readonly string apiKey;
    public string Model { get; }
    public Uri Endpoint { get; }

    public AiConfiguration(string apiKey, string model, Uri? endpoint = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192
            || apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl))
            throw Invalid();
        endpoint ??= new Uri("https://api.openai.com/v1/chat/completions");
        if (!endpoint.IsAbsoluteUri || !(endpoint.Scheme == Uri.UriSchemeHttps
                || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.IdnHost.Length > 180)
            throw Invalid();
        this.apiKey = apiKey;
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

    internal void Authorize(HttpRequestMessage message) =>
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    private static QueryException Invalid() => new("ai_configuration_invalid",
        "AI requires CODEATLAS_AI_MODEL and a valid key; its endpoint must use HTTPS (HTTP is allowed only for loopback).");
}
