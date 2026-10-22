using System.Text;
using System.Text.Json;

namespace CodeAtlas.Explanations;

/// <summary>Bounded provider transport shared by explicit connection tests and source descriptions.</summary>
public sealed class AiProviderClient(HttpClient client, AiConfiguration configuration)
{
    private const int MaximumProviderBytes = 262_144;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var result = await CompleteAsync("Return only a JSON object: {\"ok\":true}.",
            "Connection test. Do not use tools or obtain any external context.", 4096, 4096, cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(result.Content);
            if (!json.RootElement.GetProperty("ok").GetBoolean()) throw Failed();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { throw Failed(); }
    }

    // The supplied HttpClient must disable redirects. Never log requests, authorization headers or error bodies.
    internal async Task<(string Content, string Model)> CompleteAsync(string instructions, string selected,
        int maxRequestBytes, int maxResponseBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var body = new Dictionary<string, object?> { ["model"] = configuration.Model };
        if (configuration.Provider == "anthropic")
        {
            body["system"] = instructions;
            body["messages"] = new[] { new { role = "user", content = selected } };
            body["max_tokens"] = 2048;
            if (configuration.Effort is not null) body["output_config"] = new { effort = configuration.Effort };
        }
        else
        {
            body["messages"] = new[] { new { role = "system", content = instructions }, new { role = "user", content = selected } };
            body["response_format"] = new { type = "json_object" };
            body[configuration.Provider is "openai" or "azure" ? "max_completion_tokens" : "max_tokens"] = 2048;
            if (configuration.Provider is "openai" or "azure") body["store"] = false;
            if (configuration.Effort is not null)
            {
                if (configuration.Provider == "kimi" && configuration.Effort == "none")
                    body["thinking"] = new { type = "disabled" };
                else body["reasoning_effort"] = configuration.Effort;
            }
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        if (payload.Length > maxRequestBytes)
            throw new QueryException("explanation_budget_exceeded", "AI request exceeds its byte budget.");
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint);
            configuration.Authorize(message);
            message.Content = new ByteArrayContent(payload);
            message.Content.Headers.ContentType = new("application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            // Error bodies can reflect credentials or source. Never read or report them.
            if (!response.IsSuccessStatusCode)
                throw new QueryException("ai_provider_failed", $"AI request failed (HTTP {(int)response.StatusCode}). Check provider, model, effort, endpoint and key; response body omitted.");
            var bytes = await ReadBoundedAsync(response.Content, token);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
            var root = json.RootElement;
            string? content;
            if (configuration.Provider == "anthropic")
            {
                if (root.GetProperty("stop_reason").GetString() != "end_turn") throw Failed();
                if (root.TryGetProperty("stop_details", out var details) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("type", out var type) && type.GetString() == "refusal") throw Failed();
                var blocks = root.GetProperty("content");
                if (blocks.ValueKind != JsonValueKind.Array) throw Failed();
                var texts = new List<string>();
                foreach (var block in blocks.EnumerateArray())
                {
                    var kind = block.GetProperty("type").GetString();
                    if (kind == "text") texts.Add(block.GetProperty("text").GetString()!);
                    else if (kind is not ("thinking" or "redacted_thinking")) throw Failed();
                }
                content = string.Concat(texts);
            }
            else
            {
                var choices = root.GetProperty("choices");
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw Failed();
                var choice = choices[0];
                if (choice.GetProperty("finish_reason").GetString() != "stop") throw Failed();
                var generated = choice.GetProperty("message");
                if (generated.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null) throw Failed();
                content = generated.GetProperty("content").GetString();
            }
            if (string.IsNullOrWhiteSpace(content) || Encoding.UTF8.GetByteCount(content) > maxResponseBytes) throw Failed();
            var model = root.TryGetProperty("model", out var value) ? value.GetString() : configuration.Model;
            if (string.IsNullOrWhiteSpace(model) || model.Length > 200) throw Failed();
            return (content, model);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Failed(); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw Failed(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaximumProviderBytes) throw Failed();
        using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, MaximumProviderBytes - (int)output.Length)), token);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length == MaximumProviderBytes) throw Failed();
        }
    }

    private static QueryException Failed() => new("ai_provider_failed",
        "AI generation or connection test failed or returned an incomplete or invalid response. Check provider configuration, model and effort.");
}
