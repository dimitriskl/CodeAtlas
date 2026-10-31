using System.Text.Json;
using CodeAtlas;
using CodeAtlas.Explanations;
using CodeAtlas.Cli;

var managedEntryUtc = DateTimeOffset.UtcNow;
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
const string usage = """
Usage: codeatlas                (no arguments, interactive terminal: numbered start menu)
       codeatlas index --solution <path.sln|path.slnx> [--reference-workers <1-8>]
       codeatlas refresh --solution <path.sln|path.slnx> [--reference-workers <1-8>]
       codeatlas --version
       codeatlas index-status --index <path-to-index.json> [--profile]
       codeatlas find-symbol (--index <path-to-index.json> | --solution <path>) --name <method-name> [--type <declaring-type>]
                  [--kind method|type|property|field|all]
       codeatlas find-references (--index <path-to-index.json> | --solution <path>) --symbol <id>
       codeatlas get-documentation (--index <path-to-index.json> | --solution <path>) --symbol <id> [--snapshot <indexId>] [--profile]
       codeatlas search-symbols (--index <path-to-index.json> | --solution <path>) --query <text> [--mode names|assisted]
                  [--kind method|type|property|field|all] [--language csharp]
       codeatlas get-facts --index <path-to-index.json> --symbol <id>
       codeatlas explain-request --index <path-to-index.json> --symbol <id> --output <request.json>
       codeatlas explain-import --index <path-to-index.json> --response <response.json>
       codeatlas get-explanation --index <path-to-index.json> (--symbol <id> | --request <requestId>)
       codeatlas configure-ai --provider <openai|azure|anthropic|kimi|ollama|openrouter> --model <name>
                  [--effort default|none|minimal|low|medium|high|xhigh|max] [--endpoint <full-url>] [--settings <appsettings.json>]
       codeatlas describe --index <path-to-index.json> [--symbol <id>] [--limit <positive-number>] [--project <path>]
                  [--provider <name>] [--model <name>] [--effort <level>] [--endpoint <full-url>] [--settings <appsettings.json>]
       codeatlas find-duplicates --index <path-to-index.json> [--mode bodies|purpose] [--symbol <id>]
       codeatlas find-callers --index <path-to-index.json> --symbol <id> [--depth <1-5>] [--limit <1-1000>]
       codeatlas coverage --index <path-to-index.json>
       codeatlas export-json --index <path-to-index.json> --output <new-directory>
       codeatlas live --solution <path.sln|path.slnx>
       codeatlas mcp --solution <path.sln|path.slnx>

Query options: --project <relative-project-path> --limit <1-1000> --offset <nonnegative> --snapshot <indexId> --profile
Default limit is 100. Indexed continuations require --snapshot; live queries support only the first page.
find-callers reports bounded traversal and does not support --offset or --snapshot.
All JSON commands accept --contract-version 1. Projects and declaring types match exactly (case-sensitive).
Warnings are saved in the solution-scoped generation. Results include a notice with the file path.
Indexing progress and elapsed time go to stderr; stdout contains the final JSON result.
Indexing artifacts commit together under .codeatlas/solutions/<solution-identity>/current.json.
Each generation stores its analysis in one SQLite database (index.db); queries read only the rows they need.
export-json writes the legacy index.json/catalog.json generation for comparison tools. Older JSON
generations remain readable. Without a usable SQLite library, commands fail with sqlite_unavailable.
Reference indexing uses 2 concurrent searches by default; use 1 for serial comparison.
Default/explicit method discovery returns MethodInfo. Other kinds/all return compact SymbolSummary records.
get-documentation returns full developer XML, resolved sections and provenance; missing documentation remains missing.
search-symbols ranks names, signatures, authored documentation, saved AI descriptions, and one-hop structural facts.
It does not call a model. get-facts returns Roslyn facts for one saved symbol. A resolved call is a relationship, not a runtime effect.
configure-ai tests the selected model without project source, then saves non-secret options to .codeatlas/appsettings.json.
Cloud keys are read from CODEATLAS_AI_<PROVIDER>_KEY (or older variables), then the OS keychain, then an owner-only file.
A missing key is requested without echo and saved to the keychain (or that file) only after the test passes; Ollama needs no key.
describe uses saved options or legacy CODEATLAS_AI_* settings. Selection overrides test and save the new connection first.
Without a configured selection or legacy credentials AI stays disabled. Effort support depends on provider and model.
explain-request exports selected source evidence locally; optional --local, --context, --reference-limit and byte limits bound that context.
explain-import requires registered request identity, evidence citations, provenance and unchanged observed inputs. No provider is called.
live reads one JSON object per stdin line and writes one JSON object per line. Request commands are edit, find-symbol, search and find-references.
mcp reads one JSON-RPC message per stdin line and writes one JSON-RPC message per line. Its tools are find_symbol, search_symbols, index_status, coverage and index.
""";
if (StartMenu.ShouldOpen(args, Console.IsInputRedirected, Console.IsOutputRedirected))
    return await StartMenu.RunInConsoleAsync();
if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(CodeAtlasVersion.Current);
    return 0;
}
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine(usage);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var indexingProgress = args.Length > 0 && (args[0] is "index" or "refresh") ? new IndexProgressReporter() : null;
var command = args.FirstOrDefault();
if (command == "live")
    return await LiveHost.RunAsync(args, Console.In, Console.Out, Console.Error, cancellation.Token);
if (command == "mcp")
    return await McpHost.RunAsync(args, Console.In, Console.Out, Console.Error, cancellation.Token);
CodebaseIndex? loadedIndex = null;
LoadedSnapshot? loadedSnapshot = null;
TimingSnapshot? committedTimings = null;
object? freshness = null;
var coverageAvailable = false;
QueryProfile? profile = null;
try
{
    if (args.Length < 3 || command is not ("index" or "refresh" or "index-status" or "find-symbol" or "find-references" or "get-documentation" or "search-symbols" or "get-facts" or "explain-request" or "explain-import" or "get-explanation" or "describe" or "configure-ai" or "find-duplicates" or "find-callers" or "coverage" or "export-json"))
        throw new ArgumentException(usage);
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--profile")
        {
            if (!options.TryAdd(args[i], "true"))
                throw new ArgumentException(usage);
            continue;
        }
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length
            || args[i + 1].StartsWith("--", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException(usage);
        i++;
    }
    if (options.Remove("--contract-version", out var requestedVersion) && requestedVersion != "1")
        throw new QueryException("unsupported_contract", "Supported JSON contract version: 1.");
    if (options.Remove("--profile"))
    {
        if (command is "index" or "refresh")
            throw new ArgumentException("--profile is for queries and index-status; indexing already saves stage timings.");
        profile = new QueryProfile(managedEntryUtc);
    }

    async Task<T> TimedAsync<T>(string phase, Func<Task<T>> action) =>
        profile is null ? await action() : await profile.MeasureAsync(phase, action);
    T Timed<T>(string phase, Func<T> action) => profile is null ? action() : profile.Measure(phase, action);
    int Integer(string key, int fallback) => !options.TryGetValue(key, out var raw) ? fallback
        : int.TryParse(raw, out var number) ? number : throw new ArgumentException($"{key} must be an integer.");
    QueryRequest Request() => new(options.GetValueOrDefault("--project"), options.GetValueOrDefault("--type"),
        Integer("--limit", 100), Integer("--offset", 0), options.GetValueOrDefault("--snapshot"));
    // Row-backed by default: a SQLite generation opens header-only and each query reads its rows.
    // "full" materializes the complete index for commands that need every record (explanations).
    async Task<CodebaseIndex> LoadFreshIndexAsync(string path, bool includeCatalog = true, bool full = false)
    {
        loadedSnapshot = await TimedAsync("jsonLoading", () => full
            ? IndexStore.LoadSnapshotAsync(path, includeCatalog, cancellation.Token)
            : IndexStore.OpenSnapshotAsync(path, includeCatalog, cancellation.Token));
        var index = loadedIndex = loadedSnapshot.Index;
        coverageAvailable = true;
        var status = await TimedAsync("freshnessHashing", () => IndexStore.CheckFreshnessAsync(index, cancellation.Token));
        freshness = ResponseContract.Freshness(status);
        if (status.IsStale) throw new StaleIndexException(status.ChangedFiles);
        return index;
    }

    if (command == "configure-ai")
    {
        if (options.Keys.Any(key => !AiPreferences.IsSelectionOption(key) && key != "--settings") || profile is not null)
            throw new ArgumentException("configure-ai accepts --provider, --model, --effort, --endpoint and --settings.");
        var settingsPath = Path.GetFullPath(options.GetValueOrDefault("--settings") ?? AiSettingsStore.DefaultPath);
        var selected = AiPreferences.Select(options, AiSettingsStore.Load(settingsPath), AiSetup.ReadEnvironment)
            ?? throw new ArgumentException("configure-ai requires --model and an optional --provider.");
        using var http = AiSetup.CreateHttpClient();
        var setup = await AiSetup.Interactive(http).ConfigureAsync(selected, settingsPath, cancellation.Token);
        var configured = setup.Configuration;
        var configurationOutput = JsonSerializer.SerializeToNode(new
        {
            ai = "enabled", connection = "tested", configured.Provider, configured.Model, configured.Effort,
            endpoint = configured.Endpoint.AbsoluteUri, settings = settingsPath,
            credentialEnvironmentVariable = configured.Provider == "ollama" ? null : selected.KeyVariable,
            credentialStore = setup.KeyLocation, credentialWarning = setup.KeyWarning,
            message = "Connection passed and options saved. No project source was sent."
        }, jsonOptions)!.AsObject();
        ResponseContract.AddEnvelope(configurationOutput, command, true, jsonOptions, null, false);
        Console.WriteLine(configurationOutput.ToJsonString(jsonOptions));
        return 0;
    }

    object result;
    if (command is "index" or "refresh")
    {
        if (!options.TryGetValue("--solution", out var path)
            || options.Keys.Any(key => key is not ("--solution" or "--reference-workers")))
            throw new ArgumentException(usage);
        var referenceWorkers = 2;
        if (options.TryGetValue("--reference-workers", out var workers)
            && (!int.TryParse(workers, out referenceWorkers) || referenceWorkers is < 1 or > 8))
            throw new ArgumentException("--reference-workers must be an integer between 1 and 8.");
        // Fail before minutes of analysis when the store cannot be written.
        SqliteAvailability.Require();
        var index = command == "refresh"
            ? await SolutionAnalysis.RefreshIndexAsync(path, cancellation.Token, indexingProgress, referenceWorkers)
            : await SolutionAnalysis.CreateIndexAsync(path, cancellation.Token, indexingProgress, referenceWorkers);
        indexingProgress!.Report(new(IndexStage.Saving));
        freshness = ResponseContract.Freshness(await IndexStore.CheckFreshnessAsync(index, cancellation.Token));
        loadedSnapshot = await IndexStore.PublishAsync(index, async (stream, token) =>
        {
            committedTimings = indexingProgress.SnapshotTimings();
            await TimingReport.WriteAsync(stream, index.Solution, index.IndexId, committedTimings, token);
        }, cancellation.Token);
        loadedIndex = index;
        coverageAvailable = true;
        result = new
        {
            index = IndexStore.DefaultPath(index.Solution),
            overview = loadedSnapshot.OverviewPath,
            snapshotIndex = loadedSnapshot.IndexPath,
            index.IndexId, index.CreatedAtUtc, index.ToolVersion, refresh = index.Refresh,
            projects = index.Projects.Length, types = index.Types.Length, methods = index.Methods.Length,
            warningCount = index.Warnings.Length, index.Warnings
        };
    }
    else if (command == "index-status")
    {
        if (options.Count != 1 || !options.TryGetValue("--index", out var path))
            throw new ArgumentException(usage);
        loadedSnapshot = await TimedAsync("jsonLoading", () => IndexStore.OpenSnapshotAsync(path, includeCatalog: true, cancellation.Token));
        var index = loadedIndex = loadedSnapshot.Index;
        // Status is the integrity command: it hashes the whole database against the manifest.
        await TimedAsync("storeVerification", async () => { await IndexStore.VerifyStoreAsync(loadedSnapshot, cancellation.Token); return true; });
        coverageAvailable = true;
        freshness = ResponseContract.Freshness(await TimedAsync("freshnessHashing", () => IndexStore.CheckFreshnessAsync(index, cancellation.Token)));
        result = new { index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
            refresh = index.Refresh,
            warningCount = index.Warnings.Length, index.Warnings };
    }
    else if (command == "export-json")
    {
        if (options.Count != 2 || !options.TryGetValue("--index", out var exportIndex) || !options.TryGetValue("--output", out var exportOutput))
            throw new ArgumentException("export-json requires --index <current.json> and --output <new-directory>.");
        var export = await TimedAsync("lookup", () => IndexStore.ExportJsonAsync(exportIndex, exportOutput, cancellation.Token));
        loadedSnapshot = export.Snapshot;
        var index = loadedIndex = export.Snapshot.Index;
        coverageAvailable = true;
        result = new { source = "index", index.Solution, index.IndexId, output = export.Directory,
            indexJson = export.IndexJson, catalogJson = export.CatalogJson, manifest = export.Manifest,
            message = "Legacy JSON generation written for comparison tools; queries keep using the SQLite store.",
            warningCount = index.Warnings.Length, index.Warnings };
    }
    else if (command == "coverage")
    {
        if (options.Count != 1 || !options.TryGetValue("--index", out var coverageIndex))
            throw new ArgumentException("coverage requires only --index.");
        var index = await LoadFreshIndexAsync(coverageIndex);
        result = new { source = "index", index.Solution, index.IndexId, supportedLanguages = LanguageBoundary.Supported,
            documentation = index.DocumentationCoverage, structural = SnapshotQueries.StructuralCoverage(loadedSnapshot!),
            warningCount = index.Warnings.Length, warnings = index.Warnings };
    }
    else if (command == "find-callers")
    {
        if (!options.TryGetValue("--index", out var callerIndex) || !options.TryGetValue("--symbol", out var callerSymbol)
            || options.Keys.Any(key => key is not ("--index" or "--symbol" or "--depth" or "--limit")))
            throw new ArgumentException("find-callers accepts --index, --symbol, --depth and --limit; --offset and --snapshot are not supported.");
        var depth = Integer("--depth", 1);
        var limit = Integer("--limit", 100);
        if (depth is < 1 or > 5) throw new ArgumentException("--depth must be from 1 to 5.");
        if (limit is < 1 or > 1000) throw new ArgumentException("--limit must be between 1 and 1000.");
        var index = await LoadFreshIndexAsync(callerIndex);
        var traversal = Timed("lookup", () => SnapshotQueries.FindCallers(loadedSnapshot!, callerSymbol, depth, limit, cancellation.Token));
        result = new { source = "index", index.Solution, index.IndexId, traversal, warningCount = index.Warnings.Length, warnings = index.Warnings };
    }
    else if (command == "find-duplicates")
    {
        if (!options.TryGetValue("--index", out var duplicateIndex)
            || options.Keys.Any(key => key is not ("--index" or "--mode" or "--symbol" or "--project" or "--limit" or "--offset" or "--snapshot")))
            throw new ArgumentException("find-duplicates accepts --index, --mode bodies|purpose, --symbol, --project and paging options.");
        var mode = options.GetValueOrDefault("--mode") ?? "bodies";
        var symbolId = options.GetValueOrDefault("--symbol");
        if (mode is not ("bodies" or "purpose") || mode == "purpose" && symbolId is null)
            throw new ArgumentException("--mode must be bodies or purpose; purpose requires --symbol.");
        var request = Request();
        request.ValidateOptions(supportsType: false, indexed: true);
        // Purpose overlap ranks over the whole catalog, so it loads the complete snapshot.
        var index = await LoadFreshIndexAsync(duplicateIndex, full: mode == "purpose");
        request.Validate(index.IndexId, supportsType: false);
        if (mode == "bodies")
        {
            var matches = Timed("lookup", () => SnapshotQueries.FindDuplicateGroups(loadedSnapshot!, request, symbolId));
            result = new { source = "index", index.Solution, index.IndexId, mode, symbol = symbolId,
                duplicates = matches.Page.Items, matches.Page.Pagination, matches.Work, duplicateCoverage = matches.Coverage,
                matches.DetectorVersion, matches.Limits, matches.OmittedMethodCount, matches.Selection, matches.Limitations,
                warningCount = index.Warnings.Length, warnings = index.Warnings };
        }
        else
        {
            SnapshotQueries.RequireMethod(loadedSnapshot!, symbolId!, "Duplicate candidates support indexed source methods only.");
            var duplicates = index.Duplicates ?? throw new QueryException("rebuild_required", "Rebuild to extract duplicate candidates.");
            var symbols = index.Symbols ?? throw new QueryException("rebuild_required", "Rebuild to extract symbol documentation.");
            var facts = index.StructuralCatalog ?? throw new QueryException("rebuild_required", "Rebuild to extract structural facts.");
            var matches = Timed("lookup", () => PurposeOverlap.Build(symbols, facts, duplicates, index.IndexId, symbolId!, request, cancellation.Token));
            result = new { source = "index", index.Solution, index.IndexId, mode, symbol = symbolId,
                candidates = matches.Page.Items, matches.Page.Pagination, matches.Query, matches.QueryTruncated,
                matches.SelectedDocumentationEvidence, matches.RankingVersion, matches.CandidatePoolLimit,
                matches.CandidatePoolTruncated, matches.StructuralWork, matches.BodyCoverage, matches.FactCoverage, matches.Limitations,
                warningCount = index.Warnings.Length, warnings = index.Warnings };
        }
    }
    else if (command == "describe")
    {
        if (!options.TryGetValue("--index", out var descriptionIndex)
            || options.Keys.Any(key => key is not ("--index" or "--symbol" or "--limit" or "--project" or "--settings") && !AiPreferences.IsSelectionOption(key)))
            throw new ArgumentException("describe requires --index; selection options include --symbol, --limit, --project, --provider, --model, --effort, --endpoint and --settings.");
        var limit = Integer("--limit", int.MaxValue);
        if (limit < 1) throw new ArgumentException("--limit must be a positive integer.");
        var settingsPath = Path.GetFullPath(options.GetValueOrDefault("--settings") ?? AiSettingsStore.DefaultPath);
        var preferences = AiPreferences.Select(options, AiSettingsStore.Load(settingsPath), AiSetup.ReadEnvironment);
        var configuration = preferences?.Configuration(AiSetup.ReadEnvironment, CredentialStore.ForCurrentUser());
        var index = await LoadFreshIndexAsync(descriptionIndex, full: true);
        var symbols = DescriptionBatch.Candidates(index, options.GetValueOrDefault("--project"), options.GetValueOrDefault("--symbol"));
        if (options.ContainsKey("--symbol") && symbols.Length == 0)
            throw new QueryException("symbol_not_found", "Select an indexed class/type or method ID.");
        if (options.Keys.Any(AiPreferences.IsSelectionOption))
        {
            using var probe = AiSetup.CreateHttpClient();
            configuration = (await AiSetup.Interactive(probe).ConfigureAsync(preferences!, settingsPath, cancellation.Token)).Configuration;
        }
        var batch = configuration is null ? new DescriptionBatchResult(0, 0, [], null)
            : await DescriptionBatch.RunAsync(loadedSnapshot!, symbols, configuration, limit, null, cancellation.Token);
        var (generated, cached, failed, failureFile) = batch;
        result = new { source = "index", index.Solution, index.IndexId,
            ai = configuration is null ? "disabled" : "enabled", generated, cached,
            remaining = symbols.Length - generated - cached, failed = failed.Count, descriptionWarningsFile = failureFile,
            message = configuration is null ? "AI disabled. Run configure-ai --provider <name> --model <name>, or configure legacy CODEATLAS_AI_API_KEY and CODEATLAS_AI_MODEL."
                : "Descriptions saved locally and searchable with search-symbols --mode assisted.",
            warningCount = index.Warnings.Length, warnings = index.Warnings };
    }
    else if (command is "explain-request" or "explain-import" or "get-explanation")
    {
        var allowed = command == "explain-import" ? new[] { "--index", "--response" }
            : command == "explain-request" ? new[] { "--index", "--symbol", "--output", "--local", "--context", "--reference-limit", "--max-files", "--max-context-bytes", "--max-request-bytes", "--max-response-bytes", "--max-source-bytes", "--max-estimated-tokens" }
            : new[] { "--index", "--symbol", "--request" };
        if (!options.TryGetValue("--index", out var explainIndex) || options.Keys.Any(key => !allowed.Contains(key))
            || (command == "explain-import" && !options.ContainsKey("--response"))
            || (command == "explain-request" && (!options.ContainsKey("--symbol") || !options.ContainsKey("--output")))
            || (command == "get-explanation" && options.ContainsKey("--symbol") == options.ContainsKey("--request")))
            throw new ArgumentException("Explanation commands require a saved index and the documented selection/output options.");
        var limits = new ExplanationLimits(Integer("--max-files", 6), Integer("--max-context-bytes", 65_536),
            Integer("--max-request-bytes", 131_072), Integer("--max-response-bytes", 65_536),
            Integer("--max-source-bytes", 1_048_576), Integer("--max-estimated-tokens", 32_768));
        limits.Validate();
        var referenceLimit = Integer("--reference-limit", 2);
        if (referenceLimit is < 0 or > 8) throw new ArgumentException("--reference-limit must be from 0 to 8.");
        if (options.TryGetValue("--request", out var identity) && !Guid.TryParseExact(identity, "N", out _))
            throw new ArgumentException("--request must be the requestId returned by explain-request.");
        var index = await LoadFreshIndexAsync(explainIndex, full: true);
        var service = ExplanationSnapshotAdapter.ForSnapshot(loadedSnapshot!);
        if (command == "explain-import")
        {
            var accepted = await TimedAsync("lookup", () => service.AcceptAsync(options["--response"], cancellation.Token));
            result = new { source = "index", index.Solution, index.IndexId, explanation = accepted,
                warningCount = index.Warnings.Length, warnings = index.Warnings };
        }
        else if (command == "explain-request")
        {
            var request = await TimedAsync("lookup", () => service.ExportAsync(options["--symbol"], options["--output"],
                limits, options.TryGetValue("--context", out var context) ? [context] : null,
                options.GetValueOrDefault("--local"), referenceLimit, cancellation.Token));
            result = new { source = "index", index.Solution, index.IndexId, request,
                output = Path.GetFullPath(options["--output"]), warningCount = index.Warnings.Length, warnings = index.Warnings };
        }
        else
        {
            var view = await TimedAsync("lookup", () => options.TryGetValue("--request", out var requestId)
                ? service.GetAsync(requestId, cancellation.Token)
                : service.GetForSymbolAsync(options["--symbol"], cancellation.Token));
            result = new { source = "index", index.Solution, index.IndexId, explanation = view,
                warningCount = index.Warnings.Length, warnings = index.Warnings };
        }
    }
    else if (command is "search-symbols" or "get-facts")
    {
        if (command == "get-facts" && (options.ContainsKey("--solution") || !options.ContainsKey("--index") || !options.ContainsKey("--symbol")))
            throw new ArgumentException("get-facts requires --index and --symbol. Live fact queries are not supported.");
        if (command == "search-symbols" && (!options.ContainsKey("--query")
            || options.ContainsKey("--solution") == options.ContainsKey("--index")))
            throw new ArgumentException(usage);
        var mode = options.GetValueOrDefault("--mode") ?? "assisted";
        var kind = options.GetValueOrDefault("--kind") ?? "method";
        var language = options.GetValueOrDefault("--language") ?? "csharp";
        var request = Request();
        request.ValidateOptions(supportsType: command == "search-symbols", indexed: options.ContainsKey("--index"));
        if (options.TryGetValue("--index", out var factIndex))
        {
            var index = await LoadFreshIndexAsync(factIndex);
            if (command == "get-facts")
            {
                var page = await TimedAsync("lookup", () => PurposeSnapshotQueries.GetFactsAsync(
                    loadedSnapshot!, options["--symbol"], request, cancellation.Token));
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbol = options["--symbol"], facts = page.Items, page.Pagination,
                    warningCount = index.Warnings.Length, warnings = index.Warnings };
            }
            else
            {
                var search = await TimedAsync("lookup", () => PurposeSnapshotQueries.SearchAsync(
                    loadedSnapshot!, options["--query"], mode, kind, request, language, cancellation.Token));
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbols = search.Page.Items, pagination = search.Page.Pagination, search.RankingVersion, search.Mode,
                    search.Language, search.QueryTokens, search.UnmatchedQueryTokens, search.EvidencePolicy, search.Decision,
                    search.WithheldWeakMatches, search.Limitations, searchCoverage = search.Coverage,
                    warningCount = index.Warnings.Length, warnings = index.Warnings };
            }
        }
        else
        {
            using var analysis = await TimedAsync("liveLoading", () => SolutionAnalysis.LoadAsync(options["--solution"], cancellation.Token));
            coverageAvailable = true;
            freshness = ResponseContract.LiveFreshness;
            var catalog = await TimedAsync("liveLookup", () => analysis.GetDocumentedSymbolsAsync(cancellation.Token));
            var facts = await TimedAsync("liveLookup", () => analysis.GetStructuralCatalogAsync(catalog, cancellation.Token));
            var search = Timed("lookup", () => PurposeSearch.Search(catalog, facts, options["--query"], mode, kind, request, snapshotId: null, language));
            result = new { source = "live", solution = analysis.SolutionPath, symbols = search.Page.Items,
                pagination = search.Page.Pagination, search.RankingVersion, search.Mode, search.Language, search.QueryTokens,
                search.UnmatchedQueryTokens, search.EvidencePolicy, search.Decision, search.WithheldWeakMatches,
                search.Limitations, searchCoverage = search.Coverage,
                warningCount = analysis.Warnings.Count, warnings = analysis.Warnings };
        }
    }
    else
    {
        var selector = command == "find-symbol" ? "--name" : "--symbol";
        if (!options.TryGetValue(selector, out var value)
            || options.ContainsKey("--solution") == options.ContainsKey("--index")
            || options.Keys.Any(key => key != selector && key is not ("--solution" or "--index" or "--project" or "--type" or "--limit" or "--offset" or "--snapshot" or "--kind")))
            throw new ArgumentException(usage);
        var singleton = command == "get-documentation";
        if (singleton && options.Keys.Any(key => key is not ("--solution" or "--index" or "--symbol" or "--snapshot")))
            throw new ArgumentException("get-documentation accepts only --symbol, --index/--solution, --snapshot, --profile and --contract-version.");
        if (command != "find-symbol" && options.ContainsKey("--kind"))
            throw new ArgumentException("--kind is supported only by find-symbol.");
        var kind = options.GetValueOrDefault("--kind") ?? "method";
        DocumentationQueries.ValidateKind(kind);
        var request = Request();
        // Validate request bounds before any expensive IO. Identity is checked after loading and freshness verification.
        request.ValidateOptions(supportsType: command == "find-symbol" || singleton, indexed: options.ContainsKey("--index"));
        if (options.TryGetValue("--index", out var indexPath))
        {
            // Documentation IDs carry their declaration kind after the project separator.
            // Known method lookups keep their catalog-free fast path and resolve the generation once.
            var declaration = value.LastIndexOf("::", StringComparison.Ordinal) + 2;
            var nonMethodReference = command == "find-references" && declaration >= 2
                && declaration + 1 < value.Length && value[declaration + 1] == ':'
                && value[declaration] is 'T' or 'P' or 'F' or 'E' or 'N';
            var index = await LoadFreshIndexAsync(indexPath, singleton || kind != "method" || nonMethodReference);
            request.Validate(index.IndexId, command == "find-symbol" || singleton);
            if (singleton)
            {
                var symbol = Timed("lookup", () => SnapshotQueries.GetDocumentation(loadedSnapshot!, value));
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbol, warningCount = index.Warnings.Length, index.Warnings };
            }
            else if (command == "find-symbol" && kind != "method")
            {
                var page = Timed("lookup", () => SnapshotQueries.FindSymbols(loadedSnapshot!, value, kind, request));
                var summaries = Timed("lookup", () => page.Items.Select(SymbolSummary.From).ToArray());
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbols = summaries, page.Pagination, warningCount = index.Warnings.Length, index.Warnings };
            }
            else if (command == "find-symbol")
            {
                var page = Timed("lookup", () => SnapshotQueries.FindMethods(loadedSnapshot!, value, request));
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbols = page.Items, page.Pagination, warningCount = index.Warnings.Length, index.Warnings };
            }
            else
            {
                var page = Timed("lookup", () => SnapshotQueries.FindReferences(loadedSnapshot!, value, request));
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbol = value, references = page.Items, page.Pagination, warningCount = index.Warnings.Length, index.Warnings };
            }
        }
        else
        {
            using var analysis = await TimedAsync("liveLoading", () => SolutionAnalysis.LoadAsync(options["--solution"], cancellation.Token));
            coverageAvailable = true;
            freshness = ResponseContract.LiveFreshness;
            if (singleton || command == "find-symbol" && kind != "method")
            {
                var catalog = await TimedAsync("liveLookup", () => analysis.GetDocumentedSymbolsAsync(cancellation.Token));
                if (singleton)
                {
                    var symbol = Timed("lookup", () => DocumentationQueries.GetDocumentation(catalog, value));
                    result = new { source = "live", solution = analysis.SolutionPath, symbol,
                        warningCount = analysis.Warnings.Count, warnings = analysis.Warnings };
                }
                else
                {
                    var page = Timed("lookup", () => DocumentationQueries.FindSymbols(catalog, value, kind, request, snapshotId: null));
                    var summaries = Timed("lookup", () => page.Items.Select(SymbolSummary.From).ToArray());
                    result = new { source = "live", solution = analysis.SolutionPath, symbols = summaries,
                        page.Pagination, warningCount = analysis.Warnings.Count, warnings = analysis.Warnings };
                }
            }
            else if (command == "find-symbol")
            {
                var methods = await TimedAsync("liveLookup", () => analysis.FindMethodsAsync(value, cancellation.Token));
                var page = Timed("lookup", () => QueryPaging.Methods(methods, request, snapshotId: null));
                result = new { source = "live", solution = analysis.SolutionPath, symbols = page.Items,
                    page.Pagination, warningCount = analysis.Warnings.Count, warnings = analysis.Warnings };
            }
            else
            {
                var references = await TimedAsync("liveLookup", () => analysis.FindReferencesAsync(value, cancellation.Token));
                var page = Timed("lookup", () => QueryPaging.References(references, request, snapshotId: null));
                result = new { source = "live", solution = analysis.SolutionPath, symbol = value, references = page.Items,
                    page.Pagination, warningCount = analysis.Warnings.Count, warnings = analysis.Warnings };
            }
        }
    }
    var output = Timed("resultNodeCreation", () => JsonSerializer.SerializeToNode(result, jsonOptions)!.AsObject());
    var warnings = output["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToArray();
    var solutionPath = options.TryGetValue("--solution", out var selectedSolution)
        ? selectedSolution : output["solution"]!.GetValue<string>();
    var warningsPath = loadedSnapshot is null
        ? await TimedAsync("warningSaving", () => WarningReport.SaveAsync(solutionPath, null, warnings, cancellation.Token))
        : loadedSnapshot.WarningsPath;
    output["snapshotIntegrity"] = loadedSnapshot is null ? "live-analysis" : loadedSnapshot.VerifiedGeneration ? "verified-generation" : "legacy-unverified";
    output["warningsFile"] = warningsPath;
    output["coverageIssueCount"] = loadedIndex?.InputManifest?.Coverage.Issues.Length;
    if (loadedIndex?.InputManifest?.Coverage is { Status: "partial" } coverage)
        output["coverageMessage"] = warningsPath is null ? "Input coverage is partial; rebuild for the coverage report."
            : $"Input coverage is partial; {coverage.Issues.Length} issues recorded in {warningsPath}";
    output.Remove("warnings");
    output["warningMessage"] = warningsPath is null
        ? $"{warnings.Length} warnings recorded in legacy index; rebuild to create a generation warning report."
        : $"{warnings.Length} warnings recorded in {warningsPath}";
    ResponseContract.AddEnvelope(output, command, true, jsonOptions, freshness, coverageAvailable,
        loadedIndex is null || loadedIndex.SchemaVersion >= 2);
    if (indexingProgress is not null)
    {
        var timings = committedTimings!;
        output["timings"] = loadedSnapshot!.TimingsPath;
        output["slowestStage"] = JsonSerializer.SerializeToNode(timings.Stages.MaxBy(s => s.ElapsedSeconds), jsonOptions);
        indexingProgress.Complete();
        output["elapsedSeconds"] = indexingProgress.ElapsedSeconds;
    }
    if (profile is not null)
        output["queryTimings"] = JsonSerializer.SerializeToNode(profile.Snapshot(), jsonOptions);
    Console.WriteLine(output.ToJsonString(jsonOptions));
    return 0;
}
catch (Exception exception)
{
    indexingProgress?.Dispose();
    var (code, exit) = ResponseContract.Classify(exception);
    var output = JsonSerializer.SerializeToNode(new
    {
        error = new { code, message = exception.Message, diagnostics = (exception as AnalysisException)?.Diagnostics ?? [] },
        warningCount = loadedIndex?.Warnings.Length ?? 0,
        indexId = loadedIndex?.IndexId,
        warningsFile = loadedSnapshot?.WarningsPath,
        warningMessage = loadedSnapshot?.WarningsPath is { } report ? $"{loadedIndex!.Warnings.Length} warnings recorded in {report}" : null
    }, jsonOptions)!.AsObject();
    ResponseContract.AddEnvelope(output, command, false, jsonOptions, freshness, coverageAvailable,
        loadedIndex is null || loadedIndex.SchemaVersion >= 2);
    if (profile is not null)
        output["queryTimings"] = JsonSerializer.SerializeToNode(profile.Snapshot(), jsonOptions);
    Console.Error.WriteLine(output.ToJsonString(jsonOptions));
    return exit;
}
