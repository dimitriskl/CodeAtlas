using System.Security.Cryptography;
using System.Text.Json;
using CodeAtlas;

namespace CodeAtlas.Qualification;

// Manual future harness; not a Fact, not an executed measurement, not a shipped CLI.
// Root must first freeze/accept limits and metrics; this checks a reviewable local acceptance file.
public static class PublicQualification
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task ExportAsync(string publicDirectory, string acceptanceFile, string newOutputFile)
    {
        publicDirectory = Path.GetFullPath(publicDirectory);
        using var freeze = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(publicDirectory, "FREEZE.json")));
        foreach (var name in new[] { "Corpus.cs", "labels.json", "PROTOCOL.md" })
        {
            var expected = freeze.RootElement.GetProperty("files").GetProperty(name).GetString();
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(publicDirectory, name))));
            if (actual != expected) throw new InvalidOperationException("Frozen fixture changed: " + name);
        }
        byte[] acceptedBytes = await File.ReadAllBytesAsync(acceptanceFile);
        using var acceptance = JsonDocument.Parse(acceptedBytes);
        var a = acceptance.RootElement;
        string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var executableHash = HashFile(typeof(DuplicateDetector).Assembly.Location);
        var harnessHash = HashFile(typeof(PublicQualification).Assembly.Location);
        if (!a.GetProperty("accepted").GetBoolean() || string.IsNullOrWhiteSpace(a.GetProperty("acceptedBy").GetString())
            || !a.GetProperty("acceptedAtUtc").TryGetDateTimeOffset(out _)
            || a.GetProperty("detectorVersion").GetString() != DuplicateDetector.Version
            || string.IsNullOrWhiteSpace(a.GetProperty("detectorRevision").GetString())
            || a.GetProperty("executableSha256").GetString() != executableHash
            || a.GetProperty("harnessSha256").GetString() != harnessHash
            || a.GetProperty("metrics").GetArrayLength() == 0
            || a.GetProperty("protocol").GetString() != freeze.RootElement.GetProperty("protocol").GetString())
            throw new InvalidOperationException("Root/user must accept this detector/configuration and metrics before the run.");
        var limits = a.GetProperty("limits").Deserialize<DuplicateLimits>(Json)
            ?? throw new InvalidOperationException("Accepted limits are missing.");
        limits.Validate();
        // Public fixture uses AdhocWorkspace and trusted platform references; it loads no application solution.
        using var fixture = PublicFixture.Create(await File.ReadAllTextAsync(Path.Combine(publicDirectory, "Corpus.cs")));
        var catalog = await fixture.ExtractAsync(limits);
        var trace = new List<DuplicatePairTrace>();
        var runId = "public-fixture-" + Guid.NewGuid().ToString("N");
        var scan = DuplicateDetector.Scan(catalog, runId, trace: trace.Add);
        await using var output = new FileStream(newOutputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, new
        {
            protocol = a.GetProperty("protocol").GetString(), runKind = "synthetic-in-memory-development-not-production-snapshot",
            fixtureRunId = runId, detectorVersion = DuplicateDetector.Version,
            detectorRevision = a.GetProperty("detectorRevision").GetString(), executableSha256 = executableHash,
            harnessSha256 = harnessHash,
            toolVersion = typeof(SolutionAnalysis).Assembly.GetName().Version?.ToString(),
            qualification = "unreviewed-author-labeled-development-output", createdAtUtc = DateTimeOffset.UtcNow,
            acceptanceSha256 = Convert.ToHexString(SHA256.HashData(acceptedBytes)),
            duplicateCatalog = catalog, duplicateScan = scan, trace
        }, Json);
    }
}

