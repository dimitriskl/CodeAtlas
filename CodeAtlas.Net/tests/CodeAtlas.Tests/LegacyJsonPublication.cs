using System.Text;
using System.Text.Json;
using CodeAtlas.Persistence;

namespace CodeAtlas.Tests;

/// <summary>
/// Publishes a generation exactly as CodeAtlas did before the SQLite store (manifest version 1:
/// index.json plus catalog.json), so legacy read paths stay covered.
/// </summary>
internal static class LegacyJsonPublication
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static async Task<LoadedSnapshot> PublishAsync(CodebaseIndex index)
    {
        var queryIndex = index with { Symbols = null, DocumentationCoverage = null, StructuralCatalog = null, Duplicates = null };
        var catalog = new SymbolCatalogFile(index.Symbols, index.DocumentationCoverage, index.StructuralCatalog, index.Duplicates)
            { IndexId = index.IndexId, Solution = index.Solution };
        static GenerationArtifact Text(string name, string value) =>
            new(name, (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(value), token).AsTask());
        var generation = await new GenerationStore().PublishAsync(index.Solution, index.IndexId,
        [
            new("index.json", (stream, token) => JsonSerializer.SerializeAsync(stream, queryIndex, Json, token)),
            new("catalog.json", (stream, token) => JsonSerializer.SerializeAsync(stream, catalog, Json, token)),
            Text("overview.md", IndexStore.Overview(index)),
            Text("warnings.txt", IndexStore.WarningText(index)),
            Text("timings.json", "{}")
        ], token => IndexStore.RequireFreshAsync(index, token));
        return new(index, generation.ManifestPath, generation.Directory, true);
    }
}
