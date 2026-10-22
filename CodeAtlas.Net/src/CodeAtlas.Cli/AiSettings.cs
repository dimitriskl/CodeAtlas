using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeAtlas.Explanations;

namespace CodeAtlas.Cli;

internal sealed record AiPreferences(string Provider, string Model, string? Effort = null, string? Endpoint = null)
{
    internal string KeyVariable => "CODEATLAS_AI_" + Provider.ToUpperInvariant() + "_API_KEY";

    internal string? ReadKey(Func<string, string?> read)
    {
        if (Provider == "ollama") return null;
        string[] names = Provider switch
        {
            "openai" => ["CODEATLAS_AI_API_KEY", KeyVariable, "OPENAI_API_KEY"],
            "azure" => [KeyVariable, "AZURE_OPENAI_API_KEY", "AZUREAI_API_KEY"],
            "anthropic" => [KeyVariable, "ANTHROPIC_API_KEY"],
            "kimi" => [KeyVariable, "MOONSHOT_API_KEY"],
            "openrouter" => [KeyVariable, "OPENROUTER_API_KEY"],
            _ => []
        };
        return names.Select(read).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    internal AiConfiguration? Configuration(Func<string, string?> read)
    {
        var key = ReadKey(read);
        return Provider != "ollama" && key is null ? null : Create(key);
    }

    internal AiConfiguration Create(string? key)
    {
        if (Endpoint is not null && !Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
            throw new QueryException("ai_configuration_invalid", "AI endpoint must be an absolute URL.");
        return new(key, Model, Endpoint is null ? null : new Uri(Endpoint), Provider, Effort);
    }

    internal static AiPreferences? Select(IReadOnlyDictionary<string, string> options, AiPreferences? saved,
        Func<string, string?> read)
    {
        if (saved is null && !options.Keys.Any(IsSelectionOption)
            && string.IsNullOrWhiteSpace(read("CODEATLAS_AI_PROVIDER"))
            && string.IsNullOrWhiteSpace(read("CODEATLAS_AI_API_KEY"))) return null;
        var provider = AiConfiguration.NormalizeProvider(options.GetValueOrDefault("--provider")
            ?? read("CODEATLAS_AI_PROVIDER") ?? saved?.Provider ?? "openai");
        var sameProvider = saved is not null && AiConfiguration.NormalizeProvider(saved.Provider) == provider;
        var modelVariable = provider switch
        {
            "azure" => "AZURE_OPENAI_DEPLOYMENT", "anthropic" => "ANTHROPIC_MODEL", "kimi" => "KIMI_MODEL",
            "ollama" => "OLLAMA_MODEL", "openrouter" => "OPENROUTER_MODEL", _ => "OPENAI_MODEL"
        };
        var model = options.GetValueOrDefault("--model") ?? read("CODEATLAS_AI_MODEL")
            ?? (sameProvider ? saved!.Model : null) ?? read(modelVariable);
        var effort = options.GetValueOrDefault("--effort") ?? read("CODEATLAS_AI_EFFORT")
            ?? (sameProvider ? saved!.Effort : null);
        var endpoint = options.GetValueOrDefault("--endpoint") ?? read("CODEATLAS_AI_" + provider.ToUpperInvariant() + "_ENDPOINT")
            ?? (provider == "openai" ? read("CODEATLAS_AI_ENDPOINT") : null)
            ?? (sameProvider ? saved!.Endpoint : null) ?? ProviderEndpoint(provider, read);
        if (model is null)
        {
            var probe = new AiPreferences(provider, "", effort, endpoint);
            if (options.Keys.Any(IsSelectionOption) || saved is not null || probe.ReadKey(read) is not null)
                throw new QueryException("ai_configuration_invalid", "Select a model with --model (Azure uses a deployment name).");
            return null;
        }
        var selected = new AiPreferences(provider, model, effort == "default" ? null : effort, endpoint);
        // Validate non-secret options even when the user has not supplied a key yet.
        var validated = selected.Create(provider == "ollama" ? null : "configuration-validation");
        return selected with { Effort = validated.Effort, Endpoint = validated.Endpoint.AbsoluteUri };
    }

    internal static bool IsSelectionOption(string name) => name is "--provider" or "--model" or "--effort" or "--endpoint";

    private static string? ProviderEndpoint(string provider, Func<string, string?> read)
    {
        var address = provider switch
        {
            "azure" => read("AZURE_OPENAI_ENDPOINT"), "anthropic" => read("ANTHROPIC_BASE_URL"),
            "kimi" => read("KIMI_BASE_URL"), "ollama" => read("OLLAMA_BASE_URL") ?? read("OLLAMA_HOST"),
            "openrouter" => read("OPENROUTER_BASE_URL"), _ => null
        };
        if (address is null) return null;
        var value = address.TrimEnd('/');
        if (value.EndsWith("/chat/completions", StringComparison.Ordinal) || value.EndsWith("/messages", StringComparison.Ordinal)) return value;
        if (provider == "azure" && !value.EndsWith("/openai/v1", StringComparison.Ordinal)) value += "/openai/v1";
        else if (provider is "ollama" or "anthropic" && !value.EndsWith("/v1", StringComparison.Ordinal)) value += "/v1";
        return value + (provider == "anthropic" ? "/messages" : "/chat/completions");
    }
}

internal static class AiSettingsStore
{
    internal static string DefaultPath => Path.Combine(Environment.CurrentDirectory, ".codeatlas", "appsettings.json");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static AiPreferences? Load(string path)
    {
        var root = Read(path);
        try { return root["ai"]?.Deserialize<AiPreferences>(Json); }
        catch (JsonException) { throw Invalid(); }
    }

    internal static void Save(string path, AiPreferences preferences)
    {
        var root = Read(path);
        root["ai"] = JsonSerializer.SerializeToNode(preferences, Json);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(Json) + Environment.NewLine);
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonObject Read(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 65_536) throw Invalid();
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static QueryException Invalid() => new("ai_configuration_invalid", "AI appsettings.json is invalid or exceeds 64 KiB; its contents were not printed or replaced.");
}
