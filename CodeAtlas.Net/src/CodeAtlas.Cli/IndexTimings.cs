using System.Text.Json;

namespace CodeAtlas.Cli;

internal sealed record StageTiming(string Stage, double ElapsedSeconds);
internal sealed record TimingSnapshot(double ElapsedSeconds, StageTiming[] Stages);

// Called under the progress reporter lock; elapsed values come from its monotonic stopwatch.
internal sealed class IndexTimings
{
    private readonly List<(IndexStage Stage, TimeSpan Duration)> finished = [];
    private IndexStage? current;
    private TimeSpan started;

    public void Report(IndexStage stage, TimeSpan elapsed)
    {
        if (stage == current)
            return;
        if (current is { } previous)
        {
            finished.Add((previous, elapsed - started));
            started = elapsed;
        }
        // Attribute command setup before the first progress event to the first stage.
        current = stage;
    }

    public TimingSnapshot Snapshot(TimeSpan elapsed)
    {
        var stages = finished.ToList();
        if (current is { } stage)
            stages.Add((stage, elapsed - started));
        return new(Math.Round(elapsed.TotalSeconds, 3), stages.Select(s => new StageTiming(
            JsonNamingPolicy.CamelCase.ConvertName(s.Stage.ToString()),
            Math.Round(s.Duration.TotalSeconds, 3))).ToArray());
    }
}

internal static class TimingReport
{
    public static async Task<string> SaveAsync(string solution, string indexId, TimingSnapshot timings,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution))!, ".codeatlas");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "timings.json");
        var temporary = Path.Combine(directory, $"timings-{Guid.NewGuid():N}.tmp");
        var report = new
        {
            solution = Path.GetFullPath(solution), indexId, toolVersion = CodeAtlasVersion.Current,
            measuredAtUtc = DateTimeOffset.UtcNow,
            scope = "Wall-clock time through index, overview, and warning-report saving; excludes timing-report publication and final console output.",
            timings.ElapsedSeconds, timings.Stages
        };
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
            }), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return path;
    }
}
