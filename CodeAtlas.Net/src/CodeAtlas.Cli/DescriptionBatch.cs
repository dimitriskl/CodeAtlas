using System.Text.Json;
using CodeAtlas.Explanations;

namespace CodeAtlas.Cli;

internal sealed record DescriptionBatchResult(int Generated, int Cached, IReadOnlyList<string> Failed, string? FailureFile);

/// <summary>Generates missing class/type and method descriptions one request at a time, up to a limit.</summary>
internal static class DescriptionBatch
{
    // The response file must use the same camelCase contract the import step validates.
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static DocumentedSymbol[] Candidates(CodebaseIndex index, string? project = null, string? symbolId = null) =>
        DocumentationSnapshotQueries.RequireCoverage(index)
            .Where(symbol => symbol.Kind is "method" or "type")
            .Where(symbol => project is null || symbol.Project == project)
            .Where(symbol => symbolId is null || symbol.Id == symbolId)
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal).ToArray();

    internal static async Task<int> CountMissingAsync(LoadedSnapshot snapshot, IReadOnlyCollection<DocumentedSymbol> symbols,
        CancellationToken token)
    {
        var descriptions = await ExplanationSnapshotAdapter.ForSnapshot(snapshot).GetSearchableAsync(token);
        return symbols.Count(symbol => !descriptions.ContainsKey(symbol.Id));
    }

    internal static async Task<DescriptionBatchResult> RunAsync(LoadedSnapshot snapshot, IReadOnlyCollection<DocumentedSymbol> symbols,
        AiConfiguration configuration, int limit, Action<int>? generatedProgress,
        CancellationToken token)
    {
        var index = snapshot.Index;
        var generated = 0;
        var failed = new List<string>();
        string? failureFile = null;
        var service = ExplanationSnapshotAdapter.ForSnapshot(snapshot);
        var descriptions = await service.GetSearchableAsync(token);
        var cached = symbols.Count(symbol => descriptions.ContainsKey(symbol.Id));
        var selected = symbols.Where(symbol => !descriptions.ContainsKey(symbol.Id));
        using var http = AiSetup.CreateHttpClient();
        var generator = new ExplanationGenerator(http, configuration);
        var exchange = Path.Combine(Path.GetDirectoryName(index.Solution)!, ".codeatlas", "agent-exchange");
        foreach (var symbol in selected)
        {
            if (generated >= limit) break;
            token.ThrowIfCancellationRequested();
            var stem = Path.Combine(exchange, Guid.NewGuid().ToString("N"));
            try
            {
                var request = await service.ExportAsync(symbol.Id, stem + ".request.json",
                    referenceLimit: 0, cancellationToken: token);
                var response = await generator.GenerateAsync(request, token);
                await File.WriteAllTextAsync(stem + ".response.json",
                    JsonSerializer.Serialize(response, Json), token);
                await service.AcceptAsync(stem + ".response.json", token);
                generated++;
                generatedProgress?.Invoke(generated);
            }
            catch (QueryException error) when (error.Code is "explanation_budget_exceeded" or "unsupported_explanation_context")
            {
                failed.Add($"{symbol.Id}: {error.Code}: {error.Message}");
            }
        }
        if (failed.Count != 0)
        {
            Directory.CreateDirectory(exchange);
            failureFile = Path.Combine(exchange, "describe-" + Guid.NewGuid().ToString("N") + ".warnings.txt");
            await File.WriteAllLinesAsync(failureFile, failed, token);
        }
        return new(generated, cached, failed, failureFile);
    }
}
