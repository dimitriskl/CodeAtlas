using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAtlas.Cli;

/// <summary>
/// One loaded <see cref="LiveIndexSession"/>. Each request is applied to that session, and the
/// answer is read from the generation the session just published.
/// </summary>
internal sealed class LiveHost : IDisposable
{
    internal const string DefaultSearchMode = "assisted";
    internal const string DefaultSymbolKind = "method";
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LiveIndexSession session;

    private LiveHost(LiveIndexSession session) => this.session = session;

    public static async Task<LiveHost> StartAsync(string solution, CancellationToken cancellationToken = default) =>
        new(await LiveIndexSession.OpenAsync(solution, cancellationToken));

    public async Task<JsonObject> HandleAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        try { return await DispatchAsync(request, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(Command(request), exception);
        }
    }

    public async Task<JsonObject> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject request)
                return Error(null, new ArgumentException("A request must be a JSON object."));
            return await HandleAsync(request, cancellationToken);
        }
        catch (JsonException)
        {
            return Error(null, new ArgumentException("The request line is not a JSON object."));
        }
    }

    public static async Task ServeAsync(TextReader input, TextWriter output, LiveHost host, CancellationToken cancellationToken = default)
    {
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await host.HandleLineAsync(line, cancellationToken);
            await output.WriteLineAsync(response.ToJsonString(Json).AsMemory(), cancellationToken);
        }
    }

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            using var host = await StartAsync(SolutionPath(args), cancellationToken);
            await ServeAsync(input, output, host, cancellationToken);
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(Error(null, exception).ToJsonString(Json).AsMemory(), cancellationToken);
            return ResponseContract.Classify(exception).Exit;
        }
    }

    public void Dispose() => session.Dispose();

    private async Task<JsonObject> DispatchAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var command = Field(request, "command");
        return command switch
        {
            "edit" => await EditAsync(request, cancellationToken),
            "find-symbol" => await FindSymbolAsync(request, cancellationToken),
            "search" => await SearchAsync(request, cancellationToken),
            "find-references" => await FindReferencesAsync(request, cancellationToken),
            _ => throw new ArgumentException("Unknown request command: " + command + ".")
        };
    }

    private async Task<JsonObject> EditAsync(JsonObject request, CancellationToken cancellationToken)
    {
        await session.ApplyAsync(Field(request, "path"), Field(request, "text", allowEmpty: true), cancellationToken);
        var current = await session.CurrentIndexAsync(cancellationToken);
        var index = current.Snapshot.Index;
        return Finish("edit", current.Snapshot, current.Freshness, new
        {
            source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
            path = Field(request, "path"), warningCount = index.Warnings.Length, warnings = index.Warnings
        });
    }

    private async Task<JsonObject> FindSymbolAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var name = Field(request, "name");
        var kind = Optional(request, "kind", DefaultSymbolKind);
        if (kind == "method")
        {
            var found = await session.FindMethodsAsync(name, cancellationToken);
            var index = found.Snapshot.Index;
            return Finish("find-symbol", found.Snapshot, found.Freshness, new
            {
                source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                symbols = found.Value.Items, found.Value.Pagination,
                warningCount = index.Warnings.Length, warnings = index.Warnings
            });
        }
        var symbols = await session.FindSymbolsAsync(name, kind, cancellationToken);
        var catalog = symbols.Snapshot.Index;
        return Finish("find-symbol", symbols.Snapshot, symbols.Freshness, new
        {
            source = "index", catalog.Solution, catalog.IndexId, catalog.CreatedAtUtc, catalog.ToolVersion,
            symbols = symbols.Value.Items.Select(SymbolSummary.From).ToArray(), symbols.Value.Pagination,
            warningCount = catalog.Warnings.Length, warnings = catalog.Warnings
        });
    }

    private async Task<JsonObject> SearchAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var found = await session.SearchAsync(Field(request, "query"), Optional(request, "mode", DefaultSearchMode),
            Optional(request, "kind", DefaultSymbolKind), cancellationToken);
        var index = found.Snapshot.Index;
        var search = found.Value;
        return Finish("search", found.Snapshot, found.Freshness, new
        {
            source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
            symbols = search.Page.Items, pagination = search.Page.Pagination, search.RankingVersion, search.Mode,
            search.Language, search.QueryTokens, search.UnmatchedQueryTokens, search.EvidencePolicy, search.Decision,
            search.WithheldWeakMatches, search.Limitations, searchCoverage = search.Coverage,
            warningCount = index.Warnings.Length, warnings = index.Warnings
        });
    }

    private async Task<JsonObject> FindReferencesAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var symbol = Field(request, "symbol");
        var found = await session.ReferencePageAsync(symbol, cancellationToken);
        var index = found.Snapshot.Index;
        return Finish("find-references", found.Snapshot, found.Freshness, new
        {
            source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
            symbol, references = found.Value.Items, found.Value.Pagination,
            warningCount = index.Warnings.Length, warnings = index.Warnings
        });
    }

    private static JsonObject Finish(string command, LoadedSnapshot snapshot, IndexFreshness freshness, object result)
    {
        var output = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
        var warnings = output["warnings"]!.AsArray().Select(warning => warning!.GetValue<string>()).ToArray();
        var warningsPath = snapshot.WarningsPath;
        output["snapshotIntegrity"] = snapshot.VerifiedGeneration ? "verified-generation" : "legacy-unverified";
        output["warningsFile"] = warningsPath;
        output["coverageIssueCount"] = snapshot.Index.InputManifest?.Coverage.Issues.Length;
        if (snapshot.Index.InputManifest?.Coverage is { Status: "partial" } coverage)
            output["coverageMessage"] = warningsPath is null
                ? "Input coverage is partial; rebuild for the coverage report."
                : $"Input coverage is partial; {coverage.Issues.Length} issues recorded in {warningsPath}";
        output.Remove("warnings");
        output["warningMessage"] = warningsPath is null
            ? $"{warnings.Length} warnings recorded in legacy index; rebuild to create a generation warning report."
            : $"{warnings.Length} warnings recorded in {warningsPath}";
        ResponseContract.AddEnvelope(output, command, true, Json, ResponseContract.Freshness(freshness), true,
            snapshot.Index.SchemaVersion >= 2);
        return output;
    }

    private static JsonObject Error(string? command, Exception exception)
    {
        var (code, _) = ResponseContract.Classify(exception);
        var output = JsonSerializer.SerializeToNode(new
        {
            error = new { code, message = exception.Message, diagnostics = (exception as AnalysisException)?.Diagnostics ?? Array.Empty<string>() },
            warningCount = 0
        }, Json)!.AsObject();
        ResponseContract.AddEnvelope(output, command, false, Json);
        return output;
    }

    private static string SolutionPath(string[] args)
    {
        if (args.Length == 3 && args[0] == "live" && args[1] == "--solution" && !string.IsNullOrWhiteSpace(args[2]))
            return args[2];
        throw new ArgumentException("Usage: codeatlas live --solution <path.sln|path.slnx>");
    }

    private static string? Command(JsonObject request) =>
        request["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;

    private static string Optional(JsonObject request, string name, string fallback) =>
        request[name] is null ? fallback : Field(request, name);

    private static string Field(JsonObject request, string name, bool allowEmpty = false)
    {
        if (request[name] is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new ArgumentException("Request field '" + name + "' is required.");
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Request field '" + name + "' is required.");
        return text;
    }
}
