using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAtlas;
using CodeAtlas.Persistence;

// This executable is deliberately outside the production solution and CLI package.
// Each invocation reloads both discovery and semantic workspaces. Application files
// are never written. A virtual generation is committed only to an isolated sink.
var clock = Stopwatch.StartNew();
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var position = 0; position < args.Length; position += 2)
{
    if (position + 1 >= args.Length || !args[position].StartsWith("--", StringComparison.Ordinal)
        || !options.TryAdd(args[position], args[position + 1]))
        throw new ArgumentException("Expected --solution PATH --mode seed|full|incremental --output DIR [--baseline index.json --edit edit.json]");
}
var path = Path.GetFullPath(options["--solution"]);
var mode = options["--mode"];
if (mode is not ("seed" or "full" or "incremental")) throw new ArgumentException("Unknown benchmark mode.");
var output = Path.GetFullPath(options["--output"]);
var requiredRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.codeatlas"));
if (!output.StartsWith(requiredRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Benchmark output must stay inside this checkout's .codeatlas directory.");
Directory.CreateDirectory(output);
var baseline = mode == "incremental" ? await IndexStore.LoadAsync(options["--baseline"]) : null;
var overlay = mode == "seed" ? null : JsonSerializer.Deserialize<BenchmarkOverlay>(await File.ReadAllTextAsync(options["--edit"]), json)
    ?? throw new ArgumentException("Missing benchmark overlay.");
var progress = new BenchmarkProgress(clock);
var index = await SolutionAnalysis.CreateBenchmarkIndexAsync(path, baseline, overlay, mode == "incremental", progress);
progress.Report(new(IndexStage.Saving));
// Match CLI response freshness and IndexStore.PublishAsync's early guard as well
// as GenerationStore's final precommit guard below.
_ = await IndexStore.CheckFreshnessAsync(index);
await IndexStore.RequireFreshAsync(index);
var syntheticSolution = Path.Combine(output, "virtual-benchmark.slnx");
// Exercise replacement, matching a refresh with an existing pointer. This is not
// a target manifest and can never be passed off as a real generation of the target.
var pointer = GenerationStore.DefaultPath(syntheticSolution);
Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
await File.WriteAllTextAsync(pointer, "{}", Encoding.UTF8);
var saved = await new GenerationStore().PublishAsync(syntheticSolution, index.IndexId,
[
    new("index.json", (stream, token) => JsonSerializer.SerializeAsync(stream, index, json, token)),
    Text("overview.md", "# VIRTUAL BENCHMARK ARTIFACT — NOT THE ACTIVE TARGET SNAPSHOT\n\n"
        + "Locations describe a controlled in-memory overlay when virtualSnapshot is present.\n\n" + IndexStore.Overview(index)),
    Text("warnings.txt", IndexStore.WarningText(index)),
    new("timings.json", (stream, token) => JsonSerializer.SerializeAsync(stream,
        new { virtualBenchmark = true, index.IndexId, elapsedSeconds = clock.Elapsed.TotalSeconds, stages = progress.Snapshot() }, json, token))
], token => IndexStore.RequireFreshAsync(index, token));
var elapsed = clock.Elapsed.TotalSeconds;
var hashFiles = new[] { typeof(SolutionAnalysis).Assembly.Location, typeof(BenchmarkProgress).Assembly.Location };
var hashes = new Dictionary<string, string>();
foreach (var file in hashFiles)
    hashes.Add(Path.GetFileName(file), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))));
var report = new
{
    mode, virtualBenchmark = overlay is not null, solution = path, index.IndexId,
    baselineIndexId = baseline?.IndexId, index.Refresh, index.VirtualSnapshot,
    snapshotIndex = saved.ArtifactPath("index.json"), isolatedManifest = saved.ManifestPath,
    elapsedSeconds = elapsed, stages = progress.Snapshot(), binarySha256 = hashes,
    unsupportedReasons = index.IncrementalState!.UnsupportedReasons,
    inputManifestHash = index.IncrementalState.InputManifestHash,
    memoryScope = "Main benchmark process only; MSBuild children excluded. Use external OS peak-counter sampling.",
    publicationScope = "Complete isolated same-volume generation staging, artifact hashing, disk flushes, current-input guard, and atomic pointer replacement included. Real target directory publication and contention are not measured. Virtual target is never published."
};
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, json));
Console.WriteLine(JsonSerializer.Serialize(report, json));

GenerationArtifact Text(string name, string value) => new(name,
    (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(value), token).AsTask());

sealed class BenchmarkProgress(Stopwatch clock) : IProgress<IndexProgress>
{
    private readonly object gate = new();
    private readonly Dictionary<string, double> durations = new(StringComparer.Ordinal);
    private IndexStage? current;
    private double started;

    public void Report(IndexProgress value)
    {
        lock (gate)
        {
            if (current == value.Stage) return;
            Accumulate();
            current = value.Stage;
            Console.Error.WriteLine($"{clock.Elapsed.TotalSeconds:F3}s {value.Stage} {value.Detail}");
        }
    }

    private void Accumulate()
    {
        var now = clock.Elapsed.TotalSeconds;
        if (current is { } previous)
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(previous.ToString());
            durations[name] = durations.GetValueOrDefault(name) + now - started;
        }
        started = now;
    }

    public Dictionary<string, double> Snapshot()
    {
        lock (gate)
        {
            Accumulate();
            return new(durations, StringComparer.Ordinal);
        }
    }
}
