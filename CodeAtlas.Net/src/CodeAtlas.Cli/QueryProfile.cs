using System.Diagnostics;

namespace CodeAtlas.Cli;

/// <summary>Per-query phase measurements are opt-in and never change lookup behavior.</summary>
internal sealed class QueryProfile
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly Dictionary<string, double> phases = new(StringComparer.Ordinal);

    public QueryProfile(DateTimeOffset managedEntryUtc)
    {
        using var process = Process.GetCurrentProcess();
        StartupSeconds = Math.Max(0, (managedEntryUtc - process.StartTime.ToUniversalTime()).TotalSeconds);
    }

    public double StartupSeconds { get; }

    public async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> action)
    {
        var start = Stopwatch.GetTimestamp();
        try { return await action(); }
        finally { Record(phase, Stopwatch.GetElapsedTime(start)); }
    }

    public T Measure<T>(string phase, Func<T> action)
    {
        var start = Stopwatch.GetTimestamp();
        try { return action(); }
        finally { Record(phase, Stopwatch.GetElapsedTime(start)); }
    }

    private void Record(string phase, TimeSpan duration) =>
        phases[phase] = phases.GetValueOrDefault(phase) + duration.TotalSeconds;

    public object Snapshot() => new
    {
        processStartupSeconds = Math.Round(StartupSeconds, 6),
        processStartupMeasurement = "OS process creation to managed entry; approximate wall-clock measurement",
        managedElapsedSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 6),
        phases = phases.ToDictionary(p => p.Key, p => Math.Round(p.Value, 6), StringComparer.Ordinal)
    };
}
