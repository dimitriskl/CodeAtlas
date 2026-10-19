using System.Text.Json;
using CodeAtlas;
using CodeAtlas.Cli;

var managedEntryUtc = DateTimeOffset.UtcNow;
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
const string usage = """
Usage: codeatlas index --solution <path.sln|path.slnx> [--reference-workers <1-8>]
       codeatlas --version
       codeatlas index-status --index <path-to-index.json> [--profile]
       codeatlas find-symbol (--index <path-to-index.json> | --solution <path>) --name <method-name> [--type <declaring-type>]
       codeatlas find-references (--index <path-to-index.json> | --solution <path>) --symbol <id>

Query options: --project <relative-project-path> --limit <1-1000> --offset <nonnegative> --snapshot <indexId> --profile
Default limit is 100. Indexed continuations require --snapshot; live queries support only the first page.
All JSON commands accept --contract-version 1. Projects and declaring types match exactly (case-sensitive).
Warnings are saved to .codeatlas/warnings.txt beside the solution. Results include a notice with the file path.
Indexing progress and elapsed time go to stderr; stdout contains the final JSON result.
Indexing stage timings are saved to .codeatlas/timings.json beside the solution.
Reference indexing uses 2 concurrent searches by default; use 1 for serial comparison.
""";
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
using var indexingProgress = args.Length > 0 && args[0] == "index" ? new IndexProgressReporter() : null;
var command = args.FirstOrDefault();
CodebaseIndex? loadedIndex = null;
object? freshness = null;
var coverageAvailable = false;
QueryProfile? profile = null;
try
{
    if (args.Length < 3 || command is not ("index" or "index-status" or "find-symbol" or "find-references"))
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
        if (command == "index")
            throw new ArgumentException("--profile is for queries and index-status; indexing already saves stage timings.");
        profile = new QueryProfile(managedEntryUtc);
    }

    async Task<T> TimedAsync<T>(string phase, Func<Task<T>> action) =>
        profile is null ? await action() : await profile.MeasureAsync(phase, action);
    T Timed<T>(string phase, Func<T> action) => profile is null ? action() : profile.Measure(phase, action);

    object result;
    if (command == "index")
    {
        if (!options.TryGetValue("--solution", out var path)
            || options.Keys.Any(key => key is not ("--solution" or "--reference-workers")))
            throw new ArgumentException(usage);
        var referenceWorkers = 2;
        if (options.TryGetValue("--reference-workers", out var workers)
            && (!int.TryParse(workers, out referenceWorkers) || referenceWorkers is < 1 or > 8))
            throw new ArgumentException("--reference-workers must be an integer between 1 and 8.");
        var index = await SolutionAnalysis.CreateIndexAsync(path, cancellation.Token, indexingProgress, referenceWorkers);
        indexingProgress!.Report(new(IndexStage.Saving));
        await IndexStore.SaveAsync(index, cancellation.Token);
        loadedIndex = index;
        coverageAvailable = true;
        freshness = ResponseContract.Freshness(new IndexFreshness(false, []));
        result = new
        {
            index = IndexStore.DefaultPath(index.Solution),
            overview = Path.Combine(Path.GetDirectoryName(IndexStore.DefaultPath(index.Solution))!, "overview.md"),
            index.IndexId, index.CreatedAtUtc, index.ToolVersion,
            projects = index.Projects.Length, types = index.Types.Length, methods = index.Methods.Length,
            warningCount = index.Warnings.Length, index.Warnings
        };
    }
    else if (command == "index-status")
    {
        if (options.Count != 1 || !options.TryGetValue("--index", out var path))
            throw new ArgumentException(usage);
        var index = loadedIndex = await TimedAsync("jsonLoading", () => IndexStore.LoadAsync(path, cancellation.Token));
        coverageAvailable = true;
        freshness = ResponseContract.Freshness(await TimedAsync("freshnessHashing", () => IndexStore.CheckFreshnessAsync(index, cancellation.Token)));
        result = new { index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
            warningCount = index.Warnings.Length, index.Warnings };
    }
    else
    {
        var selector = command == "find-symbol" ? "--name" : "--symbol";
        if (!options.TryGetValue(selector, out var value)
            || options.ContainsKey("--solution") == options.ContainsKey("--index")
            || options.Keys.Any(key => key != selector && key is not ("--solution" or "--index" or "--project" or "--type" or "--limit" or "--offset" or "--snapshot")))
            throw new ArgumentException(usage);
        static int Integer(Dictionary<string, string> values, string key, int fallback) =>
            !values.TryGetValue(key, out var raw) ? fallback : int.TryParse(raw, out var number)
                ? number : throw new ArgumentException($"{key} must be an integer.");
        var request = new QueryRequest(options.GetValueOrDefault("--project"), options.GetValueOrDefault("--type"),
            Integer(options, "--limit", 100), Integer(options, "--offset", 0), options.GetValueOrDefault("--snapshot"));
        // Validate request bounds before any expensive IO. Identity is checked after loading and freshness verification.
        request.ValidateOptions(supportsType: command == "find-symbol", indexed: options.ContainsKey("--index"));
        if (options.TryGetValue("--index", out var indexPath))
        {
            var index = loadedIndex = await TimedAsync("jsonLoading", () => IndexStore.LoadAsync(indexPath, cancellation.Token));
            coverageAvailable = true;
            var status = await TimedAsync("freshnessHashing", () => IndexStore.CheckFreshnessAsync(index, cancellation.Token));
            freshness = ResponseContract.Freshness(status);
            if (status.IsStale)
                throw new StaleIndexException(status.ChangedFiles);
            request.Validate(index.IndexId, command == "find-symbol");
            if (command == "find-symbol")
            {
                var page = Timed("lookup", () => QueryPaging.Methods(IndexStore.FindMethods(index, value), request, index.IndexId));
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbols = page.Items, page.Pagination, warningCount = index.Warnings.Length, index.Warnings };
            }
            else
            {
                var page = Timed("lookup", () => QueryPaging.References(IndexStore.FindReferences(index, value), request, index.IndexId));
                result = new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion,
                    symbol = value, references = page.Items, page.Pagination, warningCount = index.Warnings.Length, index.Warnings };
            }
        }
        else
        {
            using var analysis = await TimedAsync("liveLoading", () => SolutionAnalysis.LoadAsync(options["--solution"], cancellation.Token));
            coverageAvailable = true;
            freshness = ResponseContract.LiveFreshness;
            if (command == "find-symbol")
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
    var warningsPath = await TimedAsync("warningSaving", () => WarningReport.SaveAsync(solutionPath, output["indexId"]?.GetValue<string>(), warnings, cancellation.Token));
    output.Remove("warnings");
    if (warnings.Length > 0)
        output["warningMessage"] = $"{warnings.Length} warnings saved to {warningsPath}";
    ResponseContract.AddEnvelope(output, command, true, jsonOptions, freshness, coverageAvailable);
    if (indexingProgress is not null)
    {
        var timings = indexingProgress.SnapshotTimings();
        output["timings"] = await TimingReport.SaveAsync(solutionPath, output["indexId"]!.GetValue<string>(), timings, cancellation.Token);
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
        indexId = loadedIndex?.IndexId
    }, jsonOptions)!.AsObject();
    ResponseContract.AddEnvelope(output, command, false, jsonOptions, freshness, coverageAvailable);
    if (profile is not null)
        output["queryTimings"] = JsonSerializer.SerializeToNode(profile.Snapshot(), jsonOptions);
    Console.Error.WriteLine(output.ToJsonString(jsonOptions));
    return exit;
}
