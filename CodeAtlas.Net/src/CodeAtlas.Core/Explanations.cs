using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAtlas;

public sealed record ExplanationRequest(string SymbolId, string IndexId, string ToolVersion,
    string Name, string Signature, string ContentHash, string DocumentationStatus);

public sealed record ExplanationResponse(string SymbolId, string IndexId, string ContentHash,
    string Text, string Model);

public sealed record StoredExplanation(string SymbolId, string IndexId, string ContentHash,
    string Text, string Model, string Status);

public static class Explanations
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string ContentHash(DocumentedSymbol symbol)
    {
        var text = symbol.Id + "\n" + symbol.Signature + "\n" + symbol.Documentation.Status + "\n"
            + string.Join("\n", symbol.Documentation.Sections.Select(section => section.Tag + ":" + section.Text));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static ExplanationRequest CreateRequest(DocumentedSymbol symbol, string indexId, string toolVersion) =>
        new(symbol.Id, indexId, toolVersion, symbol.Name, symbol.Signature, ContentHash(symbol), symbol.Documentation.Status);

    public static async Task WriteRequestAsync(string path, ExplanationRequest request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, Json), cancellationToken);
    }

    public static async Task<StoredExplanation> ImportAsync(string solutionDirectory, DocumentedSymbol symbol,
        string indexId, ExplanationResponse response, CancellationToken cancellationToken = default)
    {
        var current = ContentHash(symbol);
        if (response.SymbolId != symbol.Id || response.IndexId != indexId)
            throw new QueryException("explanation_mismatch", "The explanation response names a different symbol or snapshot.");
        if (string.IsNullOrWhiteSpace(response.Text) || string.IsNullOrWhiteSpace(response.Model))
            throw new ArgumentException("An explanation needs text and a model name.");
        if (!string.Equals(response.ContentHash, current, StringComparison.OrdinalIgnoreCase))
            throw new QueryException("explanation_stale", "The symbol changed after the explanation request. Request a new explanation.");
        var stored = new StoredExplanation(symbol.Id, indexId, current, response.Text, response.Model, "current");
        var path = PathFor(solutionDirectory, indexId, symbol.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(stored, Json), cancellationToken);
        return stored;
    }

    public static async Task<StoredExplanation?> ReadAsync(string solutionDirectory, DocumentedSymbol symbol,
        string indexId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(solutionDirectory, indexId, symbol.Id);
        if (!File.Exists(path)) return null;
        var stored = JsonSerializer.Deserialize<StoredExplanation>(await File.ReadAllTextAsync(path, cancellationToken), Json)
            ?? throw new IndexFormatException("invalid_index", "The stored explanation is unreadable.");
        var status = stored.SymbolId == symbol.Id && stored.IndexId == indexId
            && string.Equals(stored.ContentHash, ContentHash(symbol), StringComparison.OrdinalIgnoreCase)
            ? "current" : "stale";
        return stored with { Status = status };
    }

    public static string PathFor(string solutionDirectory, string indexId, string symbolId)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(symbolId)));
        return Path.Combine(solutionDirectory, ".codeatlas", "explanations", indexId, safe + ".json");
    }
}
