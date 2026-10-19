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
        return new(Math.Round(elapsed.TotalSeconds, 3), stages.GroupBy(s => s.Stage).Select(group => new StageTiming(
            JsonNamingPolicy.CamelCase.ConvertName(group.Key.ToString()),
            Math.Round(group.Sum(s => s.Duration.TotalSeconds), 3))).ToArray());
    }
}

internal static class TimingReport
{
    public static Task WriteAsync(Stream stream, string solution, string indexId, TimingSnapshot timings,
        CancellationToken cancellationToken) => JsonSerializer.SerializeAsync(stream, new
        {
            solution = Path.GetFullPath(solution), indexId, toolVersion = CodeAtlasVersion.Current,
            measuredAtUtc = DateTimeOffset.UtcNow,
            scope = "Wall-clock time through immutable index, overview, and warning staging; excludes timing staging, final input verification, manifest commit, and console output.",
            timings.ElapsedSeconds, timings.Stages
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }, cancellationToken);

}
