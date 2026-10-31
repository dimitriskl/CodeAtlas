using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAtlas.Cli;

/// <summary>
/// Stdio MCP server for one solution. Queries read the published generation through one
/// <see cref="WarmIndexSession"/>. index builds and publishes a generation for that solution, and the
/// next query reloads it.
/// </summary>
internal sealed class McpHost
{
    private const string Protocol = "2025-06-18";
    private const string IoFailure = "Snapshot inputs could not be verified. Publication was not committed; resolve the input access failure and retry.";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string solutionFile;
    private readonly string solutionDirectory;
    private readonly WarmIndexSession warm;

    private McpHost(string solution)
    {
        solutionFile = SourcePaths.Real(Path.GetFullPath(solution));
        solutionDirectory = SourcePaths.Real(Path.GetDirectoryName(solutionFile)!);
        warm = new WarmIndexSession(solutionFile);
    }

    public static McpHost Start(string solution) => new(solution);

    public static async Task ServeAsync(TextReader input, TextWriter output, McpHost host, CancellationToken cancellationToken = default)
    {
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await host.HandleLineAsync(line, cancellationToken);
            if (response is null) continue;
            await output.WriteLineAsync(response.ToJsonString().AsMemory(), cancellationToken);
        }
    }

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length != 3 || args[0] != "mcp" || args[1] != "--solution" || string.IsNullOrWhiteSpace(args[2]))
        {
            await error.WriteLineAsync("Usage: codeatlas mcp --solution <path.sln|path.slnx>".AsMemory(), cancellationToken);
            return 2;
        }
        if (!File.Exists(args[2]))
        {
            await error.WriteLineAsync("Solution file was not found.".AsMemory(), cancellationToken);
            return 2;
        }
        await ServeAsync(input, output, Start(args[2]), cancellationToken);
        return 0;
    }

    public async Task<JsonObject?> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(line); }
        catch (JsonException) { return RpcError(null, -32700, "Parse error"); }
        if (parsed is not JsonObject request) return RpcError(null, -32600, "Invalid Request");
        if (request["method"] is not JsonValue methodNode || !methodNode.TryGetValue<string>(out var method) || method.Length == 0)
            return request.ContainsKey("id") ? RpcError(request["id"], -32600, "Invalid Request") : null;
        if (!request.ContainsKey("id")) return null;
        try { return await DispatchAsync(request["id"], method, request["params"] as JsonObject, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RpcError(request["id"], -32603, "Internal error");
        }
    }

    private async Task<JsonObject> DispatchAsync(JsonNode? id, string method, JsonObject? parameters, CancellationToken cancellationToken) =>
        method switch
        {
            "initialize" => Result(id, Initialize(parameters)),
            "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
            "tools/call" => await CallAsync(id, parameters, cancellationToken),
            "ping" => Result(id, new JsonObject()),
            _ => RpcError(id, -32601, "Method not found")
        };

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"] is JsonValue value && value.TryGetValue<string>(out var version) ? version : null;
        return new JsonObject
        {
            ["protocolVersion"] = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" ? requested : Protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "codeatlas", ["version"] = CodeAtlasVersion.Current }
        };
    }

    private async Task<JsonObject> CallAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        try
        {
            var tool = parameters?["name"] is JsonValue name && name.TryGetValue<string>(out var called) ? called : "";
            var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
            var envelope = tool switch
            {
                "find_symbol" => await FindSymbolAsync(arguments, cancellationToken),
                "index_status" => await IndexStatusAsync(arguments, cancellationToken),
                "coverage" => await CoverageAsync(arguments, cancellationToken),
                "index" => await IndexAsync(arguments, cancellationToken),
                _ => Failure(tool.Length == 0 ? null : tool, "invalid_arguments", "Unknown tool.", [])
            };
            return ToolResult(id, envelope, envelope["success"]!.GetValue<bool>() == false);
        }
        catch (Exception exception) when (KnownFailure(ToolName(parameters), exception) is { } failure)
        {
            return ToolResult(id, failure, true);
        }
    }

    private static JsonObject? KnownFailure(string? command, Exception exception) => exception switch
    {
        QueryException query => Failure(command, query.Code, query.Message, query.Diagnostics),
        IndexFormatException format => Failure(command, format.Code, format.Message, format.Diagnostics),
        StaleIndexException stale => Failure(command, "stale_index", stale.Message, stale.Diagnostics),
        SqliteUnavailableException sqlite => Failure(command, sqlite.Code, sqlite.Message, sqlite.Diagnostics),
        AnalysisException analysis => Failure(command, "analysis_failed", analysis.Message, analysis.Diagnostics),
        _ => null
    };

    private static string? ToolName(JsonObject? parameters) =>
        parameters?["name"] is JsonValue name && name.TryGetValue<string>(out var tool) && tool.Length > 0 ? tool : null;

    private async Task<JsonObject> FindSymbolAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var query = ReadQuery(arguments);
        RequireWorkspace(query.Workspace);
        var manifest = IndexStore.DefaultPath(solutionFile);
        if (!File.Exists(manifest))
            return query.SnapshotId is null
                ? SymbolSuccess(NoIndexFreshness(), Coverage(catalog: true, noIndex: true), Page(0, query.Offset, query.Limit, 0, null, false), [], 0)
                : Mismatch("find_symbol");
        var (snapshot, freshness) = await warm.CurrentAsync(cancellationToken);
        if (query.SnapshotId is not null && !string.Equals(query.SnapshotId, snapshot.Index.IndexId, StringComparison.Ordinal))
            return Mismatch("find_symbol");
        var coverage = Coverage(snapshot.Index.SchemaVersion >= 2, noIndex: false);
        if (freshness.IsStale)
            return Stale("find_symbol", snapshot, freshness, coverage);
        if (freshness.Diagnostics.Length != 0)
            return Io("find_symbol", snapshot, freshness, coverage);
        var rows = (query.Kind ?? "all") == "method"
            ? Methods(snapshot, query)
            : Symbols(snapshot, query);
        var matches = rows.Where(row => row.Name == query.Name || row.QualifiedName == query.Name)
            .OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var page = matches.Skip(query.Offset).Take(query.Limit).ToArray();
        return SymbolSuccess(Freshness(freshness), coverage,
            Page(matches.Length, query.Offset, query.Limit, page.Length, snapshot.Index.IndexId, true), page, snapshot.Index.Warnings.Length);
    }

    private async Task<JsonObject> IndexStatusAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var workspace = Text(arguments, "workspace", "workspace is required.", "workspace must not be empty.");
        if (!IsBound(workspace) || !File.Exists(Manifest))
            return GenerationEnvelope("index_status", NoIndexFreshness(), Coverage(catalog: true, noIndex: true), snapshot: null, 0);
        var (snapshot, freshness) = await warm.CurrentAsync(cancellationToken);
        return GenerationEnvelope("index_status", Freshness(freshness), LoadedCoverage(snapshot, emptyLimitations: true), snapshot, snapshot.Index.Warnings.Length);
    }

    private async Task<JsonObject> CoverageAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var workspace = OptionalText(arguments, "workspace", "workspace must not be empty.");
        if (workspace is null || !IsBound(workspace) || !File.Exists(Manifest))
            return Zero("coverage", NoIndexFreshness(), Coverage(catalog: true, noIndex: true), snapshotId: null, 0);
        var (snapshot, freshness) = await warm.CurrentAsync(cancellationToken);
        var coverage = LoadedCoverage(snapshot, emptyLimitations: true);
        if (freshness.IsStale) return Stale("coverage", snapshot, freshness, coverage);
        if (freshness.Diagnostics.Length != 0) return Io("coverage", snapshot, freshness, coverage);
        return Zero("coverage", Freshness(freshness), coverage, snapshot.Index.IndexId, snapshot.Index.Warnings.Length);
    }

    private async Task<JsonObject> IndexAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var workspace = OptionalText(arguments, "workspace", "workspace must not be empty.");
        if (workspace is null ? !IsBound(Environment.CurrentDirectory) : !IsBound(workspace))
            throw new QueryException("invalid_arguments", "workspace must be the solution directory.");
        SqliteAvailability.Require();
        var index = await SolutionAnalysis.CreateIndexAsync(solutionFile, cancellationToken);
        var freshness = await IndexStore.CheckFreshnessAsync(index, cancellationToken);
        var snapshot = await IndexStore.PublishAsync(index, cancellationToken: cancellationToken);
        return GenerationEnvelope("index", Freshness(freshness), LoadedCoverage(snapshot, emptyLimitations: true), snapshot, index.Warnings.Length);
    }

    private string Manifest => IndexStore.DefaultPath(solutionFile);

    private bool IsBound(string workspace) =>
        TryReal(workspace, out var real) && (Paths.Equals(real, solutionDirectory) || Paths.Equals(real, solutionFile));

    private static SymbolQuery ReadQuery(JsonObject arguments)
    {
        var workspace = Text(arguments, "workspace", "workspace is required.", "workspace must not be empty.");
        var name = Text(arguments, "name", "name must not be empty.", "name must not be empty.");
        var kind = OptionalText(arguments, "kind", "kind must not be empty.");
        if (kind is not null && kind is not ("method" or "type" or "property" or "field" or "all"))
            throw new QueryException("invalid_arguments", "Unknown symbol kind.");
        var project = OptionalText(arguments, "project", "project must not be empty.");
        var offset = OptionalInt(arguments, "offset", 0, "offset must be a nonnegative integer.", 0, int.MaxValue);
        var limit = OptionalInt(arguments, "limit", 100, "limit must be an integer from 1 through 1000.", 1, QueryRequest.MaximumLimit);
        var snapshot = OptionalText(arguments, "snapshotId", "snapshotId must not be empty.");
        if (offset > 0 && snapshot is null)
            throw new QueryException("invalid_arguments", "offset greater than zero requires snapshotId.");
        return new(workspace, name, kind, project, offset, limit, snapshot);
    }

    private void RequireWorkspace(string workspace)
    {
        if (!TryReal(workspace, out var real) || (!Paths.Equals(real, solutionDirectory) && !Paths.Equals(real, solutionFile)))
            throw new QueryException("invalid_arguments", "workspace must be the solution directory.");
    }

    private static List<SymbolRow> Methods(LoadedSnapshot snapshot, SymbolQuery query)
    {
        var simple = SimpleName(query.Name);
        return Every(offset => SnapshotQueries.FindMethods(snapshot, simple,
                new QueryRequest(query.Project, null, QueryRequest.MaximumLimit, offset, snapshot.Index.IndexId)))
            .Select(From).ToList();
    }

    private static List<SymbolRow> Symbols(LoadedSnapshot snapshot, SymbolQuery query)
    {
        var simple = SimpleName(query.Name);
        var kind = query.Kind ?? "all";
        return Every(offset => SnapshotQueries.FindSymbols(snapshot, simple, kind,
                new QueryRequest(query.Project, null, QueryRequest.MaximumLimit, offset, snapshot.Index.IndexId)))
            .Select(From).ToList();
    }

    private static List<T> Every<T>(Func<int, QueryPage<T>> page)
    {
        var items = new List<T>();
        var offset = 0;
        while (true)
        {
            var found = page(offset);
            items.AddRange(found.Items);
            if (!found.Pagination.HasMore || found.Pagination.NextOffset is not int next || next <= offset) return items;
            offset = next;
        }
    }

    private static JsonObject SymbolSuccess(JsonObject freshness, JsonObject coverage, JsonObject pagination,
        IReadOnlyList<SymbolRow> symbols, int warningCount) =>
        Envelope("find_symbol", true, warningCount, freshness, coverage, pagination, new JsonObject { ["symbols"] = Symbols(symbols) }, error: null);

    private static JsonObject GenerationEnvelope(string command, JsonObject freshness, JsonObject coverage, LoadedSnapshot? snapshot, int warningCount) =>
        Envelope(command, true, warningCount, freshness, coverage, Page(0, 0, 0, 0, snapshot?.Index.IndexId, false),
            snapshot is null ? NoIndexResult() : GenerationResult(snapshot), error: null);

    private static JsonObject Zero(string command, JsonObject freshness, JsonObject coverage, string? snapshotId, int warningCount) =>
        Envelope(command, true, warningCount, freshness, coverage, Page(0, 0, 0, 0, snapshotId, false), result: null, error: null);

    private static JsonObject Mismatch(string command) =>
        Failure(command, "snapshot_mismatch", "The index was replaced. Start a new query at offset zero.", []);

    private static JsonObject Stale(string command, LoadedSnapshot snapshot, IndexFreshness freshness, JsonObject coverage) =>
        Failure(command, "stale_index", "The saved index is stale.", [], Freshness(freshness), coverage, snapshot.Index.Warnings.Length);

    private static JsonObject Io(string command, LoadedSnapshot snapshot, IndexFreshness freshness, JsonObject coverage) =>
        Failure(command, "snapshot_io_failed", IoFailure, freshness.Diagnostics, Freshness(freshness), coverage, snapshot.Index.Warnings.Length);

    private static JsonObject Failure(string? command, string code, string message, IReadOnlyList<string> diagnostics, JsonObject? freshness = null,
        JsonObject? coverage = null, int warningCount = 0) =>
        Envelope(command, false, warningCount, freshness, coverage, pagination: null, result: null,
            new JsonObject { ["code"] = code, ["message"] = message, ["diagnostics"] = Strings(diagnostics) });

    private static JsonObject Envelope(string? command, bool success, int warningCount, JsonObject? freshness, JsonObject? coverage,
        JsonObject? pagination, JsonNode? result, JsonObject? error)
    {
        var output = new JsonObject
        {
            ["contractVersion"] = ResponseContract.Version,
            ["command"] = command,
            ["success"] = success,
            ["responseToolVersion"] = CodeAtlasVersion.Current,
            ["warningCount"] = warningCount,
            ["freshness"] = freshness,
            ["coverage"] = coverage
        };
        if (pagination is not null) output["pagination"] = pagination;
        if (result is not null) output["result"] = result;
        if (error is not null) output["error"] = error;
        return output;
    }

    private static JsonObject GenerationResult(LoadedSnapshot snapshot)
    {
        var (projects, files, symbols) = Counts(snapshot);
        return new JsonObject
        {
            ["generationId"] = snapshot.Index.IndexId,
            ["schemaVersion"] = snapshot.Index.SchemaVersion,
            ["contractVersion"] = ResponseContract.Version,
            ["appVersion"] = snapshot.Index.ToolVersion,
            ["counts"] = new JsonObject { ["projects"] = projects, ["files"] = files, ["symbols"] = symbols },
            ["index"] = Path.GetFullPath(snapshot.Path)
        };
    }

    private static JsonObject NoIndexResult() => new()
    {
        ["generationId"] = null,
        ["schemaVersion"] = null,
        ["contractVersion"] = ResponseContract.Version,
        ["appVersion"] = null,
        ["counts"] = new JsonObject { ["projects"] = 0, ["files"] = 0, ["symbols"] = 0 },
        ["index"] = null
    };

    private static (int Projects, int Files, int Symbols) Counts(LoadedSnapshot snapshot)
    {
        if (snapshot.Store is { } store) return store.Use(Count);
        var projects = snapshot.Index.Projects;
        return (projects.Length, SourceFiles(projects), snapshot.Index.Symbols?.Length ?? 0);
    }

    private static (int Projects, int Files, int Symbols) Count(CodeAtlas.Storage.SqliteIndexReader.Session session)
    {
        var projects = session.Projects();
        return (projects.Length, SourceFiles(projects), checked((int)session.SymbolCount("all")));
    }

    private static int SourceFiles(IEnumerable<IndexedProject> projects) =>
        projects.SelectMany(project => project.Files).Distinct(StringComparer.Ordinal).Count();

    private static JsonObject LoadedCoverage(LoadedSnapshot snapshot, bool emptyLimitations) =>
        Coverage(snapshot.Index.SchemaVersion >= 2, noIndex: false, emptyLimitations);

    private static JsonObject Page(int total, int offset, int limit, int returned, string? snapshotId, bool continuation)
    {
        var hasMore = (long)offset + returned < total;
        return new JsonObject
        {
            ["offset"] = offset,
            ["limit"] = limit,
            ["returned"] = returned,
            ["total"] = total,
            ["hasMore"] = hasMore,
            ["nextOffset"] = hasMore ? offset + returned : null,
            ["snapshotId"] = snapshotId,
            ["continuationSupported"] = continuation
        };
    }

    private static JsonArray Symbols(IReadOnlyList<SymbolRow> rows)
    {
        var symbols = new JsonArray();
        foreach (var row in rows)
        {
            var signatures = new JsonArray();
            if (row.HasSignature) signatures.Add(row.Signature);
            symbols.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["kind"] = row.Kind,
                ["qualifiedName"] = row.QualifiedName,
                ["project"] = row.Project,
                ["path"] = row.Path,
                ["line"] = row.Line,
                ["column"] = row.Column,
                ["signatures"] = signatures
            });
        }
        return symbols;
    }

    private static JsonObject Freshness(IndexFreshness freshness)
    {
        var reasons = freshness.Coverage.Issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return new JsonObject
        {
            ["state"] = freshness.Status,
            ["checkedAtUtc"] = freshness.CheckedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["isStale"] = freshness.IsStale,
            ["changedFiles"] = Strings(freshness.ChangedFiles.Order(StringComparer.Ordinal)),
            ["scope"] = "tracked-input-content-hashes",
            ["inputCoverage"] = new JsonObject
            {
                ["status"] = freshness.Coverage.Status,
                ["scope"] = freshness.Coverage.Scope,
                ["issueCount"] = freshness.Coverage.Issues.Length,
                ["reasonCodes"] = Strings(reasons)
            },
            ["diagnostics"] = Strings(freshness.Diagnostics)
        };
    }

    private static JsonObject NoIndexFreshness() => new()
    {
        ["state"] = "unknown",
        ["checkedAtUtc"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ["isStale"] = false,
        ["changedFiles"] = new JsonArray(),
        ["scope"] = "tracked-input-content-hashes",
        ["inputCoverage"] = new JsonObject
        {
            ["status"] = "unknown",
            ["scope"] = "no-index",
            ["issueCount"] = 0,
            ["reasonCodes"] = new JsonArray()
        },
        ["diagnostics"] = new JsonArray()
    };

    private static JsonObject Coverage(bool catalog, bool noIndex, bool emptyLimitations = false)
    {
        var source = JsonSerializer.SerializeToNode(ResponseContract.Coverage(catalog), Json)!.AsObject();
        JsonNode limitations = noIndex ? new JsonArray("no-index")
            : emptyLimitations ? new JsonArray() : source["limitations"]!.DeepClone();
        return new JsonObject
        {
            ["language"] = source["language"]!.DeepClone(),
            ["symbols"] = source["symbols"]!.DeepClone(),
            ["references"] = source["references"]!.DeepClone(),
            ["referenceSymbolKinds"] = source["referenceSymbolKinds"]!.DeepClone(),
            ["templateCoverage"] = "unknown",
            ["runtimeCallGraph"] = source["runtimeCallGraph"]!.DeepClone(),
            ["exclusions"] = source["exclusions"]!.DeepClone(),
            ["limitations"] = limitations
        };
    }

    private static JsonArray Tools()
    {
        var tools = new JsonArray
        {
            Schema("find_symbol", "Find source symbols by exact name or qualified name in the published index.",
                ["workspace", "name", "kind", "project", "offset", "limit", "snapshotId"], ["workspace", "name"],
                ["string", "string", "string", "string", "integer", "integer", "string"]),
            Schema("index_status", "Read the published generation and whether its tracked inputs still match.",
                ["workspace"], ["workspace"], ["string"]),
            Schema("coverage", "Read the analysis boundary. Omit workspace when no index should be read.",
                ["workspace"], [], ["string"]),
            Schema("index", "Build and publish a generation for this session's solution.",
                ["workspace"], [], ["string"])
        };
        return tools;
    }

    private static JsonObject Schema(string name, string description, string[] properties, string[] required, string[] types)
    {
        var fields = new JsonObject();
        for (var i = 0; i < properties.Length; i++)
            fields[properties[i]] = new JsonObject { ["type"] = types[i] };
        var requiredNames = new JsonArray();
        foreach (var item in required) requiredNames.Add(item);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = fields };
        if (required.Length > 0) schema["required"] = requiredNames;
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static string Text(JsonObject arguments, string name, string missing, string empty)
    {
        if (arguments[name] is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new QueryException("invalid_arguments", missing);
        if (text.Length == 0) throw new QueryException("invalid_arguments", empty);
        return text;
    }

    private static string? OptionalText(JsonObject arguments, string name, string empty)
    {
        if (!arguments.ContainsKey(name) || arguments[name] is null) return null;
        if (arguments[name] is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length == 0)
            throw new QueryException("invalid_arguments", empty);
        return text;
    }

    private static int OptionalInt(JsonObject arguments, string name, int fallback, string message, int minimum, int maximum)
    {
        if (!arguments.ContainsKey(name) || arguments[name] is null) return fallback;
        if (arguments[name] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < minimum || number > maximum)
            throw new QueryException("invalid_arguments", message);
        return number;
    }

    private static bool TryReal(string workspace, out string real)
    {
        real = "";
        try
        {
            var path = Path.IsPathRooted(workspace) ? workspace : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, workspace));
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            real = SourcePaths.Real(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SimpleName(string name)
    {
        var end = name.Length;
        while (end > 0)
        {
            var dot = name.LastIndexOf('.', end - 1);
            var segment = name[(dot + 1)..end];
            if (segment is not "<static>" and not "") return segment;
            if (dot < 0) break;
            end = dot;
        }
        return name;
    }

    private static string Qualified(string? containing, string name) =>
        string.IsNullOrEmpty(containing) ? name : containing + "." + name;

    private static SymbolRow From(MethodInfo method) => new(method.Id, "method", method.Name,
        Qualified(method.ContainingType, method.Name), method.Project, method.Location.File,
        method.Location.Line, method.Location.Column, method.Signature, true);

    private static SymbolRow From(DocumentedSymbol symbol) => new(symbol.Id, symbol.Kind, symbol.Name,
        Qualified(symbol.ContainingType, symbol.Name), symbol.Project, symbol.Location.File,
        symbol.Location.Line, symbol.Location.Column, symbol.Signature, symbol.Kind == "method");

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject ToolResult(JsonNode? id, JsonObject envelope, bool isError)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = envelope.ToJsonString() } };
        return Result(id, new JsonObject { ["content"] = content, ["isError"] = isError });
    }

    private static JsonObject RpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private sealed record SymbolQuery(string Workspace, string Name, string? Kind, string? Project, int Offset, int Limit, string? SnapshotId);

    private sealed record SymbolRow(string Id, string Kind, string Name, string QualifiedName, string Project, string Path,
        int Line, int Column, string Signature, bool HasSignature);
}
