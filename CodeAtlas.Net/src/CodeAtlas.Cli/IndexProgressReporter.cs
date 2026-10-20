using System.Diagnostics;

namespace CodeAtlas.Cli;

internal sealed class IndexProgressReporter : IProgress<IndexProgress>, IDisposable
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly IndexTimings timings = new();
    private readonly bool redirected = Console.IsErrorRedirected;
    private readonly Timer timer;
    private IndexProgress? current;
    private TimeSpan lastPrinted;
    private int previousWidth;
    private bool disposed;

    public IndexProgressReporter() => timer = new Timer(_ => Tick(), null, 1000, 1000);
    public double ElapsedSeconds => Math.Round(clock.Elapsed.TotalSeconds, 3);

    public TimingSnapshot SnapshotTimings()
    {
        lock (gate)
            return timings.Snapshot(clock.Elapsed);
    }

    public void Report(IndexProgress value)
    {
        lock (gate)
        {
            if (disposed)
                return;
            var changedStage = current?.Stage != value.Stage;
            timings.Report(value.Stage, clock.Elapsed);
            current = value;
            if (changedStage || value.Total is { } total && value.Completed == total)
                Render();
        }
    }

    private void Tick()
    {
        lock (gate)
        {
            if (!disposed && current is not null
                && clock.Elapsed - lastPrinted >= TimeSpan.FromSeconds(redirected ? 10 : 1))
                Render();
        }
    }

    private void Render()
    {
        if (current is null)
            return;
        var stage = current.Stage switch
        {
            IndexStage.ScanningInputs => "Scanning inputs",
            IndexStage.LoadingProjects => "Loading projects",
            IndexStage.ValidatingProjects => "Checking project compilations",
            IndexStage.FingerprintingInputs => "Fingerprinting inputs",
            IndexStage.DiscoveringSymbols => "Discovering methods",
            IndexStage.FindingReferences => "Indexing method references",
            IndexStage.CollectingTypes => "Collecting types",
            IndexStage.CollectingDocumentation => "Collecting symbol documentation",
            IndexStage.VerifyingInputs => "Verifying unchanged inputs",
            IndexStage.Saving => "Saving index and reports",
            IndexStage.Completed => "Indexing completed",
            _ => current.Stage.ToString()
        };
        var elapsed = clock.Elapsed;
        var line = FormattableString.Invariant($"[{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}] {stage}");
        if (current.Total is { } total)
            line += FormattableString.Invariant($" {current.Completed:N0} / {total:N0}");
        if (current.Detail is { } detail)
            line += " — " + new string(detail.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        try
        {
            if (redirected)
                Console.Error.WriteLine(line);
            else
            {
                var width = Math.Max(1, Console.WindowWidth - 1);
                if (line.Length > width)
                    line = line[..width];
                Console.Error.Write("\r" + line.PadRight(Math.Min(previousWidth, width)));
                previousWidth = line.Length;
            }
        }
        catch (IOException)
        {
            // A closed progress stream must not terminate analysis from the timer callback.
        }
        lastPrinted = clock.Elapsed;
    }

    public void Complete()
    {
        lock (gate)
        {
            if (disposed)
                return;
            clock.Stop();
            current = new(IndexStage.Completed);
            Render();
            Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            timer.Dispose();
            clock.Stop();
            if (!redirected && current is not null)
            {
                try { Console.Error.WriteLine(); }
                catch (IOException) { }
            }
        }
    }
}
