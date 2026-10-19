using System.Text.Json;
using CodeAtlas;
using CodeAtlas.Cli;

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
const string usage = """
Usage: codeatlas index --solution <path.sln|path.slnx> [--reference-workers <1-8>]
       codeatlas --version
       codeatlas index-status --index <path-to-index.json>
       codeatlas find-symbol (--index <path-to-index.json> | --solution <path>) --name <method-name>
       codeatlas find-references (--index <path-to-index.json> | --solution <path>) --symbol <id>

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
try
{
    if (args.Length < 3 || args[0] is not ("index" or "index-status" or "find-symbol" or "find-references"))
        throw new ArgumentException(usage);
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException(usage);
        i++;
    }
    object result;
    if (args[0] == "index")
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
        result = new
        {
            index = IndexStore.DefaultPath(index.Solution),
            overview = Path.Combine(Path.GetDirectoryName(IndexStore.DefaultPath(index.Solution))!, "overview.md"),
            index.IndexId, index.CreatedAtUtc, index.ToolVersion,
            projects = index.Projects.Length, types = index.Types.Length, methods = index.Methods.Length,
            warningCount = index.Warnings.Length, index.Warnings
        };
    }
    else if (args[0] == "index-status")
    {
        if (options.Count != 1 || !options.TryGetValue("--index", out var path))
            throw new ArgumentException(usage);
        var index = await IndexStore.LoadAsync(path, cancellation.Token);
        result = new { index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion, freshness = await IndexStore.CheckFreshnessAsync(index, cancellation.Token), warningCount = index.Warnings.Length, index.Warnings };
    }
    else
    {
        var selector = args[0] == "find-symbol" ? "--name" : "--symbol";
        if (options.Count != 2 || !options.TryGetValue(selector, out var value)
            || options.ContainsKey("--solution") == options.ContainsKey("--index"))
            throw new ArgumentException(usage);
        if (options.TryGetValue("--index", out var indexPath))
        {
            var index = await IndexStore.LoadAsync(indexPath, cancellation.Token);
            await IndexStore.RequireFreshAsync(index, cancellation.Token);
            result = args[0] == "find-symbol"
                ? new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion, symbols = IndexStore.FindMethods(index, value), warningCount = index.Warnings.Length, index.Warnings }
                : new { source = "index", index.Solution, index.IndexId, index.CreatedAtUtc, index.ToolVersion, symbol = value, references = IndexStore.FindReferences(index, value), warningCount = index.Warnings.Length, index.Warnings };
        }
        else
        {
            using var analysis = await SolutionAnalysis.LoadAsync(options["--solution"], cancellation.Token);
            result = args[0] == "find-symbol"
                ? new { source = "live", solution = analysis.SolutionPath, symbols = await analysis.FindMethodsAsync(value, cancellation.Token), warningCount = analysis.Warnings.Count, warnings = analysis.Warnings }
                : new { source = "live", solution = analysis.SolutionPath, symbol = value, references = await analysis.FindReferencesAsync(value, cancellation.Token), warningCount = analysis.Warnings.Count, warnings = analysis.Warnings };
        }
    }
    var output = JsonSerializer.SerializeToNode(result, jsonOptions)!.AsObject();
    var warnings = output["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToArray();
    var solutionPath = options.TryGetValue("--solution", out var selectedSolution)
        ? selectedSolution : output["solution"]!.GetValue<string>();
    var warningsPath = await WarningReport.SaveAsync(solutionPath, output["indexId"]?.GetValue<string>(), warnings, cancellation.Token);
    output.Remove("warnings");
    if (warnings.Length > 0)
        output["warningMessage"] = $"{warnings.Length} warnings saved to {warningsPath}";
    if (indexingProgress is not null)
    {
        var timings = indexingProgress.SnapshotTimings();
        output["timings"] = await TimingReport.SaveAsync(solutionPath, output["indexId"]!.GetValue<string>(),
            timings, cancellation.Token);
        output["slowestStage"] = JsonSerializer.SerializeToNode(timings.Stages.MaxBy(s => s.ElapsedSeconds), jsonOptions);
        indexingProgress.Complete();
        output["elapsedSeconds"] = indexingProgress.ElapsedSeconds;
    }
    Console.WriteLine(output.ToJsonString(jsonOptions));
    return 0;
}
catch (Exception exception)
{
    indexingProgress?.Dispose();
    var code = exception switch
    {
        OperationCanceledException => "cancelled",
        StaleIndexException => "stale_index",
        ArgumentException => "invalid_arguments",
        _ => "analysis_failed"
    };
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        error = new { code, message = exception.Message, diagnostics = (exception as AnalysisException)?.Diagnostics ?? [] }
    }, jsonOptions));
    return exception is OperationCanceledException ? 130 : exception is StaleIndexException ? 3 : exception is ArgumentException ? 2 : 1;
}
